using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedAiAutomationJobRepositoryTests : IDisposable
{
    private const string FeedId = "20000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 4, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools feed AI automation repository tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnqueueCreatesEnabledTasksOnceAndSupersedesOldContent()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var entries = new FeedEntryRepository(database);
        var repository = new FeedAiAutomationJobRepository(database);
        FeedEntry first = Entry("entry-1", 'a');
        await entries.UpsertAsync(FeedId, [first], CancellationToken.None);
        var policy = new ResolvedFeedAiPolicy(true, true, true, "zh-Hans", 20, 2);

        Assert.Equal(2, await repository.EnqueueAsync(
            FeedId, [first], policy, Now, CancellationToken.None));
        Assert.Equal(0, await repository.EnqueueAsync(
            FeedId, [first], policy, Now, CancellationToken.None));

        FeedEntry updated = first with
        {
            ContentHash = new string('b', 64),
            Summary = "updated"
        };
        await entries.UpsertAsync(FeedId, [updated], CancellationToken.None);
        Assert.Equal(2, await repository.EnqueueAsync(
            FeedId, [updated], policy, Now.AddMinutes(1), CancellationToken.None));

        IReadOnlyList<FeedAiAutomationJob> claimed = await repository.ClaimDueAsync(
            Now.AddMinutes(1),
            10,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        Assert.Equal(2, claimed.Count);
        Assert.All(claimed, job => Assert.Equal(updated.ContentHash, job.ContentHash));
        Assert.Contains(claimed, job => job.TaskType == FeedAiAutomationTaskType.Summary);
        Assert.Contains(claimed, job =>
            job.TaskType == FeedAiAutomationTaskType.Translation
            && job.TargetLanguage == "zh-Hans");
    }

    [Fact]
    public async Task DailyReservationCountsDistinctEntriesAcrossTasksAndRestarts()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedAiAutomationJobRepository(database);
        DateOnly date = DateOnly.FromDateTime(Now.UtcDateTime);

        Assert.True(await repository.TryReserveDailyEntryAsync(
            date, FeedId, "entry-1", 1, Now, CancellationToken.None));
        Assert.True(await repository.TryReserveDailyEntryAsync(
            date, FeedId, "entry-1", 1, Now, CancellationToken.None));
        Assert.False(await repository.TryReserveDailyEntryAsync(
            date, FeedId, "entry-2", 1, Now, CancellationToken.None));
        Assert.True(await repository.TryReserveDailyEntryAsync(
            date.AddDays(1), FeedId, "entry-2", 1, Now.AddDays(1), CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredLeaseCanBeReclaimedAndRejectsStaleCompletion()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var entries = new FeedEntryRepository(database);
        FeedEntry entry = Entry("entry-1", 'a');
        await entries.UpsertAsync(FeedId, [entry], CancellationToken.None);
        var repository = new FeedAiAutomationJobRepository(database);
        await repository.EnqueueAsync(
            FeedId,
            [entry],
            new(true, true, false, "zh-Hans", 20, 1),
            Now,
            CancellationToken.None);

        FeedAiAutomationJob expired = Assert.Single(await repository.ClaimDueAsync(
            Now, 1, TimeSpan.FromMinutes(5), CancellationToken.None));
        FeedAiAutomationJob current = Assert.Single(await repository.ClaimDueAsync(
            Now.AddMinutes(6), 1, TimeSpan.FromMinutes(5), CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CompleteAsync(
            expired,
            FeedAiAutomationJobOutcome.Succeeded,
            null,
            Now.AddMinutes(6),
            CancellationToken.None));
        await repository.CompleteAsync(
            current,
            FeedAiAutomationJobOutcome.Succeeded,
            null,
            Now.AddMinutes(7),
            CancellationToken.None);
        Assert.Empty(await repository.ClaimDueAsync(
            Now.AddDays(1), 10, TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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

    private static FeedEntry Entry(string id, char hash) => new(
        id,
        FeedId,
        id,
        $"https://news.example/{id}",
        $"Title {id}",
        null,
        Now,
        Now,
        $"Summary {id}",
        $"<p>Content {id}</p>",
        [],
        [],
        new string(hash, 64),
        Now);
}
