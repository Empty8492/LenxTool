using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.App.ViewModels;

/// <summary>
/// 为设置页提供稳定的集成类型与用户可读名称映射。
/// </summary>
public sealed record IntegrationKindChoice(
    EntryIntegrationKind Kind,
    string Label)
{
    public static IReadOnlyList<IntegrationKindChoice> All { get; } =
        Enum.GetValues<EntryIntegrationKind>()
            // Obsidian、Eagle 与 Zotero 都有提供商专用设置卡；继续放在
            // 通用 HTTPS/DPAPI 表单中只会形成无法工作的重复配置入口。
            .Where(kind => kind is not (
                EntryIntegrationKind.Obsidian
                or EntryIntegrationKind.Eagle
                or EntryIntegrationKind.Zotero))
            .Select(kind => new IntegrationKindChoice(
                kind,
                LabelFor(kind)))
            .ToArray();

    public static string LabelFor(EntryIntegrationKind kind) =>
        kind switch
        {
            EntryIntegrationKind.Obsidian => "Obsidian",
            EntryIntegrationKind.Eagle => "Eagle",
            EntryIntegrationKind.Zotero => "Zotero",
            EntryIntegrationKind.Readwise => "Readwise",
            EntryIntegrationKind.Cubox => "Cubox",
            EntryIntegrationKind.Readeck => "Readeck",
            EntryIntegrationKind.Outline => "Outline",
            EntryIntegrationKind.QBittorrent => "qBittorrent",
            EntryIntegrationKind.Webhook => "Webhook",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}

/// <summary>
/// 管理本机非敏感目标与 DPAPI 凭据；界面从不回读凭据明文。
/// </summary>
public sealed class IntegrationSettingsViewModel
    : ObservableObject
{
    private const string KindKey = "integration.target.kind";
    private const string TargetIdKey = "integration.target.id";
    private const string EndpointKey =
        "integration.target.endpoint";
    private readonly IEntryIntegrationCredentialStore _credentials;
    private readonly IEntryIntegrationHealthService _health;
    private readonly IAppSettingsRepository _settings;
    private readonly IReadOnlyList<IntegrationKindChoice> _kinds =
        IntegrationKindChoice.All;
    private IntegrationKindChoice _selectedKind =
        IntegrationKindChoice.All.Single(
            item => item.Kind == EntryIntegrationKind.Webhook);
    private string _targetId = "default";
    private string _endpointText = string.Empty;
    private string _credentialInput = string.Empty;
    private bool _hasCredential;
    private string _status =
        "凭据仅以 Windows DPAPI 加密保存在当前用户目录。";

    public IntegrationSettingsViewModel(
        IEntryIntegrationCredentialStore credentials,
        IEntryIntegrationHealthService health,
        IAppSettingsRepository settings)
    {
        _credentials = credentials;
        _health = health;
        _settings = settings;
        SaveCommand = new(SaveAsync, CanUseTarget);
        DeleteCredentialCommand =
            new(DeleteCredentialAsync, CanUseCredentialSlot);
        TestCommand = new(TestAsync, CanUseTarget);
    }

    public IReadOnlyList<IntegrationKindChoice> Kinds => _kinds;
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand DeleteCredentialCommand { get; }
    public AsyncRelayCommand TestCommand { get; }

    public IntegrationKindChoice SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (SetProperty(
                    ref _selectedKind,
                    value ?? IntegrationKindChoice.All[0]))
            {
                OnPropertyChanged(nameof(IsFixedReadwiseTarget));
                if (IsFixedReadwiseTarget)
                {
                    // Reader token 权限较高，生产适配器固定官方端点与默认槽位，
                    // 不能沿用通用表单中的任意目标地址。
                    TargetId = ReadwiseEntryExporter.CredentialTargetId;
                    EndpointText = ReadwiseEntryExporter.ApiRoot.AbsoluteUri;
                }
                TargetChanged();
            }
        }
    }

    public bool IsFixedReadwiseTarget =>
        SelectedKind.Kind == EntryIntegrationKind.Readwise;

    public string TargetId
    {
        get => _targetId;
        set
        {
            if (SetProperty(ref _targetId, value ?? string.Empty))
            {
                TargetChanged();
            }
        }
    }

    public string EndpointText
    {
        get => _endpointText;
        set
        {
            if (SetProperty(
                    ref _endpointText,
                    value ?? string.Empty))
            {
                NotifyCommands();
            }
        }
    }

    public string CredentialInput
    {
        get => _credentialInput;
        set => SetProperty(
            ref _credentialInput,
            value ?? string.Empty);
    }

    public bool HasCredential
    {
        get => _hasCredential;
        private set => SetProperty(ref _hasCredential, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken)
    {
        string? kindText =
            await _settings.GetAsync(KindKey, cancellationToken);
        if (Enum.TryParse(
                kindText,
                ignoreCase: false,
                out EntryIntegrationKind kind)
            && Enum.IsDefined(kind)
            && Kinds.SingleOrDefault(item => item.Kind == kind)
                is { } selectedKind)
        {
            // 旧版本若误存了本机导出器类型，保持默认 Webhook，不再把它
            // 带入只接受 HTTPS 与 DPAPI 凭据的通用表单。
            SelectedKind = selectedKind;
        }
        string? savedTargetId =
            await _settings.GetAsync(TargetIdKey, cancellationToken);
        string? savedEndpoint =
            await _settings.GetAsync(EndpointKey, cancellationToken);
        if (IsFixedReadwiseTarget)
        {
            TargetId = ReadwiseEntryExporter.CredentialTargetId;
            EndpointText = ReadwiseEntryExporter.ApiRoot.AbsoluteUri;
        }
        else
        {
            TargetId = savedTargetId ?? "default";
            EndpointText = savedEndpoint ?? string.Empty;
        }
        await RefreshPresenceAsync(cancellationToken);
    }

    private async Task SaveAsync(
        CancellationToken cancellationToken)
    {
        EntryIntegrationTarget target;
        try
        {
            target = BuildTarget();
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
            return;
        }

        try
        {
            await _settings.SetAsync(
                KindKey,
                SelectedKind.Kind.ToString(),
                cancellationToken);
            await _settings.SetAsync(
                TargetIdKey,
                target.TargetId,
                cancellationToken);
            await _settings.SetAsync(
                EndpointKey,
                target.Endpoint.AbsoluteUri,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(CredentialInput))
            {
                await _credentials.SetAsync(
                    target.Kind,
                    target.TargetId,
                    CredentialInput.Trim(),
                    cancellationToken);
            }
            await RefreshPresenceAsync(cancellationToken);
            Status = HasCredential
                ? "本机目标已保存，凭据已由 Windows DPAPI 加密。"
                : "本机目标已保存；尚未填写凭据。";
        }
        finally
        {
            CredentialInput = string.Empty;
        }
    }

    private async Task DeleteCredentialAsync(
        CancellationToken cancellationToken)
    {
        string targetId = ValidateTargetId();
        await _credentials.DeleteAsync(
            SelectedKind.Kind,
            targetId,
            cancellationToken);
        CredentialInput = string.Empty;
        HasCredential = false;
        Status = "当前本机目标的加密凭据已删除。";
    }

    private async Task TestAsync(
        CancellationToken cancellationToken)
    {
        EntryIntegrationTarget target;
        try
        {
            target = BuildTarget();
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
            return;
        }
        if (!await IsSavedTargetAsync(target, cancellationToken))
        {
            Status = "请先保存当前本机目标，再测试连接。";
            return;
        }
        EntryIntegrationHealthResult result =
            await _health.CheckAsync(target, cancellationToken);
        Status = result.Status switch
        {
            EntryIntegrationHealthStatus.Healthy =>
                "连接检查通过。",
            EntryIntegrationHealthStatus.PolicyDisabled =>
                "管理员尚未启用该集成或目标主机不在共享策略中。",
            EntryIntegrationHealthStatus.BlockedEndpoint =>
                "目标地址或解析结果被安全策略阻止。",
            EntryIntegrationHealthStatus.CredentialsMissing =>
                "请先保存当前本机目标的凭据。",
            EntryIntegrationHealthStatus.AdapterUnavailable =>
                "该集成的连接适配器尚未安装，因此没有发起外部请求。",
            EntryIntegrationHealthStatus.Unauthorized =>
                "提供商拒绝了凭据。",
            EntryIntegrationHealthStatus.RateLimited =>
                $"检查过于频繁，请在 {Math.Ceiling(result.RetryAfter?.TotalSeconds ?? 1)} 秒后重试。",
            EntryIntegrationHealthStatus.TimedOut =>
                "连接检查超时。",
            _ => "连接检查暂时不可用。"
        };
    }

    private EntryIntegrationTarget BuildTarget()
    {
        string targetId = ValidateTargetId();
        if (!Uri.TryCreate(
                EndpointText.Trim(),
                UriKind.Absolute,
                out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "目标地址必须是绝对 HTTPS 地址。");
        }
        if (SelectedKind.Kind == EntryIntegrationKind.Readwise)
        {
            if (!string.Equals(
                    targetId,
                    ReadwiseEntryExporter.CredentialTargetId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    endpoint.AbsoluteUri,
                    ReadwiseEntryExporter.ApiRoot.AbsoluteUri,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Readwise Reader 只允许固定目标 https://readwise.io/ 与 default 凭据槽位。");
            }
            return new(
                ReadwiseEntryExporter.CredentialTargetId,
                EntryIntegrationKind.Readwise,
                ReadwiseEntryExporter.ApiRoot);
        }
        return new(targetId, SelectedKind.Kind, endpoint);
    }

    private async Task<bool> IsSavedTargetAsync(
        EntryIntegrationTarget target,
        CancellationToken cancellationToken)
    {
        string? savedKind = await _settings.GetAsync(
            KindKey,
            cancellationToken);
        string? savedTargetId = await _settings.GetAsync(
            TargetIdKey,
            cancellationToken);
        string? savedEndpoint = await _settings.GetAsync(
            EndpointKey,
            cancellationToken);
        return string.Equals(
                savedKind,
                target.Kind.ToString(),
                StringComparison.Ordinal)
            && string.Equals(
                savedTargetId,
                target.TargetId,
                StringComparison.Ordinal)
            && Uri.TryCreate(
                savedEndpoint,
                UriKind.Absolute,
                out Uri? endpoint)
            && string.Equals(
                endpoint.AbsoluteUri,
                target.Endpoint.AbsoluteUri,
                StringComparison.OrdinalIgnoreCase);
    }

    private string ValidateTargetId()
    {
        string targetId = TargetId.Trim();
        if (targetId.Length == 0
            || targetId.Length > 128
            || targetId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "本机目标标识不能为空且不能超过 128 个字符。");
        }
        return targetId;
    }

    private async Task RefreshPresenceAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TargetId))
        {
            HasCredential = false;
            return;
        }
        HasCredential = await _credentials.ExistsAsync(
            SelectedKind.Kind,
            TargetId.Trim(),
            cancellationToken);
    }

    private void TargetChanged()
    {
        HasCredential = false;
        NotifyCommands();
    }

    private bool CanUseTarget() =>
        CanUseCredentialSlot()
        && !string.IsNullOrWhiteSpace(EndpointText);

    private bool CanUseCredentialSlot()
    {
        try
        {
            _ = ValidateTargetId();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        DeleteCredentialCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }
}
