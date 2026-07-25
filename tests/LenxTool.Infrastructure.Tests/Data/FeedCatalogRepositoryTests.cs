using System.Globalization;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedCatalogRepositoryTests : IDisposable
{
    private const string EnabledCategoryId = "10000000-0000-4000-8000-000000000001";
    private const string DisabledCategoryId = "10000000-0000-4000-8000-000000000002";
    private const string EnabledFeedId = "20000000-0000-4000-8000-000000000001";
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools feed catalog tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NewDatabaseExposesEmptyActiveCatalogAndNoAdministratorSnapshot()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedCatalogRepository(database);

        FeedCatalogState state = await repository.GetStateAsync(CancellationToken.None);
        FeedCatalogSnapshot active = Assert.IsType<FeedCatalogSnapshot>(
            await repository.GetCatalogAsync(FeedCatalogScope.Active, CancellationToken.None));

        Assert.Equal(new FeedCatalogState(0, FeedCatalogScope.Active, null, null), state);
        Assert.Equal(state, active.State);
        Assert.Empty(active.Categories);
        Assert.Empty(active.Feeds);
        Assert.Null(await repository.GetCatalogAsync(FeedCatalogScope.All, CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceAsyncPersistsAllScopeAndReturnsDeterministicActiveProjection()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedCatalogRepository(database);
        FeedCatalogSnapshot snapshot = Catalog(
            version: 12,
            FeedCatalogScope.All,
            categories:
            [
                Category(DisabledCategoryId, "B 分类", 100, isEnabled: false),
                Category(EnabledCategoryId, "A 分类", 100, isEnabled: true)
            ],
            feeds:
            [
                Feed("20000000-0000-4000-8000-000000000004", "four", "未分类", null, 10, isEnabled: true),
                Feed("20000000-0000-4000-8000-000000000003", "three", "停用来源", EnabledCategoryId, 20, isEnabled: false),
                Feed("20000000-0000-4000-8000-000000000002", "two", "隐藏分类来源", DisabledCategoryId, 0, isEnabled: true),
                Feed(EnabledFeedId, "one", "启用来源", EnabledCategoryId, 10, isEnabled: true)
            ]);

        await repository.ReplaceAsync(snapshot, CancellationToken.None);

        Assert.Equal(snapshot.State, await repository.GetStateAsync(CancellationToken.None));
        FeedCatalogSnapshot all = Assert.IsType<FeedCatalogSnapshot>(
            await repository.GetCatalogAsync(FeedCatalogScope.All, CancellationToken.None));
        Assert.Equal([snapshot.Categories[1], snapshot.Categories[0]], all.Categories);
        Assert.Equal(
            [snapshot.Feeds[3], snapshot.Feeds[1], snapshot.Feeds[2], snapshot.Feeds[0]],
            all.Feeds);

        FeedCatalogSnapshot active = Assert.IsType<FeedCatalogSnapshot>(
            await repository.GetCatalogAsync(FeedCatalogScope.Active, CancellationToken.None));
        Assert.Equal(snapshot.State with { Scope = FeedCatalogScope.Active }, active.State);
        Assert.Equal([snapshot.Categories[1]], active.Categories);
        Assert.Equal(
            [snapshot.Feeds[3], snapshot.Feeds[0]],
            active.Feeds);
    }

    [Fact]
    public async Task ReplaceAsyncPersistsAiPolicyDefaultsAndResourceOverridesOffline()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedCatalogRepository(database);
        FeedAiPolicy defaults = FeedAiPolicy.SafeDefaults with { DailyEntryLimit = 30, MaxConcurrency = 2 };
        var categoryPolicy = FeedAiPolicy.Inherited with
        {
            ManualSummary = FeedAiPolicySwitch.Disabled,
            AutoSummary = FeedAiPolicySwitch.Enabled,
            DailyEntryLimit = 12
        };
        var feedPolicy = FeedAiPolicy.Inherited with
        {
            AutoTranslation = FeedAiPolicySwitch.Enabled,
            TranslationTargetLanguage = "ko",
            MaxConcurrency = 3
        };
        FeedCatalogSnapshot snapshot = Catalog(
            13,
            FeedCatalogScope.Active,
            [Category(EnabledCategoryId, "AI", 0, true) with { AiPolicy = categoryPolicy }],
            [Feed(EnabledFeedId, "ai", "AI Feed", EnabledCategoryId, 0, true) with { AiPolicy = feedPolicy }])
            with { AiPolicyDefaults = defaults };

        await repository.ReplaceAsync(snapshot, CancellationToken.None);

        FeedCatalogSnapshot stored = Assert.IsType<FeedCatalogSnapshot>(
            await repository.GetCatalogAsync(FeedCatalogScope.Active, CancellationToken.None));
        Assert.Equal(defaults, stored.AiPolicyDefaults);
        Assert.Equal(categoryPolicy, stored.Categories.Single().AiPolicy);
        Assert.Equal(feedPolicy, stored.Feeds.Single().AiPolicy);
    }

    [Fact]
    public async Task ReplaceAsyncRejectsVersionRegressionWithoutChangingStoredCatalog()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedCatalogRepository(database);
        FeedCatalogSnapshot original = Catalog(
            5,
            FeedCatalogScope.Active,
            [Category(EnabledCategoryId, "技术", 100, isEnabled: true)],
            [Feed(EnabledFeedId, "one", "来源一", EnabledCategoryId, 100, isEnabled: true)]);
        await repository.ReplaceAsync(original, CancellationToken.None);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReplaceAsync(Catalog(4, FeedCatalogScope.Active, [], []), CancellationToken.None));

        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
        FeedCatalogSnapshot stored = Assert.IsType<FeedCatalogSnapshot>(
            await repository.GetCatalogAsync(FeedCatalogScope.Active, CancellationToken.None));
        Assert.Equal(original.State, stored.State);
        Assert.Equal([EnabledCategoryId], stored.Categories.Select(category => category.Id));
        Assert.Equal([EnabledFeedId], stored.Feeds.Select(feed => feed.Id));
    }

    [Fact]
    public async Task ReplaceAsyncRollsBackPartialReplacementWhenAnInsertFails()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedCatalogRepository(database);
        FeedCatalogSnapshot original = Catalog(
            5,
            FeedCatalogScope.Active,
            [Category(EnabledCategoryId, "原分类", 100, isEnabled: true)],
            [Feed(EnabledFeedId, "original", "原来源", EnabledCategoryId, 100, isEnabled: true)]);
        await repository.ReplaceAsync(original, CancellationToken.None);
        await using (SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_catalog_insert
                BEFORE INSERT ON feed_catalog
                WHEN NEW.display_name = 'force-rollback'
                BEGIN
                    SELECT RAISE(ABORT, 'forced catalog rollback');
                END;
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        FeedCatalogSnapshot replacement = Catalog(
            6,
            FeedCatalogScope.Active,
            [Category(DisabledCategoryId, "新分类", 200, isEnabled: true)],
            [
                Feed("20000000-0000-4000-8000-000000000010", "new-one", "先写入", DisabledCategoryId, 10, isEnabled: true),
                Feed("20000000-0000-4000-8000-000000000011", "new-two", "force-rollback", DisabledCategoryId, 20, isEnabled: true)
            ]);

        await Assert.ThrowsAsync<SqliteException>(
            () => repository.ReplaceAsync(replacement, CancellationToken.None));

        FeedCatalogSnapshot stored = Assert.IsType<FeedCatalogSnapshot>(
            await repository.GetCatalogAsync(FeedCatalogScope.Active, CancellationToken.None));
        Assert.Equal(original.State, stored.State);
        Assert.Equal([EnabledCategoryId], stored.Categories.Select(category => category.Id));
        Assert.Equal([EnabledFeedId], stored.Feeds.Select(feed => feed.Id));
    }

    [Fact]
    public async Task ReplacingWithEmptyCatalogKeepsDownloadedEntriesForRemovedFeeds()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedCatalogRepository(database);
        await repository.ReplaceAsync(
            Catalog(
                1,
                FeedCatalogScope.Active,
                [Category(EnabledCategoryId, "技术", 100, isEnabled: true)],
                [Feed(EnabledFeedId, "one", "来源一", EnabledCategoryId, 100, isEnabled: true)]),
            CancellationToken.None);
        await using (SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO feed_entries(
                    id, feed_id, external_id, title, summary, sanitized_content,
                    enclosure_json, content_hash, fetched_at)
                VALUES(
                    'cached-entry', $feedId, 'entry-guid', '已缓存文章', '', '',
                    '[]', 'cached-entry-hash', '2026-07-22T03:00:00Z');
                """;
            command.Parameters.AddWithValue("$feedId", EnabledFeedId);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await repository.ReplaceAsync(Catalog(2, FeedCatalogScope.Active, [], []), CancellationToken.None);

        FeedCatalogSnapshot active = Assert.IsType<FeedCatalogSnapshot>(
            await repository.GetCatalogAsync(FeedCatalogScope.Active, CancellationToken.None));
        Assert.Equal(2, active.State.Version);
        Assert.Empty(active.Categories);
        Assert.Empty(active.Feeds);
        await using SqliteConnection verification = await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand count = verification.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM feed_entries WHERE id='cached-entry';";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task MarkSynchronizedOnlyUpdatesMatchingVersionTimestamp()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedCatalogRepository(database);
        FeedCatalogSnapshot original = Catalog(
            9,
            FeedCatalogScope.All,
            [Category(EnabledCategoryId, "技术", 100, isEnabled: true)],
            [Feed(EnabledFeedId, "one", "来源一", EnabledCategoryId, 100, isEnabled: true)]);
        await repository.ReplaceAsync(original, CancellationToken.None);
        DateTimeOffset synchronizedAt = DateTimeOffset.Parse(
            "2026-07-22T09:15:00Z",
            CultureInfo.InvariantCulture);

        await repository.MarkSynchronizedAsync(9, synchronizedAt, CancellationToken.None);

        FeedCatalogSnapshot stored = Assert.IsType<FeedCatalogSnapshot>(
            await repository.GetCatalogAsync(FeedCatalogScope.All, CancellationToken.None));
        Assert.Equal(synchronizedAt, stored.State.LastSyncedAt);
        Assert.Equal(original.State.GeneratedAt, stored.State.GeneratedAt);
        Assert.Equal(original.Categories, stored.Categories);
        Assert.Equal(original.Feeds, stored.Feeds);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.MarkSynchronizedAsync(8, synchronizedAt.AddMinutes(1), CancellationToken.None));
        Assert.Equal(
            synchronizedAt,
            (await repository.GetStateAsync(CancellationToken.None)).LastSyncedAt);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private SqliteDatabase CreateDatabase()
    {
        var paths = new AppPaths(_testRoot);
        return new(paths, NullLogger<SqliteDatabase>.Instance);
    }

    private static FeedCatalogSnapshot Catalog(
        long version,
        FeedCatalogScope scope,
        IReadOnlyList<FeedCategory> categories,
        IReadOnlyList<FeedCatalogItem> feeds)
    {
        return new(
            new(
                version,
                scope,
                DateTimeOffset.Parse("2026-07-22T02:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-07-22T02:01:00Z", CultureInfo.InvariantCulture)),
            categories,
            feeds);
    }

    private static FeedCategory Category(
        string id,
        string name,
        int sortOrder,
        bool isEnabled)
    {
        DateTimeOffset timestamp = DateTimeOffset.Parse(
            "2026-07-22T01:00:00Z",
            CultureInfo.InvariantCulture);
        return new(id, name, name.ToUpperInvariant(), sortOrder, isEnabled, 1, timestamp, timestamp);
    }

    private static FeedCatalogItem Feed(
        string id,
        string urlSuffix,
        string displayName,
        string? categoryId,
        int sortOrder,
        bool isEnabled)
    {
        DateTimeOffset timestamp = DateTimeOffset.Parse(
            "2026-07-22T01:00:00Z",
            CultureInfo.InvariantCulture);
        string url = $"https://{urlSuffix}.example/feed.xml";
        return new(
            id,
            url,
            url,
            displayName,
            $"https://{urlSuffix}.example/",
            categoryId,
            FeedViewKind.Article,
            60,
            sortOrder,
            isEnabled,
            1,
            timestamp,
            timestamp);
    }
}
