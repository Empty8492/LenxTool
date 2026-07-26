using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using LenxTool.App.Mvvm;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed class AutomationAdminViewModel : PageViewModel
{
    private const int SimulationEntryLimit = 20;
    private readonly IFeedAutomationRuleAdminService _adminService;
    private readonly IFeedAutomationRuleSimulationService _simulationService;
    private readonly IAccountSessionService _accountSession;
    private readonly SynchronizationContext? _synchronizationContext;
    private FeedAutomationRule? _selectedRule;
    private string? _editingRuleId;
    private bool _isAdmin;
    private bool _isBusy;
    private long _ruleSetVersion;
    private string _ruleName = string.Empty;
    private int _priority = 100;
    private int _conflictOrder = 100;
    private bool _ruleIsEnabled = true;
    private AutomationMatchModeChoice _selectedMatchMode;
    private string _status = "正在读取自动化规则…";

    public AutomationAdminViewModel(
        IFeedAutomationRuleAdminService adminService,
        IFeedAutomationRuleSimulationService simulationService,
        IAccountSessionService accountSession)
        : base(
            "自动化规则",
            "用受限字段和动作创建规则；只读模拟不会执行 AI、媒体或通知")
    {
        _adminService = adminService;
        _simulationService = simulationService;
        _accountSession = accountSession;
        _synchronizationContext = SynchronizationContext.Current;
        Rules = [];
        Conditions = [];
        Actions = [];
        SimulationEntries = [];
        FieldChoices = Enum.GetValues<FeedAutomationField>()
            .Select(value => new AutomationFieldChoice(
                value,
                AutomationRuleLabels.Field(value)))
            .ToArray();
        ActionChoices = Enum.GetValues<FeedAutomationActionType>()
            .Select(value => new AutomationActionChoice(
                value,
                AutomationRuleLabels.Action(value)))
            .ToArray();
        MatchModeChoices =
        [
            new(FeedAutomationMatchMode.All, "满足全部条件"),
            new(FeedAutomationMatchMode.Any, "满足任一条件")
        ];
        TranslationLanguageChoices =
        [
            new("zh-Hans", "简体中文"),
            new("en", "英语"),
            new("ja", "日语"),
            new("ko", "韩语")
        ];
        BooleanChoices = ["true", "false"];
        _selectedMatchMode = MatchModeChoices[0];

        RefreshCommand = new(RefreshAsync, () => IsAdmin && !IsBusy);
        BeginNewRuleCommand = new(BeginNewRule, () => IsAdmin && !IsBusy);
        AddConditionCommand = new(
            AddCondition,
            () => IsAdmin
                && !IsBusy
                && Conditions.Count < FeedAutomationRuleValidator.MaximumConditionCount);
        RemoveConditionCommand = new RelayCommand<AutomationConditionEditorItem>(
            RemoveCondition,
            item => IsAdmin
                && !IsBusy
                && item is not null
                && Conditions.Count > 1);
        AddActionCommand = new(
            AddAction,
            () => IsAdmin
                && !IsBusy
                && Actions.Count < FeedAutomationRuleValidator.MaximumActionCount);
        RemoveActionCommand = new RelayCommand<AutomationActionEditorItem>(
            RemoveAction,
            item => IsAdmin
                && !IsBusy
                && item is not null
                && Actions.Count > 1);
        SimulateCommand = new(SimulateAsync, CanUseDraft);
        PublishCommand = new(PublishAsync, CanUseDraft);

        _accountSession.SessionChanged += OnSessionChanged;
        ApplySession(_accountSession.Current);
        BeginNewRule();
    }

    public ObservableCollection<FeedAutomationRule> Rules { get; }
    public ObservableCollection<AutomationConditionEditorItem> Conditions { get; }
    public ObservableCollection<AutomationActionEditorItem> Actions { get; }
    public ObservableCollection<AutomationSimulationEntryViewModel>
        SimulationEntries { get; }
    public IReadOnlyList<AutomationFieldChoice> FieldChoices { get; }
    public IReadOnlyList<AutomationActionChoice> ActionChoices { get; }
    public IReadOnlyList<AutomationMatchModeChoice> MatchModeChoices { get; }
    public IReadOnlyList<AutomationValueChoice> TranslationLanguageChoices { get; }
    public IReadOnlyList<string> BooleanChoices { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand BeginNewRuleCommand { get; }
    public RelayCommand AddConditionCommand { get; }
    public RelayCommand<AutomationConditionEditorItem> RemoveConditionCommand { get; }
    public RelayCommand AddActionCommand { get; }
    public RelayCommand<AutomationActionEditorItem> RemoveActionCommand { get; }
    public AsyncRelayCommand SimulateCommand { get; }
    public AsyncRelayCommand PublishCommand { get; }

    public bool IsAdmin => _isAdmin;
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
    public long RuleSetVersion
    {
        get => _ruleSetVersion;
        private set => SetProperty(ref _ruleSetVersion, value);
    }
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }
    public bool IsNewRule => _editingRuleId is null;
    public string EditorTitle => IsNewRule
        ? "新建规则"
        : $"编辑规则 · v{SelectedRule?.Version ?? 0}";
    public string PublishLabel => IsNewRule ? "发布新规则" : "发布新版本";

    public FeedAutomationRule? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (!SetProperty(ref _selectedRule, value) || value is null)
            {
                return;
            }
            LoadRule(value);
        }
    }

    public string RuleName
    {
        get => _ruleName;
        set
        {
            if (SetProperty(ref _ruleName, value ?? string.Empty))
            {
                DraftChanged();
            }
        }
    }

    public int Priority
    {
        get => _priority;
        set
        {
            if (SetProperty(ref _priority, value))
            {
                DraftChanged();
            }
        }
    }

    public int ConflictOrder
    {
        get => _conflictOrder;
        set
        {
            if (SetProperty(ref _conflictOrder, value))
            {
                DraftChanged();
            }
        }
    }

    public bool RuleIsEnabled
    {
        get => _ruleIsEnabled;
        set
        {
            if (SetProperty(ref _ruleIsEnabled, value))
            {
                DraftChanged();
            }
        }
    }

    public AutomationMatchModeChoice SelectedMatchMode
    {
        get => _selectedMatchMode;
        set
        {
            if (SetProperty(ref _selectedMatchMode, value ?? MatchModeChoices[0]))
            {
                DraftChanged();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ApplySession(_accountSession.Current);
        if (!IsAdmin)
        {
            Status = "需要管理员账号才能查看或发布自动化规则。";
            return;
        }
        try
        {
            await LoadRulesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (AppException exception)
        {
            Status = FormatError(exception);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await LoadRulesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (AppException exception)
        {
            Status = FormatError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadRulesAsync(CancellationToken cancellationToken)
    {
        FeedAutomationRuleSnapshot snapshot =
            await _adminService.GetAllAsync(cancellationToken)
                .ConfigureAwait(true);
        if (!IsAdmin)
        {
            return;
        }
        string? selectedId = _editingRuleId;
        Rules.Clear();
        foreach (FeedAutomationRule rule in snapshot.Rules
                     .OrderByDescending(item => item.Priority)
                     .ThenBy(item => item.ConflictOrder)
                     .ThenBy(item => item.Name, StringComparer.CurrentCulture))
        {
            Rules.Add(rule);
        }
        RuleSetVersion = snapshot.RuleSetVersion;
        FeedAutomationRule? selected = selectedId is null
            ? Rules.FirstOrDefault()
            : Rules.FirstOrDefault(rule =>
                string.Equals(rule.Id, selectedId, StringComparison.Ordinal));
        if (selected is null)
        {
            BeginNewRule();
        }
        else
        {
            SelectedRule = selected;
        }
        Status = $"规则集 v{RuleSetVersion} 已加载，共 {Rules.Count} 条。";
    }

    private void BeginNewRule()
    {
        _editingRuleId = null;
        SetProperty(ref _selectedRule, null, nameof(SelectedRule));
        _ruleName = string.Empty;
        _priority = 100;
        _conflictOrder = 100;
        _ruleIsEnabled = true;
        _selectedMatchMode = MatchModeChoices[0];
        OnPropertyChanged(nameof(RuleName));
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(ConflictOrder));
        OnPropertyChanged(nameof(RuleIsEnabled));
        OnPropertyChanged(nameof(SelectedMatchMode));
        Conditions.Clear();
        Actions.Clear();
        Conditions.Add(CreateCondition(
            FeedAutomationField.Title,
            FeedAutomationOperator.Contains,
            string.Empty));
        Actions.Add(CreateAction(FeedAutomationActionType.Notify, null));
        SimulationEntries.Clear();
        NotifyEditorState();
    }

    private void LoadRule(FeedAutomationRule rule)
    {
        _editingRuleId = rule.Id;
        _ruleName = rule.Name;
        _priority = rule.Priority;
        _conflictOrder = rule.ConflictOrder;
        _ruleIsEnabled = rule.IsEnabled;
        _selectedMatchMode = MatchModeChoices.Single(
            item => item.Mode == rule.MatchMode);
        OnPropertyChanged(nameof(RuleName));
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(ConflictOrder));
        OnPropertyChanged(nameof(RuleIsEnabled));
        OnPropertyChanged(nameof(SelectedMatchMode));
        Conditions.Clear();
        foreach (FeedAutomationCondition condition in rule.Conditions)
        {
            Conditions.Add(CreateCondition(
                condition.Field,
                condition.Operator,
                condition.Value));
        }
        Actions.Clear();
        foreach (FeedAutomationAction action in rule.Actions)
        {
            Actions.Add(CreateAction(action.Type, action.Value));
        }
        SimulationEntries.Clear();
        NotifyEditorState();
    }

    private void AddCondition()
    {
        Conditions.Add(CreateCondition(
            FeedAutomationField.Title,
            FeedAutomationOperator.Contains,
            string.Empty));
        DraftChanged();
    }

    private void RemoveCondition(AutomationConditionEditorItem? item)
    {
        if (item is not null && Conditions.Count > 1)
        {
            Conditions.Remove(item);
            DraftChanged();
        }
    }

    private void AddAction()
    {
        Actions.Add(CreateAction(FeedAutomationActionType.Notify, null));
        DraftChanged();
    }

    private void RemoveAction(AutomationActionEditorItem? item)
    {
        if (item is not null && Actions.Count > 1)
        {
            Actions.Remove(item);
            DraftChanged();
        }
    }

    private async Task SimulateAsync(CancellationToken cancellationToken)
    {
        FeedAutomationRuleDefinition definition = BuildDefinition();
        IsBusy = true;
        try
        {
            FeedAutomationSimulationResult result =
                await _simulationService.SimulateAsync(
                        definition,
                        SimulationEntryLimit,
                        cancellationToken)
                    .ConfigureAwait(true);
            SimulationEntries.Clear();
            foreach (FeedAutomationSimulationEntry entry in result.Entries)
            {
                SimulationEntries.Add(MapSimulationEntry(entry));
            }
            Status =
                $"只读模拟完成：检查 {result.ExaminedCount} 条，命中 {result.MatchedCount} 条；未执行任何动作。";
        }
        catch (Exception exception) when (
            exception is AppException or InvalidDataException or ArgumentException)
        {
            Status = FormatException(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        FeedAutomationRuleDefinition definition = BuildDefinition();
        string? targetId = _editingRuleId;
        IsBusy = true;
        try
        {
            FeedAutomationRuleMutationResult result = targetId is null
                ? await _adminService.CreateAsync(
                        definition,
                        RuleSetVersion,
                        cancellationToken)
                    .ConfigureAwait(true)
                : await _adminService.UpdateAsync(
                        targetId,
                        definition,
                        RuleSetVersion,
                        cancellationToken)
                    .ConfigureAwait(true);
            RuleSetVersion = result.RuleSetVersion;
            UpsertRule(result.Rule);
            SelectedRule = result.Rule;
            Status =
                $"规则已发布为规则集 v{RuleSetVersion}，服务端已记录管理员审计。";
        }
        catch (AppException exception)
            when (exception.Error.Code == AppErrorCode.Conflict)
        {
            try
            {
                await LoadRulesAsync(cancellationToken).ConfigureAwait(true);
                Status = "其他管理员已发布新版本；已刷新规则，请检查后重试。";
            }
            catch (AppException refreshException)
            {
                Status =
                    $"其他管理员已发布新版本，且刷新失败：{FormatError(refreshException)}";
            }
        }
        catch (Exception exception) when (
            exception is AppException or InvalidDataException or ArgumentException)
        {
            Status = FormatException(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpsertRule(FeedAutomationRule rule)
    {
        FeedAutomationRule? existing = Rules.FirstOrDefault(
            item => string.Equals(item.Id, rule.Id, StringComparison.Ordinal));
        if (existing is not null)
        {
            Rules.Remove(existing);
        }
        int index = 0;
        while (index < Rules.Count
               && CompareRules(Rules[index], rule) <= 0)
        {
            index++;
        }
        Rules.Insert(index, rule);
    }

    private FeedAutomationRuleDefinition BuildDefinition()
    {
        FeedAutomationCondition[] conditions = Conditions.Select(item => new
            FeedAutomationCondition(
                item.SelectedField.Field,
                item.SelectedOperator.Operator,
                item.RequiresValue ? item.Value : null))
            .ToArray();
        FeedAutomationAction[] actions = Actions.Select((item, index) => new
            FeedAutomationAction(
                item.SelectedType.Type,
                index * 10,
                item.RequiresValue ? item.Value : null))
            .ToArray();
        return FeedAutomationRuleValidator.ValidateAndNormalizeDefinition(new(
            RuleName,
            Priority,
            ConflictOrder,
            RuleIsEnabled,
            SelectedMatchMode.Mode,
            conditions,
            actions));
    }

    private bool CanUseDraft()
    {
        if (!IsAdmin || IsBusy)
        {
            return false;
        }
        try
        {
            _ = BuildDefinition();
            return true;
        }
        catch (Exception exception)
            when (exception is InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private AutomationConditionEditorItem CreateCondition(
        FeedAutomationField field,
        FeedAutomationOperator @operator,
        string? value) => new(
            FieldChoices.Single(item => item.Field == field),
            @operator,
            value,
            DraftChanged);

    private AutomationActionEditorItem CreateAction(
        FeedAutomationActionType type,
        string? value) => new(
            ActionChoices.Single(item => item.Type == type),
            value,
            DraftChanged);

    private void DraftChanged()
    {
        SimulationEntries.Clear();
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        BeginNewRuleCommand.NotifyCanExecuteChanged();
        AddConditionCommand.NotifyCanExecuteChanged();
        RemoveConditionCommand.NotifyCanExecuteChanged();
        AddActionCommand.NotifyCanExecuteChanged();
        RemoveActionCommand.NotifyCanExecuteChanged();
        SimulateCommand.NotifyCanExecuteChanged();
        PublishCommand.NotifyCanExecuteChanged();
    }

    private void NotifyEditorState()
    {
        OnPropertyChanged(nameof(IsNewRule));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(PublishLabel));
        NotifyCommands();
    }

    private void OnSessionChanged(
        object? sender,
        AccountSessionChangedEventArgs eventArgs)
    {
        if (_synchronizationContext is not null
            && SynchronizationContext.Current != _synchronizationContext)
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
        if (SetProperty(ref _isAdmin, isAdmin, nameof(IsAdmin)))
        {
            NotifyCommands();
        }
        if (isAdmin)
        {
            if (Rules.Count == 0)
            {
                Status = "管理员权限已确认，请刷新规则。";
            }
            return;
        }
        Rules.Clear();
        RuleSetVersion = 0;
        SimulationEntries.Clear();
        BeginNewRule();
        Status = "需要管理员账号才能查看或发布自动化规则。";
    }

    private static AutomationSimulationEntryViewModel MapSimulationEntry(
        FeedAutomationSimulationEntry entry)
    {
        bool matched =
            entry.Outcome == FeedAutomationRuleEvaluationOutcome.Matched;
        string actions = entry.Actions.Count == 0
            ? "不执行动作"
            : string.Join(
                "、",
                entry.Actions.Select(action =>
                    $"{AutomationRuleLabels.Action(action.Type)}" +
                    (action.Disposition == FeedAutomationActionDisposition.Suppressed
                        ? "（已抑制）"
                        : string.Empty)));
        return new(
            entry.EntryId,
            entry.Title,
            entry.SourceLabel,
            entry.PublishedAt?.ToLocalTime().ToString(
                "MM-dd HH:mm",
                CultureInfo.CurrentCulture)
                ?? "时间未知",
            matched,
            entry.Outcome switch
            {
                FeedAutomationRuleEvaluationOutcome.Matched => "命中",
                FeedAutomationRuleEvaluationOutcome.Disabled => "规则已停用",
                _ => "未命中"
            },
            actions);
    }

    private static int CompareRules(
        FeedAutomationRule left,
        FeedAutomationRule right)
    {
        int priority = right.Priority.CompareTo(left.Priority);
        if (priority != 0)
        {
            return priority;
        }
        int conflict = left.ConflictOrder.CompareTo(right.ConflictOrder);
        return conflict != 0
            ? conflict
            : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static string FormatException(Exception exception) =>
        exception is AppException appException
            ? FormatError(appException)
            : $"规则尚未完整：{exception.Message}";

    private static string FormatError(AppException exception) =>
        $"{exception.Error.UserMessage} {exception.Error.Suggestion}".Trim();
}

public sealed record AutomationValueChoice(string Value, string Label);
