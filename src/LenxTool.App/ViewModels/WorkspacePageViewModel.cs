using System.IO;
using System.Net.Http;
using System.Threading;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Core.Updates;

namespace LenxTool.App.ViewModels;

public sealed record WorkspaceFeature(string Kicker, string Title, string Description, string Status);

public class WorkspacePageViewModel(
    string title,
    string subtitle,
    string primaryAction,
    IReadOnlyList<WorkspaceFeature> features) : PageViewModel(title, subtitle)
{
    public string PrimaryAction { get; } = primaryAction;
    public IReadOnlyList<WorkspaceFeature> Features { get; } = features;
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly IThemeService _themeService;
    private readonly IUpdateService _updateService;
    private readonly ISecretStore _secretStore;
    private readonly IAppSettingsRepository _settings;
    private readonly IAccountSessionService _accountSession;
    private readonly IFeedCatalogSyncService _catalogSync;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly string _databaseLocation = "%LocalAppData%\\LenxTool\\Data\\lenx.db";
    private readonly string _secretStorage = "Windows DPAPI · 当前用户";
    private readonly string _updateChannel = "稳定版 · GitHub Releases";
    private bool _isDarkMode;
    private bool _reduceMotion;
    private string _updateStatus = "启动后会在后台检查，更新包必须通过 SHA-256 和发布签名校验。";
    private double _updateProgress;
    private UpdateCandidate? _candidate;
    private string _groqKeyInput = string.Empty;
    private string _deepSeekKeyInput = string.Empty;
    private string _secretStatus = "密钥仅以 Windows DPAPI 加密保存在当前用户目录。";
    private string _appearanceStatus = "外观设置保存在本地数据库。";
    private string _accountUsernameInput = string.Empty;
    private string _accountPasswordInput = string.Empty;
    private string _accountStatus = "云服务未登录；本地功能仍可使用。";
    private string _catalogSyncStatus = "共享目录尚未同步。";
    private AccountSessionSnapshot _account = AccountSessionSnapshot.SignedOut;

    public SettingsViewModel(
        IThemeService themeService,
        IUpdateService updateService,
        ISecretStore secretStore,
        IAppSettingsRepository settings,
        IAccountSessionService accountSession,
        IFeedCatalogSyncService catalogSync,
        IDatabaseMaintenanceService? databaseMaintenance = null,
        IntegrationSettingsViewModel? integrationSettings = null,
        ObsidianSettingsViewModel? obsidianSettings = null,
        EagleSettingsViewModel? eagleSettings = null,
        ZoteroSettingsViewModel? zoteroSettings = null)
        : base("设置", "外观、服务凭据、数据与更新")
    {
        _themeService = themeService;
        _updateService = updateService;
        _secretStore = secretStore;
        _settings = settings;
        _accountSession = accountSession;
        _catalogSync = catalogSync;
        _databaseMaintenance = databaseMaintenance;
        IntegrationSettings = integrationSettings;
        ObsidianSettings = obsidianSettings;
        EagleSettings = eagleSettings;
        ZoteroSettings = zoteroSettings;
        _synchronizationContext = SynchronizationContext.Current;
        CheckForUpdatesCommand = new(CheckForUpdatesAsync);
        DownloadUpdateCommand = new(DownloadUpdateAsync, () => _candidate is not null);
        SaveSecretsCommand = new(SaveSecretsAsync, CanSaveSecrets);
        DeleteSecretsCommand = new(DeleteSecretsAsync);
        SaveAppearanceCommand = new(SaveAppearanceAsync);
        LoginCommand = new(LoginAsync, CanLogin);
        LogoutCommand = new(LogoutAsync, () => IsSignedIn);
        RefreshAccountCommand = new(RefreshAccountAsync, () => IsSignedIn);
        ConfigureStorageMaintenance();
        _accountSession.SessionChanged += OnAccountSessionChanged;
        _catalogSync.StatusChanged += OnCatalogSyncStatusChanged;
        ApplyAccountSession(_accountSession.Current);
        ApplyCatalogSyncStatus(_catalogSync.Current);
    }

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            if (SetProperty(ref _isDarkMode, value))
            {
                _themeService.ApplyTheme(value);
                AppearanceStatus = "外观设置已更改，点击“保存外观”后重启仍会保留。";
            }
        }
    }

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (SetProperty(ref _reduceMotion, value))
            {
                _themeService.ApplyReduceMotion(value);
                AppearanceStatus = "外观设置已更改，点击“保存外观”后重启仍会保留。";
            }
        }
    }

    public string DatabaseLocation => _databaseLocation;
    public string SecretStorage => _secretStorage;
    public string UpdateChannel => _updateChannel;
    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }
    public double UpdateProgress
    {
        get => _updateProgress;
        private set => SetProperty(ref _updateProgress, value);
    }
    public string ReleaseNotes => _candidate?.Release.ReleaseNotes ?? string.Empty;
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand DownloadUpdateCommand { get; }
    public AsyncRelayCommand SaveSecretsCommand { get; }
    public AsyncRelayCommand DeleteSecretsCommand { get; }
    public AsyncRelayCommand SaveAppearanceCommand { get; }
    public AsyncRelayCommand LoginCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }
    public AsyncRelayCommand RefreshAccountCommand { get; }
    public IntegrationSettingsViewModel? IntegrationSettings { get; }
    public ObsidianSettingsViewModel? ObsidianSettings { get; }
    public EagleSettingsViewModel? EagleSettings { get; }
    public ZoteroSettingsViewModel? ZoteroSettings { get; }
    public string AppearanceStatus
    {
        get => _appearanceStatus;
        private set => SetProperty(ref _appearanceStatus, value);
    }
    public string GroqKeyInput
    {
        get => _groqKeyInput;
        set
        {
            if (SetProperty(ref _groqKeyInput, value ?? string.Empty))
                SaveSecretsCommand.NotifyCanExecuteChanged();
        }
    }
    public string DeepSeekKeyInput
    {
        get => _deepSeekKeyInput;
        set
        {
            if (SetProperty(ref _deepSeekKeyInput, value ?? string.Empty))
                SaveSecretsCommand.NotifyCanExecuteChanged();
        }
    }
    public string SecretStatus
    {
        get => _secretStatus;
        private set => SetProperty(ref _secretStatus, value);
    }
    public string AccountUsernameInput
    {
        get => _accountUsernameInput;
        set
        {
            if (SetProperty(ref _accountUsernameInput, value ?? string.Empty))
                LoginCommand.NotifyCanExecuteChanged();
        }
    }
    public string AccountPasswordInput
    {
        get => _accountPasswordInput;
        set
        {
            if (SetProperty(ref _accountPasswordInput, value ?? string.Empty))
                LoginCommand.NotifyCanExecuteChanged();
        }
    }
    public string AccountStatus
    {
        get => _accountStatus;
        private set => SetProperty(ref _accountStatus, value);
    }
    public string CatalogSyncStatus
    {
        get => _catalogSyncStatus;
        private set => SetProperty(ref _catalogSyncStatus, value);
    }
    public bool IsSignedIn => _account.IsAuthenticated;
    public bool IsSignedOut => !IsSignedIn;
    public bool IsAdmin => _account.IsAdmin;
    public string AccountIdentity => _account.User is null
        ? "未登录"
        : $"{_account.User.Username} · {(_account.User.Role == AccountRole.Admin ? "管理员" : "普通用户")}";
    public string AccountQuotaSummary => _account.Quota is null
        ? "登录后显示共享额度"
        : $"AI {_account.Quota.Ai.Remaining}/{_account.Quota.Ai.Limit}" +
          $" · 语音 {_account.Quota.SpeechSeconds.Remaining}/{_account.Quota.SpeechSeconds.Limit} 秒" +
          $" · {_account.Quota.Date:yyyy-MM-dd} UTC";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsDarkMode = bool.TryParse(await _settings.GetAsync("appearance.dark_mode", cancellationToken), out bool dark) && dark;
        ReduceMotion = bool.TryParse(await _settings.GetAsync("appearance.reduce_motion", cancellationToken), out bool reduce) && reduce;
        AppearanceStatus = "外观设置已从本地恢复。";
        await RefreshSecretStatusAsync(cancellationToken);
        try
        {
            await _accountSession.InitializeAsync(cancellationToken);
            ApplyAccountSession(_accountSession.Current);
        }
        catch (AppException exception)
        {
            ApplyAccountSession(_accountSession.Current);
            AccountStatus = $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ApplyAccountSession(_accountSession.Current);
            AccountStatus = "账号令牌文件暂时无法读取；请检查当前 Windows 用户的目录权限。";
        }

        await _catalogSync.InitializeAsync(cancellationToken);
        ApplyCatalogSyncStatus(_catalogSync.Current);
        if (IntegrationSettings is not null)
        {
            await IntegrationSettings.InitializeAsync(
                cancellationToken);
        }
        if (ObsidianSettings is not null)
        {
            await ObsidianSettings.InitializeAsync(cancellationToken);
        }
        if (EagleSettings is not null)
        {
            await EagleSettings.InitializeAsync(cancellationToken);
        }
        if (ZoteroSettings is not null)
        {
            await ZoteroSettings.InitializeAsync(cancellationToken);
        }
    }

    private async Task SaveAppearanceAsync(CancellationToken cancellationToken)
    {
        await _settings.SetAsync("appearance.dark_mode", IsDarkMode.ToString(), cancellationToken);
        await _settings.SetAsync("appearance.reduce_motion", ReduceMotion.ToString(), cancellationToken);
        AppearanceStatus = "外观设置已保存，重启后会自动恢复。";
    }

    public async Task CheckInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckForUpdatesAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        UpdateStatus = "正在验证更新清单…";
        try
        {
            _candidate = await _updateService.CheckAsync(cancellationToken);
            UpdateStatus = _candidate is null
                ? "当前已是最新版本。"
                : $"发现 {_candidate.Release.Version} · {FormatSize(_candidate.Release.Size)}" +
                  (_candidate.IsMandatory ? " · 必须安装的安全更新" : string.Empty);
            OnPropertyChanged(nameof(ReleaseNotes));
            DownloadUpdateCommand.NotifyCanExecuteChanged();
        }
        catch (AppException exception)
        {
            UpdateStatus = $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
        catch (HttpRequestException)
        {
            UpdateStatus = "当前离线，已跳过更新检查；不会影响本地功能。";
        }
    }

    private async Task DownloadUpdateAsync(CancellationToken cancellationToken)
    {
        if (_candidate is null) return;
        var progress = new Progress<double>(value =>
        {
            UpdateProgress = value;
            UpdateStatus = $"正在下载并校验更新… {value:0}%";
        });
        string installer = await _updateService.DownloadAsync(_candidate, progress, cancellationToken);
        UpdateStatus = "更新已验证，即将静默覆盖安装并重新启动。";
        _updateService.LaunchInstallerAndExit(installer);
    }

    private async Task SaveSecretsAsync(CancellationToken cancellationToken)
    {
        if (!CanSaveSecrets()) return;
        SecretStatus = "正在使用 Windows DPAPI 加密保存…";
        try
        {
            if (!string.IsNullOrWhiteSpace(GroqKeyInput))
                await _secretStore.SetAsync("groq_api_key", GroqKeyInput.Trim(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(DeepSeekKeyInput))
                await _secretStore.SetAsync("deepseek_api_key", DeepSeekKeyInput.Trim(), cancellationToken);
            GroqKeyInput = string.Empty;
            DeepSeekKeyInput = string.Empty;
            await RefreshSecretStatusAsync(cancellationToken, "已加密保存");
        }
        catch (AppException exception)
        {
            SecretStatus = $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
        catch (UnauthorizedAccessException)
        {
            SecretStatus = "保存失败：当前 Windows 用户无权写入密钥目录。";
        }
        catch (IOException)
        {
            SecretStatus = "保存失败：密钥文件暂时无法写入，请稍后重试。";
        }
    }

    private async Task DeleteSecretsAsync(CancellationToken cancellationToken)
    {
        await _secretStore.DeleteAsync("groq_api_key", cancellationToken);
        await _secretStore.DeleteAsync("deepseek_api_key", cancellationToken);
        GroqKeyInput = string.Empty;
        DeepSeekKeyInput = string.Empty;
        await RefreshSecretStatusAsync(cancellationToken, "已清除");
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        if (!CanLogin()) return;
        AccountStatus = "正在安全登录…";
        try
        {
            await _accountSession.LoginAsync(
                AccountUsernameInput,
                AccountPasswordInput,
                cancellationToken);
            ApplyAccountSession(_accountSession.Current);
        }
        catch (AppException exception)
        {
            AccountStatus = $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ApplyAccountSession(_accountSession.Current);
            AccountStatus = "登录未完成：账号令牌无法安全写入当前 Windows 用户目录。";
        }
        finally
        {
            AccountPasswordInput = string.Empty;
        }
    }

    private async Task LogoutAsync(CancellationToken cancellationToken)
    {
        AccountStatus = "正在退出云服务…";
        try
        {
            await _accountSession.LogoutAsync(cancellationToken);
            ApplyAccountSession(_accountSession.Current);
            AccountStatus = "已退出云服务；本地数据和离线功能不受影响。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ApplyAccountSession(_accountSession.Current);
            AccountStatus = "内存会话已清除，但加密令牌文件无法更新；请检查目录权限。";
        }
    }

    private async Task RefreshAccountAsync(CancellationToken cancellationToken)
    {
        AccountStatus = "正在刷新账号与额度…";
        try
        {
            await _accountSession.RefreshAsync(cancellationToken);
            ApplyAccountSession(_accountSession.Current);
        }
        catch (AppException exception)
        {
            ApplyAccountSession(_accountSession.Current);
            AccountStatus = $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ApplyAccountSession(_accountSession.Current);
            AccountStatus = "额度刷新未完成：轮换后的账号令牌无法安全保存。";
        }
    }

    private bool CanLogin() =>
        _accountSession.IsConfigured
        && IsSignedOut
        && !string.IsNullOrWhiteSpace(AccountUsernameInput)
        && !string.IsNullOrEmpty(AccountPasswordInput);

    private void OnAccountSessionChanged(object? sender, AccountSessionChangedEventArgs eventArgs)
    {
        if (_synchronizationContext is not null && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(_ => ApplyAccountSession(eventArgs.Session), null);
            return;
        }
        ApplyAccountSession(eventArgs.Session);
    }

    private void OnCatalogSyncStatusChanged(
        object? sender,
        FeedCatalogSyncStatusChangedEventArgs eventArgs)
    {
        if (_synchronizationContext is not null && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(_ => ApplyCatalogSyncStatus(eventArgs.Status), null);
            return;
        }
        ApplyCatalogSyncStatus(eventArgs.Status);
    }

    private void ApplyCatalogSyncStatus(FeedCatalogSyncStatus status)
    {
        if (status.IsSynchronizing)
        {
            CatalogSyncStatus = "共享目录正在同步…";
            return;
        }
        if (status.LastSynchronizedAt is null)
        {
            CatalogSyncStatus = status.Error is null
                ? "共享目录尚未同步。登录后将自动获取。"
                : "共享目录暂时无法同步；稍后会自动重试。";
            return;
        }

        string freshness = status.IsStale ? "已过期，继续使用本地版本" : "已是最新";
        CatalogSyncStatus = $"共享目录 v{status.Version} · 上次同步 " +
            $"{status.LastSynchronizedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm} · {freshness}";
    }

    private void ApplyAccountSession(AccountSessionSnapshot session)
    {
        _account = session;
        AccountStatus = session.Status switch
        {
            AccountSessionStatus.SignedIn => "云服务已登录；角色与额度来自 Worker。",
            AccountSessionStatus.Expired => "登录已过期，请重新输入账号和密码。",
            _ when !_accountSession.IsConfigured =>
                "未配置云服务地址；设置 LENXTOOL_WORKER_BASE_URL 后重启即可登录。",
            _ => "云服务未登录；本地功能仍可使用。"
        };
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(IsAdmin));
        OnPropertyChanged(nameof(AccountIdentity));
        OnPropertyChanged(nameof(AccountQuotaSummary));
        LoginCommand.NotifyCanExecuteChanged();
        LogoutCommand.NotifyCanExecuteChanged();
        RefreshAccountCommand.NotifyCanExecuteChanged();
    }

    private bool CanSaveSecrets() =>
        !string.IsNullOrWhiteSpace(GroqKeyInput)
        || !string.IsNullOrWhiteSpace(DeepSeekKeyInput);

    private async Task RefreshSecretStatusAsync(
        CancellationToken cancellationToken,
        string? suffix = null)
    {
        bool hasGroq = !string.IsNullOrWhiteSpace(
            await _secretStore.GetAsync("groq_api_key", cancellationToken));
        bool hasDeepSeek = !string.IsNullOrWhiteSpace(
            await _secretStore.GetAsync("deepseek_api_key", cancellationToken));
        SecretStatus = $"Groq：{(hasGroq ? "已配置" : "未配置")}" +
            $" · DeepSeek：{(hasDeepSeek ? "已配置" : "未配置")}" +
            (suffix is null ? string.Empty : $" · {suffix}");
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB"
        : $"{bytes / 1024d:0.0} KB";
}
