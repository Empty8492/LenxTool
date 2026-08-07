using System.Globalization;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class ContentSearchRepositoryTests : IDisposable
{
    private const string CategoryId =
        "10000000-0000-4000-8000-000000000001";
    private const string FeedId =
        "30000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 8, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools content search tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SearchSupportsMixedTypesAndCombinedFeedPrivateFilters()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var news = new NewsRepository(database);
        var media = new MediaJobRepository(database);
        var favorites = new FavoriteRepository(database);
        await SeedFeedEntryAsync(database, "entry-orion", "Orion Feed", "orion");
        await news.UpsertAsync(
        [
            new(
                "news-orion",
                new DateOnly(2026, 7, 26),
                "Lenx 早报",
                "Orion News",
                "orion",
                "orion",
                "https://example.test/news/orion",
                "news-orion-hash",
                Now.AddDays(-1))
        ], CancellationToken.None);
        await news.UpsertTrendsAsync(
        [
            new(
                "trend-orion",
                "GitHub",
                1,
                "Orion Trend",
                "orion",
                "https://example.test/trend/orion",
                "trend-orion-hash",
                Now.AddHours(-4))
        ], CancellationToken.None);
        await news.UpsertReportAsync(new(
            "report-orion",
            "daily",
            null,
            "daily_trend",
            "Orion Report",
            "orion",
            "deepseek",
            1,
            12,
            Now.AddHours(-3)), CancellationToken.None);
        await media.CreateMediaJobWithSegmentsAsync(
            MediaJob("job-orion", "C:\\Media\\orion.srt"),
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "orion subtitle")],
            CancellationToken.None);
        FavoriteItem favorite = await favorites.UpsertAsync(
            "feed_entry",
            "entry-orion",
            "orion favorite",
            CancellationToken.None);
        TagItem tag = await favorites.AddTagAsync(
            "feed_entry",
            "entry-orion",
            "orion tag",
            "#4B6B88",
            CancellationToken.None);

        ContentSearchPage mixed = await news.SearchContentAsync(
            new("orion", Limit: 20),
            CancellationToken.None);
        ContentSearchPage subtitle = await news.SearchContentAsync(
            new("orion", Type: ContentSearchResultType.Subtitle, Limit: 20),
            CancellationToken.None);
        ContentSearchPage filtered = await news.SearchContentAsync(
            new(
                "orion",
                Type: ContentSearchResultType.FeedEntry,
                PublishedFrom: Now.AddDays(-2),
                PublishedBefore: Now.AddDays(1),
                FeedId: FeedId,
                CategoryId: CategoryId,
                TagId: tag.Id,
                FavoritesOnly: true,
                Limit: 20),
            CancellationToken.None);

        Assert.Contains(
            Enum.GetValues<ContentSearchResultType>(),
            type => mixed.Items.Any(item => item.Type == type));
        ContentSearchResult subtitleItem = Assert.Single(subtitle.Items);
        Assert.Equal("job-orion", subtitleItem.EntityId);
        Assert.Equal("orion.srt", subtitleItem.Title);
        Assert.Equal("entry-orion", Assert.Single(filtered.Items).EntityId);
        Assert.Contains(
            mixed.Items,
            item => item.Type == ContentSearchResultType.Favorite
                    && item.EntityId == favorite.EntityId);
        Assert.False(mixed.HasMore);
    }

    [Fact]
    public async Task SearchPagingIsStableAndRejectsInvalidBounds()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var news = new NewsRepository(database);
        await SeedFeedEntryAsync(database, "entry-c", "Stable C", "stableterm");
        await SeedFeedEntryAsync(database, "entry-a", "Stable A", "stableterm");
        await SeedFeedEntryAsync(database, "entry-b", "Stable B", "stableterm");

        ContentSearchPage first = await news.SearchContentAsync(
            new("stableterm", Offset: 0, Limit: 2),
            CancellationToken.None);
        ContentSearchPage second = await news.SearchContentAsync(
            new("stableterm", Offset: 2, Limit: 2),
            CancellationToken.None);

        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(
            ["entry-a", "entry-b", "entry-c"],
            first.Items.Concat(second.Items).Select(item => item.EntityId));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            news.SearchContentAsync(
                new(" ", Limit: 20),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            news.SearchContentAsync(
                new("stableterm", Offset: -1, Limit: 20),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            news.SearchContentAsync(
                new(
                    "stableterm",
                    PublishedFrom: Now,
                    PublishedBefore: Now),
                CancellationToken.None));
    }

    [Fact]
    public async Task SubtitleFavoriteAndTagIndexesTrackUpdatesAndDeletes()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var news = new NewsRepository(database);
        var media = new MediaJobRepository(database);
        var favorites = new FavoriteRepository(database);
        await SeedFeedEntryAsync(database, "entry-private", "Private", "private");
        await media.CreateMediaJobWithSegmentsAsync(
            MediaJob("job-sync", "C:\\Media\\sync.srt"),
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "subtitleold")],
            CancellationToken.None);
        await favorites.UpsertAsync(
            "feed_entry",
            "entry-private",
            "favoriteold",
            CancellationToken.None);
        TagItem tag = await favorites.UpsertTagAsync(
            "tagold",
            "#4B6B88",
            CancellationToken.None);

        await media.ReplaceAsync(
            "job-sync",
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "subtitlenew")],
            CancellationToken.None);
        await favorites.UpsertAsync(
            "feed_entry",
            "entry-private",
            "favoritenew",
            CancellationToken.None);
        await favorites.DeleteTagAsync(tag.Id, CancellationToken.None);

        Assert.Empty((await news.SearchContentAsync(
            new("subtitleold"),
            CancellationToken.None)).Items);
        Assert.Single((await news.SearchContentAsync(
            new("subtitlenew", Type: ContentSearchResultType.Subtitle),
            CancellationToken.None)).Items);
        Assert.Empty((await news.SearchContentAsync(
            new("favoriteold"),
            CancellationToken.None)).Items);
        Assert.Single((await news.SearchContentAsync(
            new("favoritenew", Type: ContentSearchResultType.Favorite),
            CancellationToken.None)).Items);
        Assert.Empty((await news.SearchContentAsync(
            new("tagold"),
            CancellationToken.None)).Items);

        await DeleteMediaJobAsync(database, "job-sync");
        await favorites.RemoveAsync(
            "feed_entry",
            "entry-private",
            CancellationToken.None);

        Assert.Empty((await news.SearchContentAsync(
            new("subtitlenew"),
            CancellationToken.None)).Items);
        Assert.Empty((await news.SearchContentAsync(
            new("favoritenew"),
            CancellationToken.None)).Items);
    }

    [Fact]
    public async Task VersionSixteenUpgradeBackfillsPrivateAndSubtitleSearchDocuments()
    {
        using (var versionSixteen = new SqliteDatabase(
                   new AppPaths(_testRoot),
                   NullLogger<SqliteDatabase>.Instance))
        {
            await versionSixteen.InitializeAsync(CancellationToken.None);
            await using SqliteConnection connection =
                await versionSixteen.OpenConnectionAsync(CancellationToken.None);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER media_jobs_fts_delete;
                DROP TRIGGER favorites_fts_insert;
                DROP TRIGGER favorites_fts_update;
                DROP TRIGGER favorites_fts_delete;
                DROP TRIGGER tags_fts_insert;
                DROP TRIGGER tags_fts_update;
                DROP TRIGGER tags_fts_delete;
                DELETE FROM content_fts
                WHERE entity_type IN ('subtitle', 'favorite', 'tag');
                ALTER TABLE feed_catalog DROP COLUMN view_kind_explicit;
                DROP INDEX ix_app_notifications_kind_created;
                ALTER TABLE app_notifications DROP COLUMN kind;
                DROP TABLE feed_smart_views;
                DROP TABLE feed_smart_view_state;
                DROP TABLE entry_export_tasks;
                DROP TABLE feed_digest_requests;
                DROP TABLE local_schedule_run_retries;
                DROP TABLE local_scheduled_task_payloads;
                DROP TABLE local_schedule_runs;
                DROP TABLE local_scheduled_tasks;
                DELETE FROM schema_versions WHERE version>=17;

                INSERT INTO media_jobs(
                    id, kind, input_path, status, progress, engine,
                    shared_usage_seconds, ai_request_count, created_at, updated_at)
                VALUES(
                    'migration-job', 'SubtitleImport', 'C:\Media\migration.srt',
                    'Completed', 100, 'ImportedSrt', 0, 0, $now, $now);
                INSERT INTO subtitle_segments(
                    media_job_id, sequence, start_ms, end_ms, text)
                VALUES('migration-job', 1, 0, 1000, 'migrationterm');
                INSERT INTO favorites(
                    id, entity_type, entity_id, note, created_at)
                VALUES(
                    'migration-favorite', 'feed_entry', 'migration-entry',
                    'migrationterm', $now);
                INSERT INTO tags(id, name, color, created_at)
                VALUES(
                    'migration-tag', 'migrationterm', '#4B6B88', $now);
                """;
            command.Parameters.AddWithValue("$now", Format(Now));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        using var upgraded = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await upgraded.InitializeAsync(CancellationToken.None);

        ContentSearchPage page = await new NewsRepository(upgraded)
            .SearchContentAsync(new("migrationterm", Limit: 20), CancellationToken.None);
        Assert.Equal(
            [
                ContentSearchResultType.Subtitle,
                ContentSearchResultType.Tag,
                ContentSearchResultType.Favorite
            ],
            page.Items.Select(item => item.Type).Order().ToArray());
        await using SqliteConnection verification =
            await upgraded.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand version = verification.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_versions;";
        Assert.Equal(
            25L,
            (long)(await version.ExecuteScalarAsync(CancellationToken.None))!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO feed_categories(
                id, name, name_norm, sort_order, is_enabled, version,
                created_at, updated_at)
            VALUES(
                $categoryId, 'Technology', 'technology', 1, 1, 1,
                $now, $now);
            INSERT INTO feed_catalog(
                id, original_url, normalized_url, display_name, site_url,
                category_id, view_kind, refresh_interval_minutes, sort_order,
                is_enabled, version, created_at, updated_at)
            VALUES(
                $feedId, 'https://feeds.example/orion.xml',
                'https://feeds.example/orion.xml', 'Orion Feed',
                'https://feeds.example/', $categoryId, 'ARTICLE', 60, 1,
                1, 1, $now, $now);
            """;
        command.Parameters.AddWithValue("$categoryId", CategoryId);
        command.Parameters.AddWithValue("$feedId", FeedId);
        command.Parameters.AddWithValue("$now", Format(Now));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        return database;
    }

    private static async Task SeedFeedEntryAsync(
        SqliteDatabase database,
        string id,
        string title,
        string content)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO feed_entries(
                id, feed_id, external_id, normalized_url, title, author,
                published_at, updated_at, summary, sanitized_content,
                enclosure_json, content_hash, fetched_at)
            VALUES(
                $id, $feedId, $id, $url, $title, 'Lenx',
                $timestamp, $timestamp, $content, $content,
                '[]', $hash, $timestamp);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$feedId", FeedId);
        command.Parameters.AddWithValue(
            "$url",
            $"https://feeds.example/entries/{id}");
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$hash", $"hash-{id}");
        command.Parameters.AddWithValue("$timestamp", Format(Now));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task DeleteMediaJobAsync(
        SqliteDatabase database,
        string jobId)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM media_jobs WHERE id=$id;";
        command.Parameters.AddWithValue("$id", jobId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static MediaJob MediaJob(string id, string inputPath) => new(
        id,
        "SubtitleImport",
        inputPath,
        null,
        MediaJobStatus.Completed,
        100,
        TranscriptionEngine.ImportedSrt,
        null,
        0,
        0,
        null,
        Now,
        Now);

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
