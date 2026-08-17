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
        // 这里只列出已经注册生产 exporter 与连接探针的通用设置类型。
        // 其余枚举仍是共享策略/线协议的一部分，不能因为尚未接通就删除。
        [new(EntryIntegrationKind.Readwise, LabelFor(
            EntryIntegrationKind.Readwise))];

    public static IReadOnlyList<IntegrationKindChoice>
        LegacyCleanupKinds
    { get; } =
        [
            new(EntryIntegrationKind.Cubox, LabelFor(
                EntryIntegrationKind.Cubox)),
            new(EntryIntegrationKind.Readeck, LabelFor(
                EntryIntegrationKind.Readeck)),
            new(EntryIntegrationKind.Outline, LabelFor(
                EntryIntegrationKind.Outline)),
            new(EntryIntegrationKind.QBittorrent, LabelFor(
                EntryIntegrationKind.QBittorrent)),
            new(EntryIntegrationKind.Webhook, LabelFor(
                EntryIntegrationKind.Webhook))
        ];

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
/// 管理本机非敏感目标与 DPAPI 凭据；界面不显示或持有凭据明文。
/// </summary>
public sealed class IntegrationSettingsViewModel
    : ObservableObject
{
    private const string KindKey = "integration.target.kind";
    private const string TargetIdKey = "integration.target.id";
    private const string EndpointKey =
        "integration.target.endpoint";
    private const string LegacyKindKey =
        "integration.legacy.kind";
    private const string LegacyTargetIdKey =
        "integration.legacy.target.id";
    private readonly IEntryIntegrationCredentialStore _credentials;
    private readonly IEntryIntegrationHealthService _health;
    private readonly IAppSettingsRepository _settings;
    private readonly IReadOnlyList<IntegrationKindChoice> _kinds =
        IntegrationKindChoice.All;
    private readonly IReadOnlyList<IntegrationKindChoice>
        _legacyCleanupKinds = IntegrationKindChoice.LegacyCleanupKinds;
    private IntegrationKindChoice _selectedKind =
        IntegrationKindChoice.All.Single();
    private string _targetId =
        ReadwiseEntryExporter.CredentialTargetId;
    private string _endpointText =
        ReadwiseEntryExporter.ApiRoot.AbsoluteUri;
    private string _credentialInput = string.Empty;
    private bool _hasCredential;
    private bool _isSelectedKindSupported = true;
    private EntryIntegrationKind? _legacyCredentialKind;
    private string? _legacyCredentialTargetId;
    private bool _hasLegacyCredential;
    private string _legacyCredentialStatus =
        "未检测到旧版占位集成凭据。";
    private IntegrationKindChoice _selectedLegacyCleanupKind =
        IntegrationKindChoice.LegacyCleanupKinds[0];
    private string _legacyCleanupTargetId = string.Empty;
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
        DeleteLegacyCredentialCommand =
            new(DeleteLegacyCredentialAsync, () => HasLegacyCredential);
        DeleteSpecifiedLegacyCredentialCommand = new(
            DeleteSpecifiedLegacyCredentialAsync,
            CanDeleteSpecifiedLegacyCredential);
        TestCommand = new(TestAsync, CanUseTarget);
    }

    public IReadOnlyList<IntegrationKindChoice> Kinds => _kinds;
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand DeleteCredentialCommand { get; }
    public AsyncRelayCommand DeleteLegacyCredentialCommand { get; }
    public AsyncRelayCommand DeleteSpecifiedLegacyCredentialCommand { get; }
    public AsyncRelayCommand TestCommand { get; }

    public IReadOnlyList<IntegrationKindChoice> LegacyCleanupKinds =>
        _legacyCleanupKinds;

    public IntegrationKindChoice SelectedLegacyCleanupKind
    {
        get => _selectedLegacyCleanupKind;
        set
        {
            IntegrationKindChoice choice =
                IntegrationKindChoice.LegacyCleanupKinds
                    .SingleOrDefault(item => item.Kind == value?.Kind)
                ?? IntegrationKindChoice.LegacyCleanupKinds[0];
            if (!SetProperty(
                    ref _selectedLegacyCleanupKind,
                    choice))
            {
                return;
            }
            DeleteSpecifiedLegacyCredentialCommand
                .NotifyCanExecuteChanged();
        }
    }

    public string LegacyCleanupTargetId
    {
        get => _legacyCleanupTargetId;
        set
        {
            if (!SetProperty(
                    ref _legacyCleanupTargetId,
                    value ?? string.Empty))
            {
                return;
            }
            DeleteSpecifiedLegacyCredentialCommand
                .NotifyCanExecuteChanged();
        }
    }

    public IntegrationKindChoice SelectedKind
    {
        get => _selectedKind;
        set
        {
            bool isSupported = value is null
                || IntegrationKindChoice.All.Any(
                    item => item.Kind == value.Kind);
            IntegrationKindChoice supportedChoice =
                IntegrationKindChoice.All.SingleOrDefault(
                    item => item.Kind == value?.Kind)
                ?? IntegrationKindChoice.All[0];
            bool supportChanged =
                _isSelectedKindSupported != isSupported;
            _isSelectedKindSupported = isSupported;
            if (SetProperty(
                    ref _selectedKind,
                    supportedChoice))
            {
                OnPropertyChanged(nameof(IsFixedReadwiseTarget));
                TargetChanged();
            }
            if (IsFixedReadwiseTarget)
            {
                // Reader token 权限较高，生产适配器固定官方端点与默认槽位，
                // 不能沿用通用表单中的任意目标地址。
                TargetId = ReadwiseEntryExporter.CredentialTargetId;
                EndpointText = ReadwiseEntryExporter.ApiRoot.AbsoluteUri;
            }
            if (!isSupported)
            {
                Status = "该集成尚未接通，不能保存凭据或测试连接。";
            }
            if (supportChanged)
            {
                NotifyCommands();
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

    public bool HasLegacyCredential
    {
        get => _hasLegacyCredential;
        private set
        {
            if (!SetProperty(ref _hasLegacyCredential, value)) return;
            DeleteLegacyCredentialCommand.NotifyCanExecuteChanged();
        }
    }

    public string LegacyCredentialStatus
    {
        get => _legacyCredentialStatus;
        private set => SetProperty(ref _legacyCredentialStatus, value);
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
        string? savedTargetId =
            await _settings.GetAsync(TargetIdKey, cancellationToken);
        string? savedEndpoint =
            await _settings.GetAsync(EndpointKey, cancellationToken);
        await RestoreLegacyCredentialAsync(
            kindText,
            savedTargetId,
            cancellationToken);
        if (Enum.TryParse(
                kindText,
                ignoreCase: false,
                out EntryIntegrationKind kind)
            && Enum.IsDefined(kind)
            && Kinds.SingleOrDefault(item => item.Kind == kind)
                is { } selectedKind)
        {
            // 只有当前已接通的类型才能恢复；旧版本若保存过占位类型，
            // 保持安全默认值，不再把它带入凭据与连接测试流程。
            SelectedKind = selectedKind;
        }
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

    private async Task DeleteLegacyCredentialAsync(
        CancellationToken cancellationToken)
    {
        if (_legacyCredentialKind is not { } kind
            || string.IsNullOrEmpty(_legacyCredentialTargetId))
        {
            return;
        }

        string targetId = _legacyCredentialTargetId;
        await _credentials.DeleteAsync(
            kind,
            targetId,
            cancellationToken);
        await NormalizeMatchingLegacyTargetAsync(
            kind,
            targetId,
            cancellationToken);
        await ClearLegacyCredentialReferenceAsync(cancellationToken);
        _legacyCredentialKind = null;
        _legacyCredentialTargetId = null;
        HasLegacyCredential = false;
        LegacyCredentialStatus = "旧版占位集成凭据已从本机安全存储删除。";
        Status = LegacyCredentialStatus;
    }

    private async Task DeleteSpecifiedLegacyCredentialAsync(
        CancellationToken cancellationToken)
    {
        string targetId;
        try
        {
            targetId = ValidateCredentialTargetId(
                LegacyCleanupTargetId);
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
            return;
        }

        EntryIntegrationKind kind = SelectedLegacyCleanupKind.Kind;
        if (IsDedicatedDefaultTarget(kind, targetId))
        {
            Status = "生产适配器的 default 槽位请使用对应专用卡删除，避免误删当前凭据。";
            return;
        }
        await _credentials.DeleteAsync(
            kind,
            targetId,
            cancellationToken);
        await NormalizeMatchingLegacyTargetAsync(
            kind,
            targetId,
            cancellationToken);
        if (_legacyCredentialKind == kind
            && string.Equals(
                _legacyCredentialTargetId,
                targetId,
                StringComparison.Ordinal))
        {
            await ClearLegacyCredentialReferenceAsync(
                cancellationToken);
            ResetLegacyCredentialState();
        }

        LegacyCleanupTargetId = string.Empty;
        Status =
            $"旧版 {IntegrationKindChoice.LabelFor(kind)} / {targetId} 槽位已幂等删除；未保存目标或发起连接测试。";
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

    private async Task RestoreLegacyCredentialAsync(
        string? currentKindText,
        string? currentTargetId,
        CancellationToken cancellationToken)
    {
        string? storedKindText = await _settings.GetAsync(
            LegacyKindKey,
            cancellationToken);
        string? storedTargetId = await _settings.GetAsync(
            LegacyTargetIdKey,
            cancellationToken);
        bool hasStoredReference = TryCreateLegacyCredentialReference(
            storedKindText,
            storedTargetId,
            out EntryIntegrationKind kind,
            out string targetId);
        if (!hasStoredReference
            && !TryCreateLegacyCredentialReference(
                currentKindText,
                currentTargetId,
                out kind,
                out targetId))
        {
            ResetLegacyCredentialState();
            return;
        }

        if (!hasStoredReference)
        {
            // 遗留入口只保留非秘密槽位指针，不按旧槽位查询或返回凭据。
            // 当前 Readwise presence 刷新仍可能让底层共享 DPAPI blob 整体解密，
            // 这里的边界是不把旧值交给 ViewModel、探针或 exporter。
            await _settings.SetAsync(
                LegacyKindKey,
                kind.ToString(),
                cancellationToken);
            await _settings.SetAsync(
                LegacyTargetIdKey,
                targetId,
                cancellationToken);
        }
        _legacyCredentialKind = kind;
        _legacyCredentialTargetId = targetId;
        LegacyCredentialStatus =
            $"检测到旧版 {IntegrationKindChoice.LabelFor(kind)} / {targetId} 的凭据清理记录；旧值不会返回界面或交给探针/exporter，可在此显式删除。";
        HasLegacyCredential = true;
    }

    private async Task NormalizeMatchingLegacyTargetAsync(
        EntryIntegrationKind legacyKind,
        string legacyTargetId,
        CancellationToken cancellationToken)
    {
        string? currentKind = await _settings.GetAsync(
            KindKey,
            cancellationToken);
        string? currentTargetId = await _settings.GetAsync(
            TargetIdKey,
            cancellationToken);
        bool stillMatchesLegacy = string.Equals(
                currentKind,
                legacyKind.ToString(),
                StringComparison.Ordinal)
            && string.Equals(
                currentTargetId,
                legacyTargetId,
                StringComparison.Ordinal);
        bool isRetryingSafeKind = string.Equals(
            currentKind,
            EntryIntegrationKind.Readwise.ToString(),
            StringComparison.Ordinal);
        if (!stillMatchesLegacy && !isRetryingSafeKind)
        {
            return;
        }

        // 只迁移仍精确指向已删除旧槽位的配置，避免覆盖用户随后保存的新目标。
        // Kind 先写入，立即让旧适配器失效；后续设置中途失败时，保留的清理指针
        // 会让重试继续补齐 Readwise 固定目标，而不会从漂移后的旧 TargetId 重建提示。
        await _settings.SetAsync(
            KindKey,
            EntryIntegrationKind.Readwise.ToString(),
            cancellationToken);
        await _settings.SetAsync(
            TargetIdKey,
            ReadwiseEntryExporter.CredentialTargetId,
            cancellationToken);
        await _settings.SetAsync(
            EndpointKey,
            ReadwiseEntryExporter.ApiRoot.AbsoluteUri,
            cancellationToken);
    }

    private async Task ClearLegacyCredentialReferenceAsync(
        CancellationToken cancellationToken)
    {
        await _settings.SetAsync(
            LegacyKindKey,
            string.Empty,
            cancellationToken);
        await _settings.SetAsync(
            LegacyTargetIdKey,
            string.Empty,
            cancellationToken);
    }

    private void ResetLegacyCredentialState()
    {
        _legacyCredentialKind = null;
        _legacyCredentialTargetId = null;
        HasLegacyCredential = false;
        LegacyCredentialStatus = "未检测到旧版占位集成凭据。";
    }

    private static bool TryCreateLegacyCredentialReference(
        string? kindText,
        string? targetId,
        out EntryIntegrationKind kind,
        out string normalizedTargetId)
    {
        normalizedTargetId = targetId ?? string.Empty;
        if (!Enum.TryParse(
                kindText,
                ignoreCase: false,
                out kind)
            || !Enum.IsDefined(kind)
            || !IsLegacyUnwiredKind(kind)
            || normalizedTargetId.Length == 0
            || normalizedTargetId.Length > 128
            || normalizedTargetId.Any(char.IsControl)
            || !string.Equals(
                normalizedTargetId,
                normalizedTargetId.Trim(),
                StringComparison.Ordinal))
        {
            kind = default;
            normalizedTargetId = string.Empty;
            return false;
        }

        return true;
    }

    private static bool IsLegacyUnwiredKind(
        EntryIntegrationKind kind) =>
        kind is EntryIntegrationKind.Cubox;

    private static bool IsDedicatedDefaultTarget(
        EntryIntegrationKind kind,
        string targetId) =>
        kind is EntryIntegrationKind.Readeck
            or EntryIntegrationKind.Outline
            or EntryIntegrationKind.QBittorrent
            or EntryIntegrationKind.Webhook
        && string.Equals(targetId, "default", StringComparison.Ordinal);

    private string ValidateTargetId()
    {
        return ValidateCredentialTargetId(TargetId);
    }

    private static string ValidateCredentialTargetId(string? value)
    {
        string targetId = value?.Trim() ?? string.Empty;
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
        if (!_isSelectedKindSupported)
        {
            return false;
        }
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

    private bool CanDeleteSpecifiedLegacyCredential()
    {
        if (!IntegrationKindChoice.LegacyCleanupKinds.Any(
                item => item.Kind == SelectedLegacyCleanupKind.Kind))
        {
            return false;
        }
        try
        {
            string targetId = ValidateCredentialTargetId(
                LegacyCleanupTargetId);
            return !IsDedicatedDefaultTarget(
                SelectedLegacyCleanupKind.Kind,
                targetId);
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
