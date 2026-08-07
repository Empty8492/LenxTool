using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class AppNotificationMigrationTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools notification migration tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VersionTwentyFourNotificationsGainClosedTargets()
    {
        await CreateVersionTwentyFourDatabaseAsync();
        using SqliteDatabase database = CreateDatabase();

        await database.InitializeAsync(CancellationToken.None);
        var repository = new AppNotificationRepository(database);
        IReadOnlyList<AppNotification> notifications =
            await repository.GetRecentAsync(20, CancellationToken.None);

        Assert.Collection(
            notifications.OrderBy(item => item.Id, StringComparer.Ordinal),
            item =>
            {
                Assert.Equal(AppNotificationKind.ContentMatch, item.Kind);
                Assert.Equal(
                    AppNotificationTargetKind.FeedEntry,
                    item.TargetKind);
                Assert.Equal("entry-content", item.TargetId);
            },
            item =>
            {
                Assert.Equal(AppNotificationKind.SystemHealth, item.Kind);
                Assert.Equal(AppNotificationTargetKind.None, item.TargetKind);
                Assert.Null(item.TargetId);
            },
            item =>
            {
                Assert.Equal(AppNotificationKind.TaskCompleted, item.Kind);
                Assert.Equal(
                    AppNotificationTargetKind.FeedEntry,
                    item.TargetKind);
                Assert.Equal("entry-task", item.TargetId);
            });

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand version = connection.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_versions;";
        Assert.Equal(
            25L,
            (long)(await version.ExecuteScalarAsync(
                CancellationToken.None))!);
    }

    [Fact]
    public async Task DatabaseConstraintRejectsMismatchedTargetShape()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_notifications(
                id, entry_id, feed_id, rule_id, rule_version,
                title, source_label, created_at, read_at, kind,
                target_kind, target_id)
            VALUES(
                $id, 'entry', 'feed', $ruleId, 1,
                '标题', '来源', $createdAt, NULL, 'CONTENT_MATCH',
                'NONE', 'entry');
            """;
        command.Parameters.AddWithValue("$id", new string('a', 64));
        command.Parameters.AddWithValue(
            "$ruleId",
            Guid.Empty.ToString("D"));
        command.Parameters.AddWithValue(
            "$createdAt",
            "2026-08-08T00:00:00.0000000+00:00");

        await Assert.ThrowsAsync<SqliteException>(() =>
            command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private async Task CreateVersionTwentyFourDatabaseAsync()
    {
        AppPaths paths = CreatePaths();
        paths.EnsureCreated();
        await using var connection = new SqliteConnection(
            $"Data Source={paths.DatabasePath};Pooling=False");
        await connection.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE schema_versions(
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL,
                checksum TEXT NOT NULL);
            INSERT INTO schema_versions(version, applied_at, checksum)
            VALUES(24, '2026-08-06T00:00:00Z', 'v24');

            CREATE TABLE app_notifications(
                id TEXT PRIMARY KEY CHECK(length(id) = 64),
                entry_id TEXT NOT NULL CHECK(length(entry_id) BETWEEN 1 AND 512),
                feed_id TEXT NOT NULL CHECK(length(feed_id) BETWEEN 1 AND 512),
                rule_id TEXT NOT NULL CHECK(length(rule_id) = 36),
                rule_version INTEGER NOT NULL CHECK(rule_version >= 1),
                title TEXT NOT NULL CHECK(length(title) BETWEEN 1 AND 1024),
                source_label TEXT NOT NULL CHECK(length(source_label) BETWEEN 1 AND 160),
                created_at TEXT NOT NULL CHECK(length(created_at) BETWEEN 20 AND 40),
                read_at TEXT,
                kind TEXT NOT NULL CHECK(kind IN (
                    'CONTENT_MATCH', 'SYSTEM_HEALTH', 'TASK_COMPLETED'))
            ) WITHOUT ROWID;
            CREATE INDEX ix_app_notifications_unread
                ON app_notifications(read_at, created_at DESC, id);
            CREATE INDEX ix_app_notifications_kind_created
                ON app_notifications(kind, created_at DESC, id);

            INSERT INTO app_notifications VALUES(
                $contentId, 'entry-content', 'feed', $ruleId, 1,
                '内容命中', '来源', $createdAt, NULL, 'CONTENT_MATCH');
            INSERT INTO app_notifications VALUES(
                $healthId, 'entry-health', 'feed', $ruleId, 1,
                '系统健康', '来源', $createdAt, NULL, 'SYSTEM_HEALTH');
            INSERT INTO app_notifications VALUES(
                $taskId, 'entry-task', 'feed', $ruleId, 1,
                '任务完成', '来源', $createdAt, NULL, 'TASK_COMPLETED');
            """;
        command.Parameters.AddWithValue("$contentId", new string('a', 64));
        command.Parameters.AddWithValue("$healthId", new string('b', 64));
        command.Parameters.AddWithValue("$taskId", new string('c', 64));
        command.Parameters.AddWithValue("$ruleId", Guid.Empty.ToString("D"));
        command.Parameters.AddWithValue(
            "$createdAt",
            "2026-08-08T00:00:00.0000000+00:00");
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private SqliteDatabase CreateDatabase() => new(
        CreatePaths(),
        NullLogger<SqliteDatabase>.Instance);

    private AppPaths CreatePaths() => new(_testRoot);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
