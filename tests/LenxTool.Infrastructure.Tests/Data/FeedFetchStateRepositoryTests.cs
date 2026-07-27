using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedFetchStateRepositoryTests : IDisposable
{
    private const string CategoryId = "10000000-0000-4000-8000-000000000001";
    private const string DisabledCategoryId = "10000000-0000-4000-8000-000000000002";
    private const string FeedId = "30000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools feed fetch state tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DueQueryReturnsOnlyEnabledFeedsInEnabledCategories()
    {
        using SqliteDatabase database = await CreatePopulatedDatabaseAsync(
            Feed(FeedId, true, CategoryId),
            Feed("30000000-0000-4000-8000-000000000002", false, CategoryId),
            Feed("30000000-0000-4000-8000-000000000003", true, DisabledCategoryId),
            Feed("30000000-0000-4000-8000-000000000004", true, null));
        var repository = new FeedFetchStateRepository(database);

        IReadOnlyList<FeedRefreshTarget> due = await repository.GetDueTargetsAsync(
            Now,
            100,
            CancellationToken.None);

        Assert.Equal(
            [FeedId, "30000000-0000-4000-8000-000000000004"],
            due.Select(target => target.Feed.Id));
        Assert.Null(await repository.GetTargetAsync(
            "30000000-0000-4000-8000-000000000002",
            CancellationToken.None));
        Assert.Null(await repository.GetTargetAsync(
            "30000000-0000-4000-8000-000000000003",
            CancellationToken.None));
    }

    [Fact]
    public async Task HealthQueryReturnsEveryFeedAndRedactedFetchState()
    {
        using SqliteDatabase database = await CreatePopulatedDatabaseAsync(
            Feed(FeedId, true, CategoryId),
            Feed("30000000-0000-4000-8000-000000000002", false, CategoryId),
            Feed("30000000-0000-4000-8000-000000000003", true, DisabledCategoryId));
        var repository = new FeedFetchStateRepository(database);
        FeedFetchState failure = new(
            "30000000-0000-4000-8000-000000000002",
            null,
            null,
            Now.AddMinutes(10),
            null,
            Now,
            3,
            "http_503",
            Now);
        Assert.True(await repository.SaveStateAsync(failure, CancellationToken.None));

        IReadOnlyList<FeedRefreshTarget> health = await repository.GetAllTargetsAsync(
            CancellationToken.None);

        Assert.Equal(
            [FeedId, "30000000-0000-4000-8000-000000000002", "30000000-0000-4000-8000-000000000003"],
            health.Select(target => target.Feed.Id));
        FeedRefreshTarget failed = Assert.Single(
            health,
            target => target.Feed.Id == failure.FeedId);
        Assert.Equal(failure, failed.State);
        Assert.DoesNotContain("secret", failed.State?.ErrorCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StateRoundTripsAndFutureScheduleIsNotDue()
    {
        using SqliteDatabase database = await CreatePopulatedDatabaseAsync(Feed(FeedId, true, CategoryId));
        var repository = new FeedFetchStateRepository(database);
        var state = new FeedFetchState(
            FeedId,
            "\"etag-v1\"",
            "Tue, 21 Jul 2026 10:00:00 GMT",
            Now.AddHours(1),
            Now,
            Now.AddDays(-1),
            0,
            null,
            Now);

        Assert.True(await repository.SaveStateAsync(state, CancellationToken.None));
        FeedRefreshTarget stored = Assert.IsType<FeedRefreshTarget>(
            await repository.GetTargetAsync(FeedId, CancellationToken.None));

        Assert.Equal(state, stored.State);
        Assert.Empty(await repository.GetDueTargetsAsync(Now, 10, CancellationToken.None));
        Assert.Single(await repository.GetDueTargetsAsync(Now.AddHours(1), 10, CancellationToken.None));

        FeedFetchState failure = state with
        {
            NextFetchAt = Now.AddMinutes(5),
            LastFailureAt = Now,
            ConsecutiveFailures = 2,
            ErrorCode = "http_503",
            UpdatedAt = Now.AddMinutes(1)
        };
        Assert.True(await repository.SaveStateAsync(failure, CancellationToken.None));
        Assert.Equal(
            failure,
            (await repository.GetTargetAsync(FeedId, CancellationToken.None))?.State);
    }

    [Fact]
    public async Task RefreshTargetPreservesExplicitViewKindOverride()
    {
        FeedCatalogItem picture = Feed(FeedId, true, CategoryId) with
        {
            ViewKind = FeedViewKind.Picture,
            IsViewKindExplicit = true
        };
        using SqliteDatabase database = await CreatePopulatedDatabaseAsync(picture);
        var repository = new FeedFetchStateRepository(database);

        FeedRefreshTarget stored = Assert.IsType<FeedRefreshTarget>(
            await repository.GetTargetAsync(FeedId, CancellationToken.None));

        Assert.Equal(FeedViewKind.Picture, stored.Feed.ViewKind);
        Assert.True(stored.Feed.IsViewKindExplicit);
    }

    [Fact]
    public async Task SavingAfterCatalogRemovalReturnsFalseWithoutOrphanState()
    {
        using SqliteDatabase database = await CreatePopulatedDatabaseAsync(Feed(FeedId, true, CategoryId));
        var catalog = new FeedCatalogRepository(database);
        var repository = new FeedFetchStateRepository(database);
        await catalog.ReplaceAsync(Snapshot([]), CancellationToken.None);
        var state = new FeedFetchState(FeedId, null, null, Now, null, Now, 1, "network", Now);

        Assert.False(await repository.SaveStateAsync(state, CancellationToken.None));

        await using SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM feed_fetch_state;";
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(CancellationToken.None))!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private async Task<SqliteDatabase> CreatePopulatedDatabaseAsync(params FeedCatalogItem[] feeds)
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        await new FeedCatalogRepository(database).ReplaceAsync(Snapshot(feeds), CancellationToken.None);
        return database;
    }

    private static FeedCatalogSnapshot Snapshot(IReadOnlyList<FeedCatalogItem> feeds) => new(
        new(2, FeedCatalogScope.All, Now.AddHours(-1), Now),
        [
            new(CategoryId, "Enabled", "enabled", 1, true, 2, Now.AddDays(-1), Now),
            new(DisabledCategoryId, "Disabled", "disabled", 2, false, 2, Now.AddDays(-1), Now)
        ],
        feeds);

    private static FeedCatalogItem Feed(string id, bool enabled, string? categoryId) => new(
        id,
        $"https://feeds.example/{id}.xml",
        $"https://feeds.example/{id}.xml",
        id,
        "https://feeds.example/",
        categoryId,
        FeedViewKind.Article,
        60,
        id[^1],
        enabled,
        2,
        Now.AddDays(-1),
        Now);
}
