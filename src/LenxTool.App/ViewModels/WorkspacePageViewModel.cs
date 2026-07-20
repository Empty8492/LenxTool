using System.Net.Http;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
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

public sealed class SettingsViewModel : PageViewModel
{
    private readonly IThemeService _themeService;
    private readonly IUpdateService _updateService;
    private readonly ISecretStore _secretStore;
    private readonly IAppSettingsRepository _settings;
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

    public SettingsViewModel(
        IThemeService themeService,
        IUpdateService updateService,
        ISecretStore secretStore,
        IAppSettingsRepository settings)
        : base("设置", "外观、服务凭据、数据与更新")
    {
        _themeService = themeService;
        _updateService = updateService;
        _secretStore = secretStore;
        _settings = settings;
        CheckForUpdatesCommand = new(CheckForUpdatesAsync);
        DownloadUpdateCommand = new(DownloadUpdateAsync, () => _candidate is not null);
        SaveSecretsCommand = new(SaveSecretsAsync);
        DeleteSecretsCommand = new(DeleteSecretsAsync);
        SaveAppearanceCommand = new(SaveAppearanceAsync);
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
    public string AppearanceStatus
    {
        get => _appearanceStatus;
        private set => SetProperty(ref _appearanceStatus, value);
    }
    public string GroqKeyInput
    {
        get => _groqKeyInput;
        set => SetProperty(ref _groqKeyInput, value ?? string.Empty);
    }
    public string DeepSeekKeyInput
    {
        get => _deepSeekKeyInput;
        set => SetProperty(ref _deepSeekKeyInput, value ?? string.Empty);
    }
    public string SecretStatus
    {
        get => _secretStatus;
        private set => SetProperty(ref _secretStatus, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsDarkMode = bool.TryParse(await _settings.GetAsync("appearance.dark_mode", cancellationToken), out bool dark) && dark;
        ReduceMotion = bool.TryParse(await _settings.GetAsync("appearance.reduce_motion", cancellationToken), out bool reduce) && reduce;
        AppearanceStatus = "外观设置已从本地恢复。";
        bool hasGroq = !string.IsNullOrWhiteSpace(await _secretStore.GetAsync("groq_api_key", cancellationToken));
        bool hasDeepSeek = !string.IsNullOrWhiteSpace(await _secretStore.GetAsync("deepseek_api_key", cancellationToken));
        SecretStatus = $"Groq：{(hasGroq ? "已配置" : "未配置")} · DeepSeek：{(hasDeepSeek ? "已配置" : "未配置")}";
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
        if (!string.IsNullOrWhiteSpace(GroqKeyInput))
            await _secretStore.SetAsync("groq_api_key", GroqKeyInput.Trim(), cancellationToken);
        if (!string.IsNullOrWhiteSpace(DeepSeekKeyInput))
            await _secretStore.SetAsync("deepseek_api_key", DeepSeekKeyInput.Trim(), cancellationToken);
        GroqKeyInput = string.Empty;
        DeepSeekKeyInput = string.Empty;
        await InitializeAsync(cancellationToken);
    }

    private async Task DeleteSecretsAsync(CancellationToken cancellationToken)
    {
        await _secretStore.DeleteAsync("groq_api_key", cancellationToken);
        await _secretStore.DeleteAsync("deepseek_api_key", cancellationToken);
        GroqKeyInput = string.Empty;
        DeepSeekKeyInput = string.Empty;
        await InitializeAsync(cancellationToken);
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB"
        : $"{bytes / 1024d:0.0} KB";
}
