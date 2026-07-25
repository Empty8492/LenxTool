using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedAutomationActionQueueRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly FeedAutomationActionType[] AllActionTypes =
        Enum.GetValues<FeedAutomationActionType>();
    private const string EntryId = "entry-action-queue";
    private const string WinnerRuleId = "30000000-0000-4000-8000-000000000091";
    private const string LaterRuleId = "30000000-0000-4000-8000-000000000092";
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools feed automation action queue repository tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ClaimUsesDeterministicOrderAndNeverReturnsSuppressedActions()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        await StageAsync(database, Plan());
        var queue = new FeedAutomationActionQueueRepository(database);

        IReadOnlyList<FeedAutomationActionLease> first = await queue.ClaimDueAsync(
            Now,
            AllActionTypes,
            2,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(2, first.Count);
        Assert.Equal(
            [FeedAutomationActionType.Translate, FeedAutomationActionType.AddTag],
            first.Select(action => action.Type));
        Assert.All(first, action =>
        {
            Assert.Equal(1, action.AttemptCount);
            Assert.Equal(32, action.LeaseToken.Length);
        });
        foreach (FeedAutomationActionLease action in first)
        {
            await queue.CompleteAsync(
                action,
                FeedAutomationActionRunOutcome.Succeeded,
                null,
                Now.AddMinutes(1),
                CancellationToken.None);
        }

        FeedAutomationActionLease remaining = Assert.Single(
            await queue.ClaimDueAsync(
                Now.AddMinutes(1),
                AllActionTypes,
                10,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        Assert.Equal(FeedAutomationActionType.Notify, remaining.Type);
        await queue.CompleteAsync(
            remaining,
            FeedAutomationActionRunOutcome.Succeeded,
            null,
            Now.AddMinutes(2),
            CancellationToken.None);
        Assert.Empty(await queue.ClaimDueAsync(
            Now.AddDays(1),
            AllActionTypes,
            10,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
    }

    [Fact]
    public async Task RetrySurvivesRestartAndSucceededActionStaysTerminal()
    {
        using (SqliteDatabase database = await CreateDatabaseAsync())
        {
            await StageAsync(database, SingleActionPlan());
            var queue = new FeedAutomationActionQueueRepository(database);
            FeedAutomationActionLease claimed = Assert.Single(await queue.ClaimDueAsync(
                Now,
                AllActionTypes,
                1,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
            await queue.ScheduleRetryAsync(
                claimed,
                "TEMPORARY_FAILURE",
                Now.AddMinutes(10),
                Now.AddMinutes(1),
                CancellationToken.None);
        }

        using SqliteDatabase reopened = await CreateDatabaseAsync();
        var reopenedQueue = new FeedAutomationActionQueueRepository(reopened);
        Assert.Empty(await reopenedQueue.ClaimDueAsync(
            Now.AddMinutes(9),
            AllActionTypes,
            1,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        FeedAutomationActionLease retried = Assert.Single(
            await reopenedQueue.ClaimDueAsync(
                Now.AddMinutes(10),
                AllActionTypes,
                1,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        Assert.Equal(2, retried.AttemptCount);
        await reopenedQueue.CompleteAsync(
            retried,
            FeedAutomationActionRunOutcome.Succeeded,
            null,
            Now.AddMinutes(11),
            CancellationToken.None);

        FeedAutomationRunSnapshot snapshot =
            await new FeedAutomationRunRepository(reopened).GetAsync(
                EntryId,
                CancellationToken.None);
        FeedAutomationActionRun action = Assert.Single(snapshot.ActionRuns);
        Assert.Equal(FeedAutomationActionRunStatus.Succeeded, action.Status);
        Assert.Equal(2, action.AttemptCount);
        Assert.Null(action.LastErrorCode);
        Assert.Empty(await reopenedQueue.ClaimDueAsync(
            Now.AddDays(1),
            AllActionTypes,
            1,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredLeaseIsReclaimedAndRejectsStaleCompletion()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        await StageAsync(database, SingleActionPlan());
        var queue = new FeedAutomationActionQueueRepository(database);
        FeedAutomationActionLease expired = Assert.Single(await queue.ClaimDueAsync(
            Now,
            AllActionTypes,
            1,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        FeedAutomationActionLease current = Assert.Single(await queue.ClaimDueAsync(
            Now.AddMinutes(6),
            AllActionTypes,
            1,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));

        Assert.NotEqual(expired.LeaseToken, current.LeaseToken);
        Assert.Equal(2, current.AttemptCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.CompleteAsync(
            expired,
            FeedAutomationActionRunOutcome.Succeeded,
            null,
            Now.AddMinutes(6),
            CancellationToken.None));
        await queue.CompleteAsync(
            current,
            FeedAutomationActionRunOutcome.Failed,
            "PERMANENT_FAILURE",
            Now.AddMinutes(7),
            CancellationToken.None);

        FeedAutomationActionRun action = Assert.Single(
            (await new FeedAutomationRunRepository(database).GetAsync(
                EntryId,
                CancellationToken.None)).ActionRuns);
        Assert.Equal(FeedAutomationActionRunStatus.Failed, action.Status);
        Assert.Equal("PERMANENT_FAILURE", action.LastErrorCode);
    }

    [Fact]
    public async Task ReleaseMakesClaimImmediatelyAvailableWithANewLease()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        await StageAsync(database, SingleActionPlan());
        var queue = new FeedAutomationActionQueueRepository(database);
        FeedAutomationActionLease first = Assert.Single(await queue.ClaimDueAsync(
            Now,
            AllActionTypes,
            1,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));

        await queue.ReleaseAsync(
            first,
            Now.AddMinutes(1),
            CancellationToken.None);
        FeedAutomationActionLease second = Assert.Single(await queue.ClaimDueAsync(
            Now.AddMinutes(1),
            AllActionTypes,
            1,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));

        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.Equal(2, second.AttemptCount);
    }

    [Fact]
    public async Task ConcurrentClaimsIssueOnlyOneLeaseForAnAction()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        await StageAsync(database, SingleActionPlan());
        var firstQueue = new FeedAutomationActionQueueRepository(database);
        var secondQueue = new FeedAutomationActionQueueRepository(database);

        IReadOnlyList<FeedAutomationActionLease>[] claims = await Task.WhenAll(
            firstQueue.ClaimDueAsync(
                Now,
                AllActionTypes,
                1,
                TimeSpan.FromMinutes(5),
                CancellationToken.None),
            secondQueue.ClaimDueAsync(
                Now,
                AllActionTypes,
                1,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));

        FeedAutomationActionLease lease = Assert.Single(claims.SelectMany(items => items));
        Assert.Equal(1, lease.AttemptCount);
    }

    [Fact]
    public async Task ClaimFiltersTypesWithoutLeasingOtherActions()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        await StageAsync(database, Plan());
        var queue = new FeedAutomationActionQueueRepository(database);

        FeedAutomationActionLease local = Assert.Single(await queue.ClaimDueAsync(
            Now,
            [
                FeedAutomationActionType.AddTag,
                FeedAutomationActionType.Hide,
                FeedAutomationActionType.MarkRead
            ],
            10,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        Assert.Equal(FeedAutomationActionType.AddTag, local.Type);

        FeedAutomationActionLease remote = Assert.Single(await queue.ClaimDueAsync(
            Now,
            [FeedAutomationActionType.Translate],
            10,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        Assert.Equal(FeedAutomationActionType.Translate, remote.Type);
    }

    [Fact]
    public async Task ClaimRejectsEmptyAndUnknownActionTypeFilters()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var queue = new FeedAutomationActionQueueRepository(database);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => queue.ClaimDueAsync(
                Now,
                [],
                1,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => queue.ClaimDueAsync(
                Now,
                [(FeedAutomationActionType)999],
                1,
                TimeSpan.FromMinutes(5),
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

    private static Task<FeedAutomationStageResult> StageAsync(
        SqliteDatabase database,
        FeedAutomationPlan plan) =>
        new FeedAutomationRunRepository(database).StageAsync(
            plan,
            Now,
            CancellationToken.None);

    private static FeedAutomationPlan SingleActionPlan() =>
        new(
            EntryId,
            [
                new(
                    WinnerRuleId,
                    1,
                    FeedAutomationRuleEvaluationOutcome.Matched)
            ],
            [
                new(
                    WinnerRuleId,
                    1,
                    500,
                    0,
                    FeedAutomationActionType.Notify,
                    10,
                    null,
                    FeedAutomationActionDisposition.Planned,
                    FeedAutomationActionSuppressionReason.None,
                    null,
                    null,
                    null)
            ]);

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
                    FeedAutomationRuleEvaluationOutcome.Matched)
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
                    FeedAutomationActionType.Notify,
                    5,
                    null,
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
