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
    string allowedHostsText)
    : ObservableObject
{
    private bool _isEnabled = isEnabled;
    private string _allowedHostsText = allowedHostsText;

    public EntryIntegrationKind Kind { get; } = kind;
    public string Label { get; } = label;

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
            value ?? string.Empty);
    }
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
        RefreshCommand = new(RefreshAsync, CanOperate);
        PublishCommand = new(PublishAsync, CanOperate);
        _accountSession.SessionChanged += OnSessionChanged;
        ApplySession(_accountSession.Current);
    }

    public ObservableCollection<IntegrationPolicyEditorItem>
        Policies { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PublishCommand { get; }
    public bool IsAdmin => _isAdmin;
    public long Version
    {
        get => _version;
        private set => SetProperty(ref _version, value);
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
                snapshot.Policies);
            Status =
                $"策略集 v{Version} 已加载；当前启用 {Policies.Count(item => item.IsEnabled)} 项。";
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
            ApplySnapshot(result.Version, result.Policies);
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
                SplitHosts(item.AllowedHostsText)))
            .ToArray();
        IReadOnlyList<EntryIntegrationPolicy> normalized =
            EntryIntegrationPolicyValidator
                .ValidateAndNormalizeSet(values);
        return normalized
            .Select(policy => new EntryIntegrationPolicyInput(
                policy.Kind,
                policy.IsEnabled,
                policy.AllowedHosts))
            .ToArray();
    }

    private void ApplySnapshot(
        long version,
        IReadOnlyList<EntryIntegrationPolicy> policies)
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
                        policy.AllowedHosts)));
        }
        Version = version;
    }

    private static string[] SplitHosts(string value) =>
        value.Split(
                ['\r', '\n', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(host => host.Length > 0)
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
        Status = "当前账号没有共享集成策略管理权限。";
    }

    private bool CanOperate() => IsAdmin && !IsBusy;

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        PublishCommand.NotifyCanExecuteChanged();
    }
}
