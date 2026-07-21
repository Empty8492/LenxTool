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
            "app_settings", "schema_versions", "content_fts"
        ];
        Assert.All(required, table => Assert.Contains(table, names));

        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT COUNT(*) FROM schema_versions";
        Assert.Equal(3L, (long)(await versionCommand.ExecuteScalarAsync(
            CancellationToken.None))!);
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
}
