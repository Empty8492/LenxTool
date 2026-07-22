using System.Globalization;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class SqliteDatabaseTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InitializeCreatesRequiredSchemaAndAppliesMigrationOnce()
    {
        SqliteDatabase database = CreateDatabase();

        await database.InitializeAsync(CancellationToken.None);
        await database.InitializeAsync(CancellationToken.None);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            CancellationToken.None);
        await using SqliteCommand tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','view')";

        var names = new HashSet<string>(StringComparer.Ordinal);
        await using (SqliteDataReader reader = await tablesCommand.ExecuteReaderAsync(
            CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                names.Add(reader.GetString(0));
            }
        }

        string[] required =
        [
            "news_articles", "trend_items", "ai_reports", "media_jobs",
            "subtitle_segments", "favorites", "tags", "entity_tags",
            "app_settings", "schema_versions", "content_fts",
            "feed_catalog_state", "feed_categories", "feed_catalog",
            "feed_fetch_state", "feed_entries", "feed_entry_search_documents"
        ];
        Assert.All(required, table => Assert.Contains(table, names));

        await using SqliteCommand indexesCommand = connection.CreateCommand();
        indexesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='index';";
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using (SqliteDataReader reader = await indexesCommand.ExecuteReaderAsync(
            CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                indexes.Add(reader.GetString(0));
            }
        }
        Assert.Contains("ux_feed_catalog_normalized_url", indexes);
        Assert.Contains("ux_feed_entries_feed_external_id", indexes);
        Assert.Contains("ix_feed_entries_normalized_url", indexes);
        Assert.Contains("ix_feed_entries_content_hash", indexes);

        await using SqliteCommand catalogStateCommand = connection.CreateCommand();
        catalogStateCommand.CommandText =
            "SELECT CAST(catalog_version AS TEXT) || ':' || scope FROM feed_catalog_state WHERE singleton_id=1;";
        Assert.Equal("0:ACTIVE", (string?)await catalogStateCommand.ExecuteScalarAsync(
            CancellationToken.None));

        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT COUNT(*) FROM schema_versions";
        Assert.Equal(4L, (long)(await versionCommand.ExecuteScalarAsync(
            CancellationToken.None))!);
    }

    [Fact]
    public async Task SchemaVersionTwoUpgradePreservesExistingDataAndAddsFeedSchema()
    {
        await CreateLegacySchemaVersionTwoAsync();
        using SqliteDatabase upgraded = CreateDatabase();

        await upgraded.InitializeAsync(CancellationToken.None);

        var news = new NewsRepository(upgraded);
        Assert.Equal("旧早报", Assert.Single(await news.GetLatestAsync(10, CancellationToken.None)).Title);
        Assert.Equal("旧热点", Assert.Single(await news.GetLatestTrendsAsync(10, null, CancellationToken.None)).Title);
        Assert.Equal("旧报告", Assert.Single(await news.GetLatestReportsAsync(10, CancellationToken.None)).Title);
        var media = new MediaJobRepository(upgraded);
        MediaJob mediaJob = Assert.Single(await media.GetRecentAsync(10, CancellationToken.None));
        Assert.Equal("legacy-job", mediaJob.Id);
        Assert.Equal(0, mediaJob.TranslationTotalTokens);
        var settings = new AppSettingsRepository(upgraded);
        Assert.Equal("True", await settings.GetAsync("appearance.dark_mode", CancellationToken.None));

        await using SqliteConnection connection = await upgraded.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand version = connection.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_versions;";
        Assert.Equal(4L, (long)(await version.ExecuteScalarAsync(CancellationToken.None))!);
        Assert.Single(Directory.GetFiles(CreatePaths().BackupDirectory, "lenx-pre-migration-*.db"));
    }

    [Fact]
    public async Task FeedEntryIdentityIsScopedToFeedWhileContentHashRemainsNonUnique()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await using SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO feed_categories(
                id, name, name_norm, sort_order, is_enabled, version, created_at, updated_at)
            VALUES(
                '10000000-0000-4000-8000-000000000100', '技术', '技术', 100, 1, 1,
                '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
            INSERT INTO feed_catalog(
                id, original_url, normalized_url, display_name, category_id, view_kind,
                refresh_interval_minutes, sort_order, is_enabled, version, created_at, updated_at)
            VALUES
                ('10000000-0000-4000-8000-000000000001', 'https://one.example/feed.xml',
                 'https://one.example/feed.xml', '来源一', '10000000-0000-4000-8000-000000000100',
                 'ARTICLE', 60, 100, 1, 1, '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z'),
                ('10000000-0000-4000-8000-000000000002', 'https://two.example/feed.xml',
                 'https://two.example/feed.xml', '来源二', '10000000-0000-4000-8000-000000000100',
                 'ARTICLE', 60, 200, 1, 1, '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
            INSERT INTO feed_entries(
                id, feed_id, external_id, normalized_url, title, author, published_at,
                summary, sanitized_content, enclosure_json, content_hash, fetched_at)
            VALUES
                ('entry-one', '10000000-0000-4000-8000-000000000001', 'shared-guid',
                 'https://story.example/item', '转载一', '作者', '2026-07-22T01:00:00Z',
                 '摘要', '正文', '[]', 'same-content-hash', '2026-07-22T02:00:00Z'),
                ('entry-two', '10000000-0000-4000-8000-000000000002', 'shared-guid',
                 'https://story.example/item', '转载二', '作者', '2026-07-22T01:00:00Z',
                 '摘要', '正文', '[]', 'same-content-hash', '2026-07-22T02:00:00Z');
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        command.CommandText = "SELECT COUNT(*) FROM feed_entries WHERE content_hash='same-content-hash';";
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);

        command.CommandText = """
            INSERT INTO feed_entries(
                id, feed_id, external_id, title, summary, sanitized_content,
                enclosure_json, content_hash, fetched_at)
            VALUES(
                'entry-duplicate', '10000000-0000-4000-8000-000000000001', 'shared-guid',
                '重复', '', '', '[]', 'different-hash', '2026-07-22T03:00:00Z');
            """;
        SqliteException duplicate = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(CancellationToken.None));
        Assert.Equal(19, duplicate.SqliteErrorCode);
    }

    [Fact]
    public async Task FeedSchemaMigrationFailureRollsBackSchemaAndVersion()
    {
        await CreateLegacySchemaVersionThreeWithFeedConflictAsync();
        using SqliteDatabase database = CreateDatabase();

        AppException exception = await Assert.ThrowsAsync<AppException>(
            () => database.InitializeAsync(CancellationToken.None));

        Assert.Equal(AppErrorCode.DatabaseMigrationFailed, exception.Error.Code);
        await using var connection = new SqliteConnection(
            $"Data Source={CreatePaths().DatabasePath};Pooling=False");
        await connection.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_versions;";
        Assert.Equal(3L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='feed_categories';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
        command.CommandText = "SELECT value FROM app_settings WHERE key='appearance.dark_mode';";
        Assert.Equal("True", (string?)await command.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NewsRepositoryUpsertsByFingerprintAndSearchesChineseContent()
    {
        SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        NewsRepository repository = new(database);
        DateTimeOffset fetchedAt = DateTimeOffset.Parse(
            "2026-07-19T08:00:00+08:00",
            CultureInfo.InvariantCulture);

        NewsArticle first = new(
            "news-1", new DateOnly(2026, 7, 19), "Lenx 早报", "人工智能进入本地时代",
            "摘要", "本地模型与云端服务开始协同。", "https://example.test/news/1", "hash-1", fetchedAt)
        {
            RichContent = "<h2>要闻</h2><a href=\"https://example.test/item\">详情</a>"
        };
        NewsArticle updated = first with { Id = "news-2", Summary = "更新后的摘要" };

        await repository.UpsertAsync([first, updated], CancellationToken.None);
        IReadOnlyList<NewsArticle> results = await repository.SearchAsync(
            "人工智能", 20, CancellationToken.None);

        NewsArticle item = Assert.Single(results);
        Assert.Equal("更新后的摘要", item.Summary);
        Assert.Equal(first.RichContent, item.RichContent);
        Assert.Equal("news-1", item.Id);
    }

    [Fact]
    public async Task NewsRepositorySearchContentReturnsNewsTrendsAndAiReports()
    {
        SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        NewsRepository repository = new(database);
        DateTimeOffset timestamp = DateTimeOffset.Parse(
            "2026-07-20T08:00:00+08:00",
            CultureInfo.InvariantCulture);

        await repository.UpsertAsync(
        [
            new(
                "news-search", new DateOnly(2026, 7, 20), "Lenx 早报", "人工智能新闻",
                "本地模型进展", "人工智能正在进入本地设备。", "https://example.test/news",
                "search-news-hash", timestamp)
        ], CancellationToken.None);
        await repository.UpsertTrendsAsync(
        [
            new(
                "trend-search", "GitHub", 1, "人工智能趋势", "4.2k stars",
                "https://example.test/trend", "search-trend-hash", timestamp)
        ], CancellationToken.None);

        await using (SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO ai_reports(
                    id, entity_type, entity_id, report_type, title, content, model,
                    request_count, token_usage, created_at)
                VALUES (
                    'report-search', 'daily', NULL, 'daily_trend', '人工智能报告',
                    '人工智能生态今日继续增长。', 'deepseek-chat', 1, 120, $createdAt);
                INSERT INTO content_fts(entity_type, entity_id, title, content)
                VALUES ('report', 'report-search', '人工智能报告', '人工智能生态今日继续增长。');
                """;
            command.Parameters.AddWithValue("$createdAt", timestamp.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        IReadOnlyList<ContentSearchResult> results = await repository.SearchContentAsync(
            "人工智能", 20, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal(
            [ContentSearchResultType.News, ContentSearchResultType.Trend, ContentSearchResultType.AiReport],
            results.Select(result => result.Type).Order().ToArray());
        Assert.Contains(results, result => result.Type == ContentSearchResultType.News && result.Source == "Lenx 早报");
        Assert.Contains(results, result => result.Type == ContentSearchResultType.Trend && result.Source == "GitHub");
        Assert.Contains(results, result => result.Type == ContentSearchResultType.AiReport && result.Source == "deepseek-chat");
    }

    [Fact]
    public async Task UpsertTrendsReplacesOnlyTheUpdatedPlatformSnapshot()
    {
        SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        NewsRepository repository = new(database);
        DateTimeOffset firstCapture = DateTimeOffset.Parse("2026-07-20T08:00:00+08:00", CultureInfo.InvariantCulture);
        DateTimeOffset secondCapture = firstCapture.AddMinutes(10);

        await repository.UpsertTrendsAsync(
        [
            new("zhihu-old", "知乎", 1, "旧知乎热点", "100 万", "https://zhihu.com/old", "zhihu-old", firstCapture),
            new("weibo-keep", "微博", 1, "微博热点", "200 万", "https://weibo.com/keep", "weibo-keep", firstCapture)
        ], CancellationToken.None);
        await repository.UpsertTrendsAsync(
        [
            new("zhihu-new", "知乎", 1, "新知乎热点", "120 万", "https://zhihu.com/new", "zhihu-new", secondCapture)
        ], CancellationToken.None);

        IReadOnlyList<TrendItem> stored = await repository.GetLatestTrendsAsync(20, null, CancellationToken.None);

        Assert.DoesNotContain(stored, item => item.Id == "zhihu-old");
        Assert.Contains(stored, item => item.Id == "zhihu-new");
        Assert.Contains(stored, item => item.Id == "weibo-keep");
    }

    [Fact]
    public async Task NewsRepositoryPersistsAiReportAndIndexesItsContent()
    {
        SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        NewsRepository repository = new(database);
        AiReport report = new(
            "report-1",
            "news",
            "news-1",
            "article_insight",
            "本地 AI 解读",
            "核心判断：端侧人工智能继续增长。",
            "deepseek-v4-flash",
            1,
            180,
            DateTimeOffset.Parse("2026-07-20T09:00:00+08:00", CultureInfo.InvariantCulture));

        await repository.UpsertReportAsync(report, CancellationToken.None);

        AiReport stored = Assert.Single(await repository.GetLatestReportsAsync(20, CancellationToken.None));
        Assert.Equal(report, stored);
        ContentSearchResult result = Assert.Single(await repository.SearchContentAsync(
            "端侧人工智能", 20, CancellationToken.None));
        Assert.Equal(ContentSearchResultType.AiReport, result.Type);
        Assert.Equal(report.Id, result.EntityId);
        Assert.Equal(report.Model, result.Source);
    }

    [Fact]
    public async Task AppSettingsRepositoryPersistsAndUpdatesValues()
    {
        SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new AppSettingsRepository(database);

        await repository.SetAsync("appearance.dark_mode", "True", CancellationToken.None);
        Assert.Equal("True", await repository.GetAsync("appearance.dark_mode", CancellationToken.None));

        await repository.SetAsync("appearance.dark_mode", "False", CancellationToken.None);
        Assert.Equal("False", await repository.GetAsync("appearance.dark_mode", CancellationToken.None));
        Assert.Null(await repository.GetAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task InitializeMapsCorruptedDatabaseToActionableError()
    {
        AppPaths paths = new(_testRoot);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllBytesAsync(
            paths.DatabasePath,
            [0x13, 0x37, 0x42, 0x00],
            CancellationToken.None);
        SqliteDatabase database = new(paths, NullLogger<SqliteDatabase>.Instance);

        AppException exception = await Assert.ThrowsAsync<AppException>(
            () => database.InitializeAsync(CancellationToken.None));

        Assert.Equal(AppErrorCode.DatabaseCorrupted, exception.Error.Code);
        Assert.Contains("备份", exception.Error.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigrationBackupIncludesCommittedRowsStillInWal()
    {
        SqliteDatabase original = CreateDatabase();
        await original.InitializeAsync(CancellationToken.None);

        await using SqliteConnection writer = await original.OpenConnectionAsync(CancellationToken.None);
        await using (SqliteCommand command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_autocheckpoint=0; INSERT INTO app_settings(key,value,updated_at) VALUES('wal-marker','present','2026-07-20T00:00:00Z');";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        Assert.True(File.Exists(CreatePaths().DatabasePath + "-wal"));

        SqliteDatabase reopened = CreateDatabase();
        await reopened.InitializeAsync(CancellationToken.None);

        string backup = Assert.Single(Directory.GetFiles(CreatePaths().BackupDirectory, "lenx-pre-migration-*.db"));
        await using var backupConnection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
        await backupConnection.OpenAsync(CancellationToken.None);
        await using SqliteCommand marker = backupConnection.CreateCommand();
        marker.CommandText = "SELECT value FROM app_settings WHERE key='wal-marker';";
        Assert.Equal("present", (string?)await marker.ExecuteScalarAsync(CancellationToken.None));
        await using SqliteCommand integrity = backupConnection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", (string?)await integrity.ExecuteScalarAsync(CancellationToken.None));
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
        return new(CreatePaths(), NullLogger<SqliteDatabase>.Instance);
    }

    private AppPaths CreatePaths() => new(_testRoot);

    private async Task CreateLegacySchemaVersionTwoAsync()
    {
        AppPaths paths = CreatePaths();
        paths.EnsureCreated();
        await using var connection = new SqliteConnection(
            $"Data Source={paths.DatabasePath};Pooling=False");
        await connection.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE schema_versions(
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL,
                checksum TEXT NOT NULL
            );
            INSERT INTO schema_versions(version, applied_at, checksum) VALUES
                (1, '2026-07-19T00:00:00Z', 'lenx-schema-v1'),
                (2, '2026-07-20T00:00:00Z', 'lenx-schema-v2-rich-news');
            CREATE TABLE news_articles(
                id TEXT PRIMARY KEY,
                published_date TEXT NOT NULL,
                source TEXT NOT NULL,
                title TEXT NOT NULL,
                summary TEXT NOT NULL DEFAULT '',
                content TEXT NOT NULL DEFAULT '',
                url TEXT NOT NULL,
                content_hash TEXT NOT NULL UNIQUE,
                fetched_at TEXT NOT NULL,
                rich_content TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE trend_items(
                id TEXT PRIMARY KEY,
                platform TEXT NOT NULL,
                rank INTEGER NOT NULL,
                title TEXT NOT NULL,
                heat TEXT NOT NULL DEFAULT '',
                url TEXT NOT NULL,
                content_hash TEXT NOT NULL UNIQUE,
                captured_at TEXT NOT NULL
            );
            CREATE TABLE ai_reports(
                id TEXT PRIMARY KEY,
                entity_type TEXT NOT NULL,
                entity_id TEXT,
                report_type TEXT NOT NULL,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                model TEXT NOT NULL,
                request_count INTEGER NOT NULL DEFAULT 1,
                token_usage INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            CREATE TABLE media_jobs(
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                input_path TEXT NOT NULL,
                output_path TEXT,
                status TEXT NOT NULL,
                progress REAL NOT NULL DEFAULT 0,
                engine TEXT NOT NULL,
                model TEXT,
                shared_usage_seconds REAL NOT NULL DEFAULT 0,
                ai_request_count INTEGER NOT NULL DEFAULT 0,
                error_json TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE app_settings(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            INSERT INTO news_articles(
                id, published_date, source, title, summary, content, url,
                content_hash, fetched_at, rich_content)
            VALUES(
                'legacy-news', '2026-07-20', '旧来源', '旧早报', '旧摘要', '旧正文',
                'https://example.test/legacy-news', 'legacy-news-hash',
                '2026-07-20T01:00:00Z', '<p>旧富文本</p>');
            INSERT INTO trend_items(
                id, platform, rank, title, heat, url, content_hash, captured_at)
            VALUES(
                'legacy-trend', '旧平台', 1, '旧热点', '100',
                'https://example.test/legacy-trend', 'legacy-trend-hash',
                '2026-07-20T01:00:00Z');
            INSERT INTO ai_reports(
                id, entity_type, report_type, title, content, model,
                request_count, token_usage, created_at)
            VALUES(
                'legacy-report', 'daily', 'daily_trend', '旧报告', '旧报告正文',
                'deepseek-v4-flash', 1, 12, '2026-07-20T01:00:00Z');
            INSERT INTO media_jobs(
                id, kind, input_path, status, progress, engine,
                shared_usage_seconds, ai_request_count, created_at, updated_at)
            VALUES(
                'legacy-job', 'Transcription', 'D:\\旧任务.wav', 'Completed', 100,
                'Groq', 0, 1, '2026-07-20T01:00:00Z', '2026-07-20T02:00:00Z');
            INSERT INTO app_settings(key, value, updated_at)
            VALUES('appearance.dark_mode', 'True', '2026-07-20T01:00:00Z');
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task CreateLegacySchemaVersionThreeWithFeedConflictAsync()
    {
        await CreateLegacySchemaVersionTwoAsync();
        await using var connection = new SqliteConnection(
            $"Data Source={CreatePaths().DatabasePath};Pooling=False");
        await connection.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE media_jobs ADD COLUMN translation_provider TEXT;
            ALTER TABLE media_jobs ADD COLUMN translation_target_language TEXT;
            ALTER TABLE media_jobs ADD COLUMN translation_next_segment_index INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE media_jobs ADD COLUMN translation_prompt_tokens INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE media_jobs ADD COLUMN translation_completion_tokens INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE media_jobs ADD COLUMN translation_total_tokens INTEGER NOT NULL DEFAULT 0;
            INSERT INTO schema_versions(version, applied_at, checksum)
            VALUES(3, '2026-07-21T00:00:00Z', 'lenx-schema-v3-subtitle-translation-usage');
            CREATE TABLE feed_entries(id TEXT PRIMARY KEY);
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
