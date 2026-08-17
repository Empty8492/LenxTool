using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Exports;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

/// <summary>
/// 单项共享策略草稿；只包含类型开关和精确主机文本，不承载个人凭据。
/// </summary>
public sealed class IntegrationPolicyEditorItem(
    EntryIntegrationKind kind,
    string label,
    bool isEnabled,
    string allowedHostsText,
    string trustedPrivateEndpointsText = "",
    string allowedResourcesText = "",
    string allowedLoopbackHttpPortsText = "")
    : ObservableObject
{
    private bool _isEnabled = isEnabled;
    private string _allowedHostsText = RequiresAllowedHostsFor(kind)
        ? allowedHostsText
        : string.Empty;
    private string _trustedPrivateEndpointsText =
        SupportsTrustedPrivateEndpointsFor(kind)
            ? trustedPrivateEndpointsText
            : string.Empty;
    private string _allowedResourcesText = SupportsResourcesFor(kind)
        ? allowedResourcesText
        : string.Empty;
    private string _allowedLoopbackHttpPortsText =
        kind == EntryIntegrationKind.QBittorrent
            ? allowedLoopbackHttpPortsText
            : string.Empty;

    public EntryIntegrationKind Kind { get; } = kind;
    public string Label { get; } = label;
    public bool RequiresAllowedHosts { get; } =
        RequiresAllowedHostsFor(kind);
    public bool SupportsTrustedPrivateEndpoints { get; } =
        SupportsTrustedPrivateEndpointsFor(kind);
    public bool SupportsResources { get; } = SupportsResourcesFor(kind);
    public bool SupportsLoopbackHttpPorts { get; } =
        kind == EntryIntegrationKind.QBittorrent;
    public string HostGuidance => RequiresAllowedHosts
        ? "公网 HTTPS：每行填写一个精确 DNS 主机；未填写时可由受信私网或 qBittorrent loopback 目标满足。"
        : "本机集成：目标只保存在客户端，此处必须留空。";
    public string ResourceGuidance => Kind switch
    {
        EntryIntegrationKind.Outline =>
            "每行一个允许的 Outline collection UUID。",
        EntryIntegrationKind.QBittorrent =>
            "每行一个允许的 qBittorrent 分类；不允许未分类投递。",
        _ => string.Empty
    };

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string AllowedHostsText
    {
        get => _allowedHostsText;
        set => SetProperty(
            ref _allowedHostsText,
            RequiresAllowedHosts
                ? value ?? string.Empty
                : string.Empty);
    }

    public string TrustedPrivateEndpointsText
    {
        get => _trustedPrivateEndpointsText;
        set => SetProperty(
            ref _trustedPrivateEndpointsText,
            SupportsTrustedPrivateEndpoints
                ? value ?? string.Empty
                : string.Empty);
    }

    public string AllowedResourcesText
    {
        get => _allowedResourcesText;
        set => SetProperty(
            ref _allowedResourcesText,
            SupportsResources ? value ?? string.Empty : string.Empty);
    }

    public string AllowedLoopbackHttpPortsText
    {
        get => _allowedLoopbackHttpPortsText;
        set => SetProperty(
            ref _allowedLoopbackHttpPortsText,
            SupportsLoopbackHttpPorts
                ? value ?? string.Empty
                : string.Empty);
    }

    private static bool RequiresAllowedHostsFor(
        EntryIntegrationKind value) =>
        value is not (
            EntryIntegrationKind.Obsidian
            or EntryIntegrationKind.Eagle);

    private static bool SupportsTrustedPrivateEndpointsFor(
        EntryIntegrationKind value) =>
        value is (
            EntryIntegrationKind.Readeck
            or EntryIntegrationKind.Outline
            or EntryIntegrationKind.QBittorrent
            or EntryIntegrationKind.Webhook);

    private static bool SupportsResourcesFor(
        EntryIntegrationKind value) =>
        value is (
            EntryIntegrationKind.Outline
            or EntryIntegrationKind.QBittorrent);
}

