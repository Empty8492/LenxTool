using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedAutomationRunRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 8, 0, 0, TimeSpan.Zero);
    private const string EntryId = "entry-1";
    private const string WinnerRuleId = "30000000-0000-4000-8000-000000000081";
    private const string LaterRuleId = "30000000-0000-4000-8000-000000000082";
    private const string MissedRuleId = "30000000-0000-4000-8000-000000000083";
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools feed automation run repository tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StagePersistsEveryDecisionOnceWithStableIdempotencyKeys()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedAutomationRunRepository(database);
        FeedAutomationPlan plan = Plan();

        FeedAutomationStageResult first = await repository.StageAsync(
            plan,
            Now,
            CancellationToken.None);
        FeedAutomationStageResult replay = await repository.StageAsync(
            plan,
            Now.AddMinutes(1),
            CancellationToken.None);
        FeedAutomationRunSnapshot snapshot = await repository.GetAsync(
            EntryId,
            CancellationToken.None);

        Assert.Equal(new(3, 3), first);
        Assert.Equal(new(0, 0), replay);
        Assert.Equal(3, snapshot.RuleRuns.Count);
        Assert.Equal(3, snapshot.ActionRuns.Count);
        Assert.Equal(
            [
                FeedAutomationRuleEvaluationOutcome.Matched,
                FeedAutomationRuleEvaluationOutcome.Matched,
                FeedAutomationRuleEvaluationOutcome.NotMatched
            ],
            snapshot.RuleRuns.Select(run => run.Outcome));
        Assert.Equal(
            [
                FeedAutomationActionRunStatus.Pending,
                FeedAutomationActionRunStatus.Pending,
                FeedAutomationActionRunStatus.Suppressed
            ],
            snapshot.ActionRuns.Select(run => run.Status));
        Assert.All(snapshot.ActionRuns, run =>
        {
            Assert.Equal(64, run.IdempotencyKey.Length);
            Assert.Matches("^[0-9a-f]{64}$", run.IdempotencyKey);
            Assert.Equal(Now, run.CreatedAt);
        });
        Assert.Equal(
            snapshot.ActionRuns.Count,
            snapshot.ActionRuns.Select(run => run.IdempotencyKey).Distinct().Count());

        FeedAutomationActionRun suppressed = Assert.Single(
            snapshot.ActionRuns,
            run => run.Status == FeedAutomationActionRunStatus.Suppressed);
        Assert.Equal(
            FeedAutomationActionSuppressionReason.DuplicateSingleton,
            suppressed.SuppressionReason);
        Assert.Equal(WinnerRuleId, suppressed.WinningRuleId);
        Assert.Equal(1, suppressed.WinningRuleVersion);
        Assert.Equal(10, suppressed.WinningActionOrder);
    }

    [Fact]
    public async Task RestartPreservesLedgerAndChangedReplayCannotAppendActions()
    {
        FeedAutomationRunSnapshot beforeRestart;
        using (SqliteDatabase database = await CreateDatabaseAsync())
        {
            var repository = new FeedAutomationRunRepository(database);
            await repository.StageAsync(Plan(), Now, CancellationToken.None);
            beforeRestart = await repository.GetAsync(EntryId, CancellationToken.None);
        }

        using SqliteDatabase reopened = await CreateDatabaseAsync();
        var afterRestartRepository = new FeedAutomationRunRepository(reopened);
        FeedAutomationPlan changedReplay = Plan() with
        {
            Actions =
            [
                .. Plan().Actions,
                new(
                    WinnerRuleId,
                    1,
                    500,
                    0,
                    FeedAutomationActionType.AddTag,
                    99,
                    "unexpected",
                    FeedAutomationActionDisposition.Planned,
                    FeedAutomationActionSuppressionReason.None,
                    null,
                    null,
                    null)
            ]
        };

        FeedAutomationStageResult replay = await afterRestartRepository.StageAsync(
            changedReplay,
            Now.AddHours(1),
            CancellationToken.None);
        FeedAutomationRunSnapshot afterRestart = await afterRestartRepository.GetAsync(
            EntryId,
            CancellationToken.None);

        Assert.Equal(new(0, 0), replay);
        Assert.Equal(
            beforeRestart.RuleRuns.ToArray(),
            afterRestart.RuleRuns.ToArray());
        Assert.Equal(
            beforeRestart.ActionRuns.ToArray(),
            afterRestart.ActionRuns.ToArray());
        Assert.DoesNotContain(
            afterRestart.ActionRuns,
            run => run.ActionOrder == 99);
    }

    [Fact]
    public async Task StageRejectsInconsistentPlanWithoutPartialWrites()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedAutomationRunRepository(database);
        FeedAutomationPlan invalid = Plan() with
        {
            Actions =
            [
                .. Plan().Actions,
                new(
                    "30000000-0000-4000-8000-000000000099",
                    1,
                    1,
                    1,
                    FeedAutomationActionType.Notify,
                    1,
                    null,
                    FeedAutomationActionDisposition.Planned,
                    FeedAutomationActionSuppressionReason.None,
                    null,
                    null,
                    null)
            ]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.StageAsync(
            invalid,
            Now,
            CancellationToken.None));

        FeedAutomationRunSnapshot snapshot = await repository.GetAsync(
            EntryId,
            CancellationToken.None);
        Assert.Empty(snapshot.RuleRuns);
        Assert.Empty(snapshot.ActionRuns);
    }

    [Fact]
    public async Task StageHandlesMaximumBoundedPlanAtomically()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedAutomationRunRepository(database);
        FeedAutomationRuleEvaluation[] evaluations = Enumerable.Range(1, 100)
            .Select(index => new FeedAutomationRuleEvaluation(
                $"30000000-0000-4000-8000-{index:D12}",
                1,
                FeedAutomationRuleEvaluationOutcome.Matched))
            .ToArray();
        FeedAutomationActionDecision[] actions = evaluations
            .SelectMany((evaluation, index) => Enumerable.Range(0, 8)
                .Select(actionOrder => new FeedAutomationActionDecision(
                    evaluation.RuleId,
                    evaluation.RuleVersion,
                    100 - index,
                    index,
                    FeedAutomationActionType.AddTag,
                    actionOrder,
                    $"tag-{index:D3}-{actionOrder}",
                    FeedAutomationActionDisposition.Planned,
                    FeedAutomationActionSuppressionReason.None,
                    null,
                    null,
                    null)))
            .ToArray();
        var plan = new FeedAutomationPlan(EntryId, evaluations, actions);

        FeedAutomationStageResult result = await repository.StageAsync(
            plan,
            Now,
            CancellationToken.None);

        Assert.Equal(new(100, 800), result);
        FeedAutomationRunSnapshot snapshot = await repository.GetAsync(
            EntryId,
            CancellationToken.None);
        Assert.Equal(100, snapshot.RuleRuns.Count);
        Assert.Equal(800, snapshot.ActionRuns.Count);
    }

    [Fact]
    public async Task StageRejectsMultipleVersionsOfOneRuleInTheSamePlan()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedAutomationRunRepository(database);
        FeedAutomationPlan invalid = Plan() with
        {
            RuleEvaluations =
            [
                .. Plan().RuleEvaluations,
                new(
                    WinnerRuleId,
                    2,
                    FeedAutomationRuleEvaluationOutcome.NotMatched)
            ]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.StageAsync(
            invalid,
            Now,
            CancellationToken.None));
    }

    [Fact]
    public async Task StageRejectsMoreThanEightActionsForOneRule()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedAutomationRunRepository(database);
        FeedAutomationPlan basePlan = Plan();
        FeedAutomationPlan invalid = basePlan with
        {
            Actions = Enumerable.Range(0, 9)
                .Select(index => new FeedAutomationActionDecision(
                    WinnerRuleId,
                    1,
                    500,
                    0,
                    FeedAutomationActionType.AddTag,
                    index,
                    $"tag-{index}",
                    FeedAutomationActionDisposition.Planned,
                    FeedAutomationActionSuppressionReason.None,
                    null,
                    null,
                    null))
                .ToArray()
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.StageAsync(
            invalid,
            Now,
            CancellationToken.None));
    }

    public void Dispose()
    {
        ClearTestPool();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        return database;
    }

    private void ClearTestPool()
    {
        AppPaths paths = new(_testRoot);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5
        };
        using var connection = new SqliteConnection(builder.ToString());
        SqliteConnection.ClearPool(connection);
    }

    private static FeedAutomationPlan Plan() =>
        new(
            EntryId,
            [
                new(
                    WinnerRuleId,
                    1,
                    FeedAutomationRuleEvaluationOutcome.Matched),
                new(
                    LaterRuleId,
                    1,
                    FeedAutomationRuleEvaluationOutcome.Matched),
                new(
                    MissedRuleId,
                    2,
                    FeedAutomationRuleEvaluationOutcome.NotMatched)
            ],
            [
                new(
                    WinnerRuleId,
                    1,
                    500,
                    0,
                    FeedAutomationActionType.Translate,
                    10,
                    "zh-Hans",
                    FeedAutomationActionDisposition.Planned,
                    FeedAutomationActionSuppressionReason.None,
                    null,
                    null,
                    null),
                new(
                    WinnerRuleId,
                    1,
                    500,
                    0,
                    FeedAutomationActionType.AddTag,
                    20,
                    "AI",
                    FeedAutomationActionDisposition.Planned,
                    FeedAutomationActionSuppressionReason.None,
                    null,
                    null,
                    null),
                new(
                    LaterRuleId,
                    1,
                    100,
                    0,
                    FeedAutomationActionType.Translate,
                    30,
                    "en",
                    FeedAutomationActionDisposition.Suppressed,
                    FeedAutomationActionSuppressionReason.DuplicateSingleton,
                    WinnerRuleId,
                    1,
                    10)
            ]);
}
