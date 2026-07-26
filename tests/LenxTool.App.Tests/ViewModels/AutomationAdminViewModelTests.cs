using LenxTool.App.ViewModels;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class AutomationAdminViewModelTests
{
    private const string RuleId = "10000000-0000-4000-8000-000000000010";

    [Fact]
    public async Task AdminInitializationLoadsAllRulesIntoGraphicalEditor()
    {
        var context = CreateContext(AccountRole.Admin, Snapshot());

        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.SelectedRule = Assert.Single(context.ViewModel.Rules);

        Assert.True(context.ViewModel.IsAdmin);
        Assert.Equal(7, context.ViewModel.RuleSetVersion);
        Assert.Equal("发布摘要", context.ViewModel.RuleName);
        Assert.Equal(
            FeedAutomationField.Title,
            Assert.Single(context.ViewModel.Conditions).SelectedField.Field);
        Assert.Equal(
            FeedAutomationOperator.Contains,
            Assert.Single(context.ViewModel.Conditions).SelectedOperator.Operator);
        Assert.Equal(
            FeedAutomationActionType.Notify,
            Assert.Single(context.ViewModel.Actions).SelectedType.Type);
        Assert.DoesNotContain(
            context.ViewModel.GetType().GetProperties(),
            property => property.Name.Contains("Script", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OrdinaryUserCannotLoadOrExecuteAdminCommands()
    {
        var context = CreateContext(AccountRole.User, Snapshot());

        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.False(context.ViewModel.IsAdmin);
        Assert.Empty(context.ViewModel.Rules);
        Assert.False(context.ViewModel.RefreshCommand.CanExecute(null));
        Assert.False(context.ViewModel.PublishCommand.CanExecute(null));
        Assert.False(context.ViewModel.SimulateCommand.CanExecute(null));
        Assert.Equal(0, context.Admin.GetCount);
        Assert.Contains("管理员", context.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimulationUsesDraftOnlyAndNeverPublishes()
    {
        var context = CreateContext(AccountRole.Admin, Snapshot());
        context.Simulation.Result = new(
            2,
            1,
            [
                new(
                    "entry-1",
                    "Release notes",
                    "示例源",
                    new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero),
                    FeedAutomationRuleEvaluationOutcome.Matched,
                    [
                        new(
                            RuleId,
                            1,
                            200,
                            10,
                            FeedAutomationActionType.Notify,
                            0,
                            null,
                            FeedAutomationActionDisposition.Planned,
                            FeedAutomationActionSuppressionReason.None,
                            null,
                            null,
                            null)
                    ]),
                new(
                    "entry-2",
                    "Weekly digest",
                    "示例源",
                    null,
                    FeedAutomationRuleEvaluationOutcome.NotMatched,
                    [])
            ]);
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.SelectedRule = context.ViewModel.Rules[0];

        await context.ViewModel.SimulateCommand.ExecuteAsync();

        Assert.Single(context.Simulation.Calls);
        Assert.Empty(context.Admin.CreateCalls);
        Assert.Empty(context.Admin.UpdateCalls);
        Assert.Equal(2, context.ViewModel.SimulationEntries.Count);
        Assert.True(context.ViewModel.SimulationEntries[0].IsMatched);
        Assert.Contains("通知", context.ViewModel.SimulationEntries[0].ActionsLabel);
        Assert.Contains("只读模拟", context.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishingNewRuleUsesCurrentVersionAndSelectsCreatedRule()
    {
        var context = CreateContext(AccountRole.Admin, Snapshot());
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.BeginNewRuleCommand.Execute(null);
        context.ViewModel.RuleName = "视频提醒";
        AutomationConditionEditorItem condition =
            Assert.Single(context.ViewModel.Conditions);
        condition.SelectedField = context.ViewModel.FieldChoices.Single(
            item => item.Field == FeedAutomationField.HasVideo);
        condition.SelectedOperator = condition.OperatorChoices.Single(
            item => item.Operator == FeedAutomationOperator.Equals);
        condition.Value = "true";
        AutomationActionEditorItem action =
            Assert.Single(context.ViewModel.Actions);
        action.SelectedType = context.ViewModel.ActionChoices.Single(
            item => item.Type == FeedAutomationActionType.Notify);
        context.Admin.NextMutation = new(
            8,
            Rule() with { Name = "视频提醒", Version = 1 });

        await context.ViewModel.PublishCommand.ExecuteAsync();

        RuleMutationCall call = Assert.Single(context.Admin.CreateCalls);
        Assert.Equal(7, call.ExpectedVersion);
        Assert.Equal("视频提醒", call.Definition.Name);
        Assert.Equal(8, context.ViewModel.RuleSetVersion);
        Assert.Equal("视频提醒", context.ViewModel.SelectedRule?.Name);
        Assert.Empty(context.Admin.UpdateCalls);
        Assert.Contains("审计", context.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionConflictRefreshesWithoutReplayingPublish()
    {
        var context = CreateContext(AccountRole.Admin, Snapshot());
        context.Admin.MutationFailure = new AppException(new(
            AppErrorCode.Conflict,
            "规则版本冲突",
            "其他管理员已经修改规则",
            "刷新后重试",
            "AUTOMATION_VERSION_CONFLICT"));
        context.Admin.Snapshot = Snapshot() with { RuleSetVersion = 8 };
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.SelectedRule = context.ViewModel.Rules[0];

        await context.ViewModel.PublishCommand.ExecuteAsync();

        Assert.Single(context.Admin.UpdateCalls);
        Assert.Equal(2, context.Admin.GetCount);
        Assert.Equal(8, context.ViewModel.RuleSetVersion);
        Assert.Contains("其他管理员", context.ViewModel.Status, StringComparison.Ordinal);
        Assert.Contains("重试", context.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoleLossClearsRulesAndDisablesDraftActions()
    {
        var context = CreateContext(AccountRole.Admin, Snapshot());
        await context.ViewModel.InitializeAsync(CancellationToken.None);

        context.Account.SetRole(AccountRole.User);

        Assert.False(context.ViewModel.IsAdmin);
        Assert.Empty(context.ViewModel.Rules);
        Assert.Empty(context.ViewModel.SimulationEntries);
        Assert.False(context.ViewModel.PublishCommand.CanExecute(null));
    }

    private static TestContext CreateContext(
        AccountRole role,
        FeedAutomationRuleSnapshot snapshot)
    {
        var account = new FakeAccountSession(role);
        var admin = new FakeAdminService { Snapshot = snapshot };
        var simulation = new FakeSimulationService();
        return new(
            new AutomationAdminViewModel(admin, simulation, account),
            admin,
            simulation,
            account);
    }

    private static FeedAutomationRuleSnapshot Snapshot() => new(
        7,
        new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero),
        null,
        [Rule()]);

    private static FeedAutomationRule Rule() => new(
        RuleId,
        2,
        "发布摘要",
        200,
        10,
        false,
        FeedAutomationMatchMode.All,
        [
            new(
                FeedAutomationField.Title,
                FeedAutomationOperator.Contains,
                "release")
        ],
        [new(FeedAutomationActionType.Notify, 0, null)]);

    private sealed record TestContext(
        AutomationAdminViewModel ViewModel,
        FakeAdminService Admin,
        FakeSimulationService Simulation,
        FakeAccountSession Account);

    private sealed record RuleMutationCall(
        string? RuleId,
        FeedAutomationRuleDefinition Definition,
        long ExpectedVersion);

    private sealed class FakeAdminService : IFeedAutomationRuleAdminService
    {
        public FeedAutomationRuleSnapshot Snapshot { get; set; } =
            AutomationAdminViewModelTests.Snapshot();
        public FeedAutomationRuleMutationResult NextMutation { get; set; } =
            new(8, Rule());
        public AppException? MutationFailure { get; set; }
        public int GetCount { get; private set; }
        public List<RuleMutationCall> CreateCalls { get; } = [];
        public List<RuleMutationCall> UpdateCalls { get; } = [];

        public Task<FeedAutomationRuleSnapshot> GetAllAsync(
            CancellationToken cancellationToken)
        {
            GetCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<FeedAutomationRuleMutationResult> CreateAsync(
            FeedAutomationRuleDefinition definition,
            long expectedRuleSetVersion,
            CancellationToken cancellationToken)
        {
            CreateCalls.Add(new(null, definition, expectedRuleSetVersion));
            return Mutation();
        }

        public Task<FeedAutomationRuleMutationResult> UpdateAsync(
            string ruleId,
            FeedAutomationRuleDefinition definition,
            long expectedRuleSetVersion,
            CancellationToken cancellationToken)
        {
            UpdateCalls.Add(new(ruleId, definition, expectedRuleSetVersion));
            return Mutation();
        }

        private Task<FeedAutomationRuleMutationResult> Mutation() =>
            MutationFailure is null
                ? Task.FromResult(NextMutation)
                : Task.FromException<FeedAutomationRuleMutationResult>(
                    MutationFailure);
    }

    private sealed class FakeSimulationService
        : IFeedAutomationRuleSimulationService
    {
        public FeedAutomationSimulationResult Result { get; set; } =
            new(0, 0, []);
        public List<FeedAutomationRuleDefinition> Calls { get; } = [];

        public Task<FeedAutomationSimulationResult> SimulateAsync(
            FeedAutomationRuleDefinition definition,
            int maximumEntries,
            CancellationToken cancellationToken)
        {
            Calls.Add(definition);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeAccountSession : IAccountSessionService
    {
        public FakeAccountSession(AccountRole role)
        {
            SetRole(role);
        }

        public bool IsConfigured => true;
        public AccountSessionSnapshot Current { get; private set; } =
            AccountSessionSnapshot.SignedOut;
        public event EventHandler<AccountSessionChangedEventArgs>? SessionChanged;

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task LoginAsync(
            string username,
            string password,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RefreshAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task LogoutAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void SetRole(AccountRole role)
        {
            Current = new(
                AccountSessionStatus.SignedIn,
                new(
                    "10000000-0000-4000-8000-000000000001",
                    "owner",
                    role));
            SessionChanged?.Invoke(this, new(Current));
        }
    }
}