/// <summary>
/// 管理员发布共享集成类型和精确主机白名单；降权后立即清空 ALL 快照与草稿。
/// </summary>
public sealed class IntegrationAdminViewModel : PageViewModel
{
    private readonly IEntryIntegrationPolicyService _policies;
    private readonly IAccountSessionService _accountSession;
    private readonly SynchronizationContext? _synchronizationContext;
    private long _version;
    private int _policySchemaVersion;
    private bool _isAdmin;
    private bool _isBusy;
    private string _status = "正在读取共享集成策略…";

    public IntegrationAdminViewModel(
        IEntryIntegrationPolicyService policies,
        IAccountSessionService accountSession)
        : base(
            "外部集成策略",
            "管理员控制可用类型和精确目标主机；个人凭据不会上传")
    {
        _policies = policies;
        _accountSession = accountSession;
        _synchronizationContext = SynchronizationContext.Current;
        Policies = [];
        RefreshCommand = new(RefreshAsync, CanRefresh);
        PublishCommand = new(PublishAsync, CanPublish);
        _accountSession.SessionChanged += OnSessionChanged;
        ApplySession(_accountSession.Current);
    }

    public ObservableCollection<IntegrationPolicyEditorItem>
        Policies
    { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PublishCommand { get; }
    public bool IsAdmin => _isAdmin;
    public long Version
    {
        get => _version;
        private set => SetProperty(ref _version, value);
    }
    public int PolicySchemaVersion
    {
        get => _policySchemaVersion;
        private set => SetProperty(ref _policySchemaVersion, value);
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommands();
            }
        }
    }
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken)
    {
        ApplySession(_accountSession.Current);
        if (!IsAdmin)
        {
            Status = "需要管理员账号才能读取或发布共享集成策略。";
            return;
        }
        await LoadAsync(cancellationToken);
    }

    private async Task RefreshAsync(
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await LoadAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            EntryIntegrationPolicySnapshot snapshot =
                await _policies.GetAsync(
                    EntryIntegrationPolicyScope.All,
                    cancellationToken);
            if (!IsAdmin) return;
            ApplySnapshot(
                snapshot.Version,
                snapshot.Policies,
                snapshot.PolicySchemaVersion);
            Status = PolicySchemaVersion == 2
                ? $"策略集 v{Version} 已加载；当前启用 {Policies.Count(item => item.IsEnabled)} 项。"
                : "Worker 仍使用集成策略 schema v1；当前页面只读，请先升级 Worker。";
        }
        catch (AppException exception)
        {
            Status =
                $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
    }

    private async Task PublishAsync(
        CancellationToken cancellationToken)
    {
        if (!CanPublish()) return;
        IReadOnlyList<EntryIntegrationPolicyInput> inputs;
        try
        {
            inputs = BuildInputs();
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
            return;
        }

        IsBusy = true;
        try
        {
            EntryIntegrationPolicyMutationResult result =
                await _policies.ReplaceAsync(
                    inputs,
                    Version,
                    cancellationToken);
            if (!IsAdmin) return;
            ApplySnapshot(
                result.Version,
                result.Policies,
                result.PolicySchemaVersion);
            Status =
                $"策略集 v{Version} 已发布；普通用户只能读取其中已启用的类型。";
        }
        catch (AppException exception)
        {
            Status =
                $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private EntryIntegrationPolicyInput[] BuildInputs()
    {
        EntryIntegrationPolicyInput[] values = Policies
            .Select(item => new EntryIntegrationPolicyInput(
                item.Kind,
                item.IsEnabled,
                SplitHosts(item.AllowedHostsText))
            {
                TrustedPrivateEndpoints =
                    SplitPrivateEndpoints(
                        item.TrustedPrivateEndpointsText),
                AllowedResources = SplitLines(
                    item.AllowedResourcesText),
                AllowedLoopbackHttpPorts = SplitPorts(
                    item.AllowedLoopbackHttpPortsText)
            })
            .ToArray();
        IReadOnlyList<EntryIntegrationPolicy> normalized =
            EntryIntegrationPolicyValidator
                .ValidateAndNormalizeSet(values);
        return normalized
            .Select(policy => new EntryIntegrationPolicyInput(
                policy.Kind,
                policy.IsEnabled,
                policy.AllowedHosts)
            {
                TrustedPrivateEndpoints =
                    policy.TrustedPrivateEndpoints,
                AllowedResources = policy.AllowedResources,
                AllowedLoopbackHttpPorts =
                    policy.AllowedLoopbackHttpPorts
            })
            .ToArray();
    }

    private void ApplySnapshot(
        long version,
        IReadOnlyList<EntryIntegrationPolicy> policies,
        int policySchemaVersion)
    {
        Dictionary<EntryIntegrationKind, EntryIntegrationPolicy>
            byKind = policies.ToDictionary(policy => policy.Kind);
        Policies.Clear();
        foreach (EntryIntegrationKind kind
                 in Enum.GetValues<EntryIntegrationKind>())
        {
            byKind.TryGetValue(kind, out EntryIntegrationPolicy? policy);
            Policies.Add(new(
                kind,
                IntegrationKindChoice.LabelFor(kind),
                policy?.IsEnabled ?? false,
                policy is null
                    ? string.Empty
                    : string.Join(
                        Environment.NewLine,
                        policy.AllowedHosts),
                policy is null
                    ? string.Empty
                    : string.Join(
                        Environment.NewLine,
                        policy.TrustedPrivateEndpoints.Select(
                            endpoint =>
                                $"{endpoint.Host}:{endpoint.Port}")),
                policy is null
                    ? string.Empty
                    : string.Join(
                        Environment.NewLine,
                        policy.AllowedResources),
                policy is null
                    ? string.Empty
                    : string.Join(
                        Environment.NewLine,
                        policy.AllowedLoopbackHttpPorts)));
        }
        Version = version;
        PolicySchemaVersion = policySchemaVersion;
        NotifyCommands();
    }

    private static string[] SplitHosts(string value) =>
        value.Split(
                ['\r', '\n', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(host => host.Length > 0)
            .ToArray();

    private static string[] SplitLines(string value) =>
        value.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .ToArray();

    private static EntryIntegrationPrivateEndpoint[]
        SplitPrivateEndpoints(string value) =>
        SplitLines(value)
            .Select(item =>
            {
                int separator = item.LastIndexOf(':');
                if (separator <= 0
                    || separator == item.Length - 1
                    || item[..separator].Contains(':', StringComparison.Ordinal)
                    || !int.TryParse(
                        item[(separator + 1)..],
                        out int port))
                {
                    throw new ArgumentException(
                        "受信私网目标必须按 host:port 每行填写一项。");
                }
                return new EntryIntegrationPrivateEndpoint(
                    item[..separator],
                    port);
            })
            .ToArray();

    private static int[] SplitPorts(string value) =>
        SplitLines(value)
            .Select(item => int.TryParse(item, out int port)
                ? port
                : throw new ArgumentException(
                    "qBittorrent loopback HTTP 端口必须是整数。"))
            .ToArray();

    private void OnSessionChanged(
        object? sender,
        AccountSessionChangedEventArgs eventArgs)
    {
        if (_synchronizationContext is not null
            && SynchronizationContext.Current
                != _synchronizationContext)
        {
            _synchronizationContext.Post(
                _ => ApplySession(eventArgs.Session),
                null);
            return;
        }
        ApplySession(eventArgs.Session);
    }

    private void ApplySession(AccountSessionSnapshot session)
    {
        bool isAdmin = session.IsAdmin;
        if (SetProperty(
                ref _isAdmin,
                isAdmin,
                nameof(IsAdmin)))
        {
            NotifyCommands();
        }
        if (isAdmin) return;
        Policies.Clear();
        Version = 0;
        PolicySchemaVersion = 0;
        Status = "当前账号没有共享集成策略管理权限。";
    }

    private bool CanRefresh() => IsAdmin && !IsBusy;

    private bool CanPublish() =>
        IsAdmin && !IsBusy && PolicySchemaVersion == 2;

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        PublishCommand.NotifyCanExecuteChanged();
    }
}
