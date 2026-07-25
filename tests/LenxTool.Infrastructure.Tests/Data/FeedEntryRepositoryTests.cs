using System.Text;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedEntryRepositoryTests : IDisposable
{
    private const string CategoryId = "10000000-0000-4000-8000-000000000001";
    private const string SecondCategoryId = "10000000-0000-4000-8000-000000000002";
    private const string FeedId = "30000000-0000-4000-8000-000000000001";
    private const string SecondFeedId = "30000000-0000-4000-8000-000000000002";
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools feed entry repository tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UpsertIsSearchableAndRepeatedFetchDoesNotDuplicateEntry()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry original = Entry("stable", "Original", "quantum local model", Now.AddHours(-2));
        FeedEntry updated = original with
        {
            Title = "Updated",
            Summary = "quantum local model revised",
            ContentHash = new string('b', 64),
            FetchedAt = Now
        };

        await repository.UpsertAsync(FeedId, [original], CancellationToken.None);
        await repository.UpsertAsync(FeedId, [updated], CancellationToken.None);
        FeedEntry? byId = await repository.GetByIdAsync(
            updated.Id,
            CancellationToken.None);
        FeedEntryPage page = await repository.QueryAsync(
            new FeedEntryQuery("quantum", null, null, null, null, FeedEntryReadFilter.All, 0, 20),
            CancellationToken.None);

        FeedEntry item = Assert.Single(page.Items);
        Assert.NotNull(byId);
        Assert.Equal(updated.Id, byId.Id);
        Assert.Equal(updated.Title, byId.Title);
        Assert.Equal(updated.Summary, byId.Summary);
        Assert.Equal(updated.ContentHash, byId.ContentHash);
        Assert.Equal("Updated", item.Title);
        Assert.Equal(updated.ContentHash, item.ContentHash);
        Assert.False(page.HasMore);
        await using SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand fts = connection.CreateCommand();
        fts.CommandText = "SELECT COUNT(*) FROM content_fts WHERE entity_type='feed_entry' AND entity_id=$id;";
        fts.Parameters.AddWithValue("$id", item.Id);
        Assert.Equal(1L, (long)(await fts.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task QuerySupportsStablePagingAndPrivateStateFilters()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry newest = Entry("newest", "Newest", "alpha", Now.AddDays(-1));
        FeedEntry older = Entry("older", "Older", "beta", Now.AddDays(-2));
        FeedEntry otherFeed = Entry(
            "other",
            "Other feed",
            "gamma",
            Now.AddDays(-3),
            SecondFeedId);
        await repository.UpsertAsync(FeedId, [older, newest], CancellationToken.None);
        await repository.UpsertAsync(SecondFeedId, [otherFeed], CancellationToken.None);
        const string tagId = "50000000-0000-4000-8000-000000000001";
        await using (SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand privateState = connection.CreateCommand())
        {
            privateState.CommandText = """
                INSERT INTO user_entry_states(
                    entry_id, local_profile, is_read, is_starred, progress, note, updated_at)
                VALUES($readId, 'default', 1, 0, 0, '', $now),
                      ($stateFavoriteId, 'default', 0, 1, 0, '', $now),
                      ($repositoryFavoriteId, 'secondary', 1, 0, 0, '', $now);
                INSERT INTO favorites(id, entity_type, entity_id, note, created_at)
                VALUES('favorite-filter', 'feed_entry', $repositoryFavoriteId, '', $now);
                INSERT INTO tags(id, name, color, created_at)
                VALUES($tagId, '精读', '#4B6B88', $now);
                INSERT INTO entity_tags(entity_type, entity_id, tag_id)
                VALUES('feed_entry', $stateFavoriteId, $tagId);
                """;
            privateState.Parameters.AddWithValue("$readId", newest.Id);
            privateState.Parameters.AddWithValue("$stateFavoriteId", older.Id);
            privateState.Parameters.AddWithValue("$repositoryFavoriteId", otherFeed.Id);
            privateState.Parameters.AddWithValue("$tagId", tagId);
            privateState.Parameters.AddWithValue("$now", Now.ToString("O"));
            await privateState.ExecuteNonQueryAsync(CancellationToken.None);
        }

        FeedEntryPage first = await repository.QueryAsync(
            Query(feedId: FeedId, offset: 0, limit: 1),
            CancellationToken.None);
        FeedEntryPage second = await repository.QueryAsync(
            Query(feedId: FeedId, offset: 1, limit: 1),
            CancellationToken.None);
        FeedEntryPage category = await repository.QueryAsync(
            Query(categoryId: SecondCategoryId),
            CancellationToken.None);
        FeedEntryPage date = await repository.QueryAsync(
            Query(publishedFrom: Now.AddDays(-2), publishedBefore: Now),
            CancellationToken.None);
        FeedEntryPage unread = await repository.QueryAsync(
            Query(readFilter: FeedEntryReadFilter.Unread),
            CancellationToken.None);
        FeedEntryPage read = await repository.QueryAsync(
            Query(readFilter: FeedEntryReadFilter.Read),
            CancellationToken.None);
        FeedEntryPage secondaryRead = await repository.QueryAsync(
            Query(
                readFilter: FeedEntryReadFilter.Read,
                localProfile: "secondary"),
            CancellationToken.None);
        FeedEntryPage favorites = await repository.QueryAsync(
            Query(favoritesOnly: true),
            CancellationToken.None);
        FeedEntryPage tagged = await repository.QueryAsync(
            Query(tagId: tagId),
            CancellationToken.None);

        Assert.Equal(["newest"], first.Items.Select(item => item.ExternalId));
        Assert.True(first.HasMore);
        Assert.Equal(["older"], second.Items.Select(item => item.ExternalId));
        Assert.False(second.HasMore);
        Assert.Equal(["other"], category.Items.Select(item => item.ExternalId));
        Assert.Equal(["newest", "older"], date.Items.Select(item => item.ExternalId));
        Assert.Equal(["older", "other"], unread.Items.Select(item => item.ExternalId).Order().ToArray());
        Assert.Equal(["newest"], read.Items.Select(item => item.ExternalId));
        Assert.Equal(["other"], secondaryRead.Items.Select(item => item.ExternalId));
        Assert.Equal(
            ["older", "other"],
            favorites.Items.Select(item => item.ExternalId).Order().ToArray());
        Assert.Equal(["older"], tagged.Items.Select(item => item.ExternalId));
    }

    [Fact]
    public async Task RetentionDeletesOnlyOldEntriesWithoutPrivateStateAndCleansFts()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry expired = Entry("expired", "Expired", "remove-marker", Now.AddDays(-200));
        FeedEntry favorite = Entry("favorite", "Favorite", "favorite-marker", Now.AddDays(-210));
        FeedEntry tagged = Entry("tagged", "Tagged", "tagged-marker", Now.AddDays(-220));
        FeedEntry stateful = Entry("stateful", "Stateful", "stateful-marker", Now.AddDays(-230));
        FeedEntry recent = Entry("recent", "Recent", "recent-marker", Now.AddDays(-20));
        await repository.UpsertAsync(FeedId, [expired, favorite, tagged, stateful, recent], CancellationToken.None);
        await using (SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand state = connection.CreateCommand())
        {
            state.CommandText = """
                INSERT INTO favorites(id, entity_type, entity_id, note, created_at)
                VALUES('favorite-state', 'feed_entry', $favoriteId, 'keep', $now);
                INSERT INTO tags(id, name, color, created_at)
                VALUES('tag-state', 'keep', 'neutral', $now);
                INSERT INTO entity_tags(entity_type, entity_id, tag_id)
                VALUES('feed_entry', $taggedId, 'tag-state');
                INSERT INTO user_entry_states(
                    entry_id, local_profile, is_read, is_starred, progress, note, updated_at)
                VALUES($statefulId, 'default', 1, 0, 25, 'keep', $now);
                """;
            state.Parameters.AddWithValue("$favoriteId", favorite.Id);
            state.Parameters.AddWithValue("$taggedId", tagged.Id);
            state.Parameters.AddWithValue("$statefulId", stateful.Id);
            state.Parameters.AddWithValue("$now", Now.ToString("O"));
            await state.ExecuteNonQueryAsync(CancellationToken.None);
        }

        int deleted = await repository.DeleteExpiredUnprotectedAsync(
            Now.AddDays(-180),
            100,
            CancellationToken.None);

        Assert.Equal(1, deleted);
        FeedEntryPage remaining = await repository.QueryAsync(Query(), CancellationToken.None);
        Assert.Equal(
            ["favorite", "recent", "stateful", "tagged"],
            remaining.Items.Select(item => item.ExternalId).Order().ToArray());
        Assert.Empty((await repository.QueryAsync(
            Query(searchText: "remove-marker"),
            CancellationToken.None)).Items);
        Assert.Single((await repository.QueryAsync(
            Query(searchText: "favorite-marker"),
            CancellationToken.None)).Items);
    }

    [Fact]
    public async Task UnifiedContentSearchReturnsFeedEntryWithCatalogSource()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry entry = Entry(
            "unified",
            "Unified search entry",
            "rare-unified-marker",
            Now.AddHours(-1));
        await repository.UpsertAsync(FeedId, [entry], CancellationToken.None);

        IReadOnlyList<ContentSearchResult> results = await new NewsRepository(database)
            .SearchContentAsync("rare-unified", 20, CancellationToken.None);

        ContentSearchResult result = Assert.Single(results);
        Assert.Equal(ContentSearchResultType.FeedEntry, result.Type);
        Assert.Equal("Daily Feed", result.Source);
        Assert.Equal(entry.NormalizedUrl, result.Url);
        Assert.Equal("订阅条目", result.TypeLabel);
    }

    [Fact]
    public async Task ActiveOnlyQueryExcludesDisabledFeedsAndCategories()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedEntryRepository(database);
        FeedEntry active = Entry("active", "Active", "active-marker", Now.AddHours(-1), FeedId);
        FeedEntry disabled = Entry("disabled", "Disabled", "disabled-marker", Now.AddHours(-2), SecondFeedId);
        await repository.UpsertAsync(FeedId, [active], CancellationToken.None);
        await repository.UpsertAsync(SecondFeedId, [disabled], CancellationToken.None);

        await new FeedCatalogRepository(database).ReplaceAsync(new(
            new(2, FeedCatalogScope.Active, Now, Now),
            [
                new(CategoryId, "Technology", "technology", 1, true, 2, Now.AddDays(-1), Now),
                new(SecondCategoryId, "Science", "science", 2, false, 2, Now.AddDays(-1), Now)
            ],
            [
                CatalogFeed(FeedId, CategoryId, 1),
                CatalogFeed(SecondFeedId, SecondCategoryId, 2) with { IsEnabled = true }
            ]), CancellationToken.None);

        FeedEntryPage page = await repository.QueryAsync(
            Query(activeOnly: true),
            CancellationToken.None);

        Assert.Equal(["active"], page.Items.Select(item => item.ExternalId));

        await new FeedCatalogRepository(database).ReplaceAsync(new(
            new(3, FeedCatalogScope.Active, Now, Now),
            [
                new(CategoryId, "Technology", "technology", 1, true, 3, Now.AddDays(-1), Now),
                new(SecondCategoryId, "Science", "science", 2, true, 3, Now.AddDays(-1), Now)
            ],
            [
                CatalogFeed(FeedId, CategoryId, 1),
                CatalogFeed(SecondFeedId, SecondCategoryId, 2) with { IsEnabled = false }
            ]), CancellationToken.None);

        page = await repository.QueryAsync(Query(activeOnly: true), CancellationToken.None);

        Assert.Equal(["active"], page.Items.Select(item => item.ExternalId));
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
        var catalog = new FeedCatalogRepository(database);
        await catalog.ReplaceAsync(new(
            new(1, FeedCatalogScope.Active, Now.AddHours(-1), Now),
            [
                new(CategoryId, "Technology", "technology", 1, true, 1, Now.AddDays(-1), Now),
                new(SecondCategoryId, "Science", "science", 2, true, 1, Now.AddDays(-1), Now)
            ],
            [
                CatalogFeed(FeedId, CategoryId, 1),
                CatalogFeed(SecondFeedId, SecondCategoryId, 2)
            ]), CancellationToken.None);
        return database;
    }

    private static FeedCatalogItem CatalogFeed(string feedId, string categoryId, int sortOrder) => new(
        feedId,
        $"https://feeds.example/{feedId}.xml",
        $"https://feeds.example/{feedId}.xml",
        feedId == FeedId ? "Daily Feed" : "Second Feed",
        "https://feeds.example/",
        categoryId,
        FeedViewKind.Article,
        60,
        sortOrder,
        true,
        1,
        Now.AddDays(-1),
        Now);

    private static FeedEntry Entry(
        string externalId,
        string title,
        string content,
        DateTimeOffset publishedAt,
        string feedId = FeedId)
    {
        string xml = $"<rss version='2.0'><channel><title>x</title><item><guid>{externalId}</guid><title>{title}</title><pubDate>{publishedAt:R}</pubDate><description>{content}</description></item></channel></rss>";
        return Assert.Single(new FeedDocumentParser().Parse(
            feedId,
            $"https://feeds.example/{feedId}.xml",
            Encoding.UTF8.GetBytes(xml),
            Now).Entries);
    }

    private static FeedEntryQuery Query(
        string? searchText = null,
        string? feedId = null,
        string? categoryId = null,
        DateTimeOffset? publishedFrom = null,
        DateTimeOffset? publishedBefore = null,
        FeedEntryReadFilter readFilter = FeedEntryReadFilter.All,
        int offset = 0,
        int limit = 20,
        bool activeOnly = false,
        bool favoritesOnly = false,
        string? tagId = null,
        string localProfile = "default") => new(
            searchText,
            feedId,
            categoryId,
            publishedFrom,
            publishedBefore,
            readFilter,
            offset,
            limit,
            activeOnly,
            favoritesOnly,
            tagId,
            localProfile);
}
