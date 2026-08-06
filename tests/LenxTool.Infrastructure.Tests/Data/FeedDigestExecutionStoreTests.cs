using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedDigestExecutionStoreTests : IDisposable
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 6, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DueAt =
        new(2026, 8, 6, 8, 0, 0, TimeSpan.Zero);
    private const string ReportId =
        "feed-digest-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools feed digest execution tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReportFtsAndRunCompleteInOneDurableCommit()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        LocalScheduleRunLease lease = await ClaimAsync(database);
        var store = new FeedDigestExecutionStore(database);

        Assert.Equal(
            FeedDigestExecutionBeginResult.Started,
            await store.BeginAsync(
                lease,
                ReportId,
                DueAt.AddMinutes(1),
                CancellationToken.None));
        Assert.True(await store.CompleteAsync(
            lease,
            Report(),
            DueAt.AddMinutes(2),
            CancellationToken.None));

        AiReport saved = Assert.IsType<AiReport>(
            await new NewsRepository(database).GetReportByIdAsync(
                ReportId,
                CancellationToken.None));
        Assert.Equal("原子提交的摘要正文", saved.Content);
        ContentSearchResult search = Assert.Single(
            await new NewsRepository(database).SearchContentAsync(
                "原子提交",
                10,
                CancellationToken.None));
        Assert.Equal(ReportId, search.EntityId);
        LocalScheduleRun run = Assert.Single(
            await new LocalScheduleRunRepository(database).GetRecentAsync(
                FeedDigestScheduleIds.Daily,
                10,
                CancellationToken.None));
        Assert.Equal(LocalScheduleRunStatus.Completed, run.Status);
        Assert.Equal("COMPLETED", await ReadRequestStatusAsync(database));
    }

    [Fact]
    public async Task ScheduleMutationDiscardsGeneratedReportAndCancelsOldRun()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        LocalScheduleRunLease lease = await ClaimAsync(database);
        var store = new FeedDigestExecutionStore(database);
        Assert.Equal(
            FeedDigestExecutionBeginResult.Started,
            await store.BeginAsync(
                lease,
                ReportId,
                DueAt.AddMinutes(1),
                CancellationToken.None));
        await new LocalScheduledTaskRepository(database).SetEnabledAsync(
            FeedDigestScheduleIds.Daily,
            false,
            DueAt.AddMinutes(2),
            CancellationToken.None);

        Assert.False(await store.CompleteAsync(
            lease,
            Report(),
            DueAt.AddMinutes(3),
            CancellationToken.None));

        Assert.Null(await new NewsRepository(database).GetReportByIdAsync(
            ReportId,
            CancellationToken.None));
        Assert.Equal(
            LocalScheduleRunStatus.Cancelled,
            Assert.Single(
                await new LocalScheduleRunRepository(database).GetRecentAsync(
                    FeedDigestScheduleIds.Daily,
                    10,
                    CancellationToken.None)).Status);
        Assert.Equal("DISCARDED", await ReadRequestStatusAsync(database));
    }

    [Fact]
    public async Task ExpiredStartedAttemptSuppressesAutomaticSecondModelCall()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        LocalScheduleRunLease first = await ClaimAsync(database);
        var store = new FeedDigestExecutionStore(database);
        Assert.Equal(
            FeedDigestExecutionBeginResult.Started,
            await store.BeginAsync(
                first,
                ReportId,
                DueAt.AddMinutes(1),
                CancellationToken.None));

        DateTimeOffset reclaimedAt = DueAt.AddMinutes(11);
        LocalScheduleRunLease second =
            Assert.IsType<LocalScheduleRunLease>(
                await new LocalScheduleRunRepository(database).ClaimDueAsync(
                    reclaimedAt,
                    DueAt.AddMinutes(-1),
                    TimeSpan.FromMinutes(10),
                    CancellationToken.None));
        Assert.Equal(2, second.AttemptCount);
        Assert.Equal(
            FeedDigestExecutionBeginResult
                .SuppressedUncertainPriorAttempt,
            await store.BeginAsync(
                second,
                ReportId,
                reclaimedAt,
                CancellationToken.None));

        Assert.Null(await new NewsRepository(database).GetReportByIdAsync(
            ReportId,
            CancellationToken.None));
        Assert.Equal(
            LocalScheduleRunStatus.Cancelled,
            Assert.Single(
                await new LocalScheduleRunRepository(database).GetRecentAsync(
                    FeedDigestScheduleIds.Daily,
                    10,
                    CancellationToken.None)).Status);
        Assert.Equal("AMBIGUOUS", await ReadRequestStatusAsync(database));
    }

    [Fact]
    public async Task ExplicitSafeFailureCanClearMarkerAndRetryLater()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        LocalScheduleRunLease first = await ClaimAsync(database);
        var store = new FeedDigestExecutionStore(database);
        Assert.Equal(
            FeedDigestExecutionBeginResult.Started,
            await store.BeginAsync(
                first,
                ReportId,
                DueAt.AddMinutes(1),
                CancellationToken.None));
        await store.ClearForSafeRetryAsync(
            first,
            ReportId,
            DueAt.AddMinutes(1),
            CancellationToken.None);
        DateTimeOffset retryAt = DueAt.AddMinutes(3);
        await new LocalScheduleRunRepository(database).ReleaseAsync(
            first,
            DueAt.AddMinutes(1),
            CancellationToken.None,
            retryAt);

        LocalScheduleRunLease second =
            Assert.IsType<LocalScheduleRunLease>(
                await new LocalScheduleRunRepository(database).ClaimDueAsync(
                    retryAt,
                    DueAt.AddMinutes(-1),
                    TimeSpan.FromMinutes(10),
                    CancellationToken.None));
        Assert.Equal(
            FeedDigestExecutionBeginResult.Started,
            await store.BeginAsync(
                second,
                ReportId,
                retryAt,
                CancellationToken.None));
        Assert.Equal(2, second.AttemptCount);
    }

    private static async Task<LocalScheduleRunLease> ClaimAsync(
        SqliteDatabase database)
    {
        await new LocalScheduledTaskRepository(database).SaveAsync(
            FeedDigestScheduleIds.Daily,
            new(
                LocalScheduleFrequency.Daily,
                "UTC",
                new TimeOnly(8, 0)),
            LocalScheduleMissedRunPolicy.RunOnce,
            true,
            CreatedAt,
            CancellationToken.None,
            FeedDigestScopePayload.Serialize(
                FeedDigestScope.AllActive));
        return Assert.IsType<LocalScheduleRunLease>(
            await new LocalScheduleRunRepository(database).ClaimDueAsync(
                DueAt,
                DueAt.AddMinutes(-1),
                TimeSpan.FromMinutes(10),
                CancellationToken.None));
    }

    private static AiReport Report() =>
        new(
            ReportId,
            "feed_digest",
            FeedDigestScheduleIds.Daily,
            "daily_feed_digest",
            "每日订阅摘要 · 2026-08-06",
            "原子提交的摘要正文",
            FeedDigestOptions.Default.Model,
            1,
            120,
            DueAt.AddMinutes(2));

    private static async Task<string> ReadRequestStatusAsync(
        SqliteDatabase database)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT status
            FROM feed_digest_requests
            WHERE schedule_id=$scheduleId
              AND scheduled_for=$scheduledFor;
            """;
        command.Parameters.AddWithValue(
            "$scheduleId",
            FeedDigestScheduleIds.Daily);
        command.Parameters.AddWithValue(
            "$scheduledFor",
            DueAt.ToString("O"));
        return Assert.IsType<string>(
            await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private SqliteDatabase CreateDatabase() =>
        new(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
