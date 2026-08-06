using System.Globalization;
using LenxTool.Core.Errors;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LenxTool.Infrastructure.Data;

public sealed partial class SqliteDatabase(
    AppPaths paths,
    ILogger<SqliteDatabase> logger) : IDisposable
{
    private const int CurrentSchemaVersion = 24;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return;

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            paths.EnsureCreated();

            bool databaseExists = File.Exists(paths.DatabasePath) && new FileInfo(paths.DatabasePath).Length > 0;
            await using SqliteConnection connection = await OpenConfiguredConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (databaseExists)
            {
                await BackupBeforeMigrationAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            await ApplyMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26)
        {
            throw new AppException(
                new(
                    AppErrorCode.DatabaseCorrupted,
                    "本地数据库已损坏",
                    "Lenx Tools 无法读取本地数据库，已停止写入以保护现有数据。",
                    "请从历史备份恢复；如无可用备份，请保留损坏文件后新建数据库。",
                    exception.Message,
                    "SQLite",
                    IsRetryable: false),
                exception);
        }
        catch (SqliteException exception)
        {
            throw new AppException(
                new(
                    AppErrorCode.DatabaseMigrationFailed,
                    "数据库升级失败",
                    "本地数据库结构未能完成升级，原版本号不会被提升。",
                    "请检查磁盘空间与目录权限，然后重试或从自动备份恢复。",
                    exception.Message,
                    "SQLite",
                    IsRetryable: true),
                exception);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        return await OpenConfiguredConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _initializationGate.Dispose();
        _disposed = true;
    }

    private async Task<SqliteConnection> OpenConfiguredConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ApplyMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrationOneSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_versions;";
        long version = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (version < CurrentSchemaVersion)
        {
            if (version < 1)
            {
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (1, $appliedAt, $checksum);";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v1");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                version = 1;
            }

            if (version < 2)
            {
                command.CommandText = "ALTER TABLE news_articles ADD COLUMN rich_content TEXT NOT NULL DEFAULT '';";
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (2, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v2-rich-news");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                version = 2;
            }

            if (version < 3)
            {
                command.CommandText = """
                    ALTER TABLE media_jobs ADD COLUMN translation_provider TEXT;
                    ALTER TABLE media_jobs ADD COLUMN translation_target_language TEXT;
                    ALTER TABLE media_jobs ADD COLUMN translation_next_segment_index INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE media_jobs ADD COLUMN translation_prompt_tokens INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE media_jobs ADD COLUMN translation_completion_tokens INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE media_jobs ADD COLUMN translation_total_tokens INTEGER NOT NULL DEFAULT 0;
                    """;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (3, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v3-subtitle-translation-usage");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                version = 3;
            }

            if (version < 4)
            {
                command.CommandText = MigrationFourSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (4, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v4-feed-catalog-and-entries");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                version = 4;
            }

            if (version < 5)
            {
                command.CommandText = MigrationFiveSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (5, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v5-feed-entry-fts");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (version < 6)
            {
                command.CommandText = MigrationSixSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (6, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v6-private-entry-state");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (version < 7)
            {
                command.CommandText = MigrationSevenSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (7, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v7-entry-assets");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (version < 8)
            {
                command.CommandText = MigrationEightSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (8, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v8-feed-full-text-queue");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (version < 9)
            {
                command.CommandText = MigrationNineSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (9, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v9-feed-ai-cache");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (version < 10)
            {
                command.CommandText = MigrationTenSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (10, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v10-feed-ai-policy");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (version < 11)
            {
                command.CommandText = MigrationElevenSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (11, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v11-feed-ai-automation");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                version = 11;
            }

            if (version < 12)
            {
                command.CommandText = MigrationTwelveSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (12, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v12-feed-automation-runs");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                version = 12;
            }

            if (version < 13)
            {
                command.CommandText = MigrationThirteenSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (13, $appliedAt, $checksum);";
                command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$checksum", "lenx-schema-v13-private-hidden-state");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                version = 13;
            }

            if (version < 14)
            {
                command.CommandText = MigrationFourteenSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (14, $appliedAt, $checksum);";
                command.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue(
                    "$checksum",
                    "lenx-schema-v14-feed-automation-rule-cache");
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                version = 14;
            }

            if (version < 15)
            {
                command.CommandText = MigrationFifteenSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (15, $appliedAt, $checksum);";
                command.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue(
                    "$checksum",
                    "lenx-schema-v15-feed-media-deliveries");
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                version = 15;
            }

            if (version < 16)
            {
                command.CommandText = MigrationSixteenSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (16, $appliedAt, $checksum);";
                command.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue(
                    "$checksum",
                    "lenx-schema-v16-app-notifications");
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (version < 17)
            {
                command.CommandText = MigrationSeventeenSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (17, $appliedAt, $checksum);";
                command.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue(
                    "$checksum",
                    "lenx-schema-v17-unified-content-search");
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                version = 17;
            }

            if (version < 18)
            {
                command.CommandText = MigrationEighteenSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (18, $appliedAt, $checksum);";
                command.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue(
                    "$checksum",
                    "lenx-schema-v18-explicit-feed-view-kind");
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                version = 18;
            }

            if (version < 19)
            {
                command.CommandText = MigrationNineteenSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (19, $appliedAt, $checksum);";
                command.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue(
                    "$checksum",
                    "lenx-schema-v19-notification-kinds");
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                version = 19;
            }

            if (version < 20)
            {
                command.CommandText = MigrationTwentySql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (20, $appliedAt, $checksum);";
                command.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue(
                    "$checksum",
                    "lenx-schema-v20-feed-smart-views");
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                version = 20;
            }

            if (version < 21)
            {
                command.CommandText = MigrationTwentyOneSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (21, $appliedAt, $checksum);";
                command.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue(
                    "$checksum",
                    "lenx-schema-v21-entry-export-queue");
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                version = 21;
            }

            if (version < 22)
            {
                command.CommandText = MigrationTwentyTwoSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (22, $appliedAt, $checksum);";
                command.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue(
                    "$checksum",
                    "lenx-schema-v22-local-scheduled-tasks");
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                version = 22;
            }

            if (version < 23)
            {
                command.CommandText = MigrationTwentyThreeSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (23, $appliedAt, $checksum);";
                command.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue(
                    "$checksum",
                    "lenx-schema-v23-local-schedule-runs");
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                version = 23;
            }

            if (version < 24)
            {
                command.CommandText = MigrationTwentyFourSql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                command.CommandText = "INSERT INTO schema_versions(version, applied_at, checksum) VALUES (24, $appliedAt, $checksum);";
                command.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue(
                    "$checksum",
                    "lenx-schema-v24-feed-digest-execution");
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task BackupBeforeMigrationAsync(
        SqliteConnection source,
        CancellationToken cancellationToken)
    {
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string destination = Path.Combine(paths.BackupDirectory, $"lenx-pre-migration-{timestamp}.db");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = destination,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        try
        {
            await using var target = new SqliteConnection(builder.ToString());
            await target.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(target);
            LogMigrationBackupCreated(logger, Path.GetFileName(destination));
        }
        catch
        {
            try { File.Delete(destination); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    [LoggerMessage(1001, LogLevel.Information, "Created pre-migration database backup {BackupFileName}")]
    private static partial void LogMigrationBackupCreated(ILogger logger, string backupFileName);

    private const string MigrationOneSql = """
        CREATE TABLE IF NOT EXISTS schema_versions(
            version INTEGER PRIMARY KEY,
            applied_at TEXT NOT NULL,
            checksum TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS news_articles(
            id TEXT PRIMARY KEY,
            published_date TEXT NOT NULL,
            source TEXT NOT NULL,
            title TEXT NOT NULL,
            summary TEXT NOT NULL DEFAULT '',
            content TEXT NOT NULL DEFAULT '',
            url TEXT NOT NULL,
            content_hash TEXT NOT NULL UNIQUE,
            fetched_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS trend_items(
            id TEXT PRIMARY KEY,
            platform TEXT NOT NULL,
            rank INTEGER NOT NULL,
            title TEXT NOT NULL,
            heat TEXT NOT NULL DEFAULT '',
            url TEXT NOT NULL,
            content_hash TEXT NOT NULL UNIQUE,
            captured_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS ai_reports(
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
        CREATE TABLE IF NOT EXISTS media_jobs(
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
        CREATE TABLE IF NOT EXISTS subtitle_segments(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            media_job_id TEXT NOT NULL REFERENCES media_jobs(id) ON DELETE CASCADE,
            sequence INTEGER NOT NULL,
            start_ms INTEGER NOT NULL,
            end_ms INTEGER NOT NULL,
            text TEXT NOT NULL,
            translated_text TEXT,
            avg_log_probability REAL,
            no_speech_probability REAL,
            UNIQUE(media_job_id, sequence)
        );
        CREATE TABLE IF NOT EXISTS favorites(
            id TEXT PRIMARY KEY,
            entity_type TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            note TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            UNIQUE(entity_type, entity_id)
        );
        CREATE TABLE IF NOT EXISTS tags(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            color TEXT NOT NULL DEFAULT 'neutral',
            created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS entity_tags(
            entity_type TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            tag_id TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            PRIMARY KEY(entity_type, entity_id, tag_id)
        );
        CREATE TABLE IF NOT EXISTS app_settings(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS content_fts USING fts5(
            entity_type UNINDEXED,
            entity_id UNINDEXED,
            title,
            content,
            tokenize='unicode61 remove_diacritics 2'
        );
        CREATE INDEX IF NOT EXISTS ix_news_articles_published_date ON news_articles(published_date DESC);
        CREATE INDEX IF NOT EXISTS ix_trend_items_platform_captured ON trend_items(platform, captured_at DESC);
        CREATE INDEX IF NOT EXISTS ix_media_jobs_updated_at ON media_jobs(updated_at DESC);
        CREATE INDEX IF NOT EXISTS ix_ai_reports_created_at ON ai_reports(created_at DESC);
        """;

    private const string MigrationFourSql = """
        CREATE TABLE feed_catalog_state(
            singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
            catalog_version INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(catalog_version) = 'integer' AND catalog_version >= 0),
            scope TEXT NOT NULL DEFAULT 'ACTIVE'
                CHECK(scope IN ('ACTIVE', 'ALL')),
            generated_at TEXT
                CHECK(generated_at IS NULL OR length(generated_at) BETWEEN 20 AND 40),
            last_synced_at TEXT
                CHECK(last_synced_at IS NULL OR length(last_synced_at) BETWEEN 20 AND 40)
        );
        INSERT INTO feed_catalog_state(singleton_id, catalog_version, scope)
        VALUES(1, 0, 'ACTIVE');

        CREATE TABLE feed_categories(
            id TEXT PRIMARY KEY CHECK(length(id) = 36),
            name TEXT NOT NULL CHECK(length(trim(name)) BETWEEN 1 AND 80),
            name_norm TEXT NOT NULL CHECK(length(name_norm) BETWEEN 1 AND 160),
            sort_order INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(sort_order) = 'integer' AND sort_order BETWEEN 0 AND 1000000),
            is_enabled INTEGER NOT NULL DEFAULT 1
                CHECK(typeof(is_enabled) = 'integer' AND is_enabled IN (0, 1)),
            version INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(version) = 'integer' AND version >= 0),
            created_at TEXT NOT NULL CHECK(length(created_at) BETWEEN 20 AND 40),
            updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40)
        );

        CREATE TABLE feed_catalog(
            id TEXT PRIMARY KEY CHECK(length(id) = 36),
            original_url TEXT NOT NULL
                CHECK(length(original_url) BETWEEN 1 AND 2048 AND lower(substr(trim(original_url), 1, 8)) = 'https://'),
            normalized_url TEXT NOT NULL
                CHECK(length(normalized_url) BETWEEN 1 AND 2048 AND substr(normalized_url, 1, 8) = 'https://' AND instr(normalized_url, '#') = 0),
            display_name TEXT NOT NULL CHECK(length(trim(display_name)) BETWEEN 1 AND 160),
            site_url TEXT
                CHECK(site_url IS NULL OR (length(site_url) BETWEEN 1 AND 2048 AND lower(substr(trim(site_url), 1, 8)) = 'https://')),
            category_id TEXT,
            view_kind TEXT NOT NULL DEFAULT 'ARTICLE'
                CHECK(view_kind IN ('ARTICLE', 'PICTURE', 'AUDIO', 'VIDEO', 'NOTIFICATION')),
            refresh_interval_minutes INTEGER NOT NULL DEFAULT 60
                CHECK(typeof(refresh_interval_minutes) = 'integer' AND refresh_interval_minutes BETWEEN 5 AND 1440),
            sort_order INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(sort_order) = 'integer' AND sort_order BETWEEN 0 AND 1000000),
            is_enabled INTEGER NOT NULL DEFAULT 1
                CHECK(typeof(is_enabled) = 'integer' AND is_enabled IN (0, 1)),
            version INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(version) = 'integer' AND version >= 0),
            created_at TEXT NOT NULL CHECK(length(created_at) BETWEEN 20 AND 40),
            updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40),
            FOREIGN KEY(category_id) REFERENCES feed_categories(id) ON UPDATE RESTRICT ON DELETE RESTRICT
        );

        CREATE TABLE feed_fetch_state(
            feed_id TEXT PRIMARY KEY REFERENCES feed_catalog(id) ON DELETE CASCADE,
            etag TEXT CHECK(etag IS NULL OR length(etag) <= 1024),
            last_modified TEXT CHECK(last_modified IS NULL OR length(last_modified) <= 256),
            next_fetch_at TEXT CHECK(next_fetch_at IS NULL OR length(next_fetch_at) BETWEEN 20 AND 40),
            last_success_at TEXT CHECK(last_success_at IS NULL OR length(last_success_at) BETWEEN 20 AND 40),
            last_failure_at TEXT CHECK(last_failure_at IS NULL OR length(last_failure_at) BETWEEN 20 AND 40),
            consecutive_failures INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(consecutive_failures) = 'integer' AND consecutive_failures >= 0),
            error_code TEXT CHECK(error_code IS NULL OR length(error_code) BETWEEN 1 AND 128),
            updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40)
        );

        -- No foreign key to feed_catalog: entries must survive catalog removal until retention policy deletes them.
        CREATE TABLE feed_entries(
            id TEXT PRIMARY KEY CHECK(length(id) BETWEEN 1 AND 128),
            feed_id TEXT NOT NULL CHECK(length(feed_id) = 36),
            external_id TEXT NOT NULL CHECK(length(external_id) BETWEEN 1 AND 2048),
            normalized_url TEXT
                CHECK(normalized_url IS NULL OR (
                    length(normalized_url) BETWEEN 1 AND 2048
                    AND (lower(substr(normalized_url, 1, 7)) = 'http://' OR lower(substr(normalized_url, 1, 8)) = 'https://')
                    AND instr(normalized_url, '#') = 0)),
            title TEXT NOT NULL CHECK(length(title) <= 512),
            author TEXT CHECK(author IS NULL OR length(author) <= 256),
            published_at TEXT CHECK(published_at IS NULL OR length(published_at) BETWEEN 20 AND 40),
            updated_at TEXT CHECK(updated_at IS NULL OR length(updated_at) BETWEEN 20 AND 40),
            summary TEXT NOT NULL DEFAULT '',
            sanitized_content TEXT NOT NULL DEFAULT '',
            enclosure_json TEXT NOT NULL DEFAULT '[]'
                CHECK(json_valid(enclosure_json) AND json_type(enclosure_json) = 'array'),
            content_hash TEXT NOT NULL CHECK(length(content_hash) BETWEEN 1 AND 128),
            fetched_at TEXT NOT NULL CHECK(length(fetched_at) BETWEEN 20 AND 40)
        );

        CREATE UNIQUE INDEX ux_feed_categories_name_norm ON feed_categories(name_norm);
        CREATE INDEX ix_feed_categories_order ON feed_categories(is_enabled, sort_order, id);
        CREATE UNIQUE INDEX ux_feed_catalog_normalized_url ON feed_catalog(normalized_url);
        CREATE INDEX ix_feed_catalog_order ON feed_catalog(category_id, is_enabled, sort_order, id);
        CREATE INDEX ix_feed_catalog_version ON feed_catalog(version);
        CREATE INDEX ix_feed_fetch_state_next_fetch ON feed_fetch_state(next_fetch_at, feed_id);
        CREATE UNIQUE INDEX ux_feed_entries_feed_external_id ON feed_entries(feed_id, external_id);
        CREATE INDEX ix_feed_entries_feed_published ON feed_entries(feed_id, published_at DESC, id);
        CREATE INDEX ix_feed_entries_normalized_url ON feed_entries(normalized_url);
        CREATE INDEX ix_feed_entries_content_hash ON feed_entries(content_hash);
        CREATE INDEX ix_feed_entries_fetched_at ON feed_entries(fetched_at DESC);

        CREATE VIEW feed_entry_search_documents AS
        SELECT
            'feed_entry' AS entity_type,
            id AS entity_id,
            title,
            trim(summary || char(10) || sanitized_content) AS content
        FROM feed_entries;
        """;

    private const string MigrationFiveSql = """
        DELETE FROM content_fts WHERE entity_type='feed_entry';
        INSERT INTO content_fts(entity_type, entity_id, title, content)
        SELECT 'feed_entry', id, title, trim(summary || char(10) || sanitized_content)
        FROM feed_entries;

        CREATE TRIGGER feed_entries_fts_insert
        AFTER INSERT ON feed_entries
        BEGIN
            INSERT INTO content_fts(entity_type, entity_id, title, content)
            VALUES(
                'feed_entry', NEW.id, NEW.title,
                trim(NEW.summary || char(10) || NEW.sanitized_content));
        END;

        CREATE TRIGGER feed_entries_fts_update
        AFTER UPDATE ON feed_entries
        BEGIN
            DELETE FROM content_fts
            WHERE entity_type='feed_entry' AND entity_id=OLD.id;
            INSERT INTO content_fts(entity_type, entity_id, title, content)
            VALUES(
                'feed_entry', NEW.id, NEW.title,
                trim(NEW.summary || char(10) || NEW.sanitized_content));
        END;

        CREATE TRIGGER feed_entries_fts_delete
        AFTER DELETE ON feed_entries
        BEGIN
            DELETE FROM content_fts
            WHERE entity_type='feed_entry' AND entity_id=OLD.id;
        END;
        """;

    private const string MigrationSixSql = """
        CREATE TABLE IF NOT EXISTS user_entry_states(
            entry_id TEXT NOT NULL CHECK(length(entry_id) BETWEEN 1 AND 128),
            local_profile TEXT NOT NULL DEFAULT 'default'
                CHECK(length(local_profile) BETWEEN 1 AND 64),
            is_read INTEGER NOT NULL DEFAULT 0 CHECK(is_read IN (0, 1)),
            is_starred INTEGER NOT NULL DEFAULT 0 CHECK(is_starred IN (0, 1)),
            progress REAL NOT NULL DEFAULT 0 CHECK(progress >= 0 AND progress <= 100),
            note TEXT NOT NULL DEFAULT '' CHECK(length(note) <= 4000),
            updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40),
            PRIMARY KEY(entry_id, local_profile)
        );
        CREATE INDEX IF NOT EXISTS ix_user_entry_states_profile_updated
            ON user_entry_states(local_profile, updated_at DESC, entry_id);
        """;

    private const string MigrationSevenSql = """
        CREATE TABLE IF NOT EXISTS entry_assets(
            entry_id TEXT NOT NULL CHECK(length(entry_id) BETWEEN 1 AND 256),
            source_url TEXT NOT NULL CHECK(length(source_url) BETWEEN 1 AND 4096),
            content_hash TEXT NOT NULL CHECK(length(content_hash) = 64),
            mime_type TEXT NOT NULL CHECK(length(mime_type) BETWEEN 1 AND 128),
            size_bytes INTEGER NOT NULL CHECK(size_bytes >= 0),
            created_at TEXT NOT NULL,
            last_accessed_at TEXT NOT NULL,
            PRIMARY KEY(entry_id, source_url)
        );
        CREATE INDEX IF NOT EXISTS ix_entry_assets_lru
            ON entry_assets(last_accessed_at ASC, content_hash);
        CREATE INDEX IF NOT EXISTS ix_entry_assets_hash
            ON entry_assets(content_hash);
        """;

    private const string MigrationEightSql = """
        ALTER TABLE feed_catalog
            ADD COLUMN full_text_policy TEXT NOT NULL DEFAULT 'NONE'
            CHECK(full_text_policy IN ('NONE', 'ON_OPEN', 'BACKGROUND'));

        ALTER TABLE feed_entries
            ADD COLUMN has_full_content INTEGER NOT NULL DEFAULT 0
            CHECK(has_full_content IN (0, 1));

        CREATE TABLE IF NOT EXISTS feed_full_text_content(
            entry_id TEXT PRIMARY KEY REFERENCES feed_entries(id) ON DELETE CASCADE,
            article_json TEXT NOT NULL CHECK(json_valid(article_json)),
            content_hash TEXT NOT NULL CHECK(length(content_hash) = 64),
            extracted_at TEXT NOT NULL CHECK(length(extracted_at) BETWEEN 20 AND 40)
        );

        CREATE TABLE IF NOT EXISTS feed_full_text_jobs(
            entry_id TEXT PRIMARY KEY REFERENCES feed_entries(id) ON DELETE CASCADE,
            host TEXT NOT NULL CHECK(length(host) BETWEEN 1 AND 253),
            status TEXT NOT NULL
                CHECK(status IN ('PENDING', 'IN_PROGRESS', 'RETRY', 'SUCCEEDED', 'BLOCKED')),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
            next_attempt_at TEXT CHECK(next_attempt_at IS NULL OR length(next_attempt_at) BETWEEN 20 AND 40),
            lease_expires_at TEXT CHECK(lease_expires_at IS NULL OR length(lease_expires_at) BETWEEN 20 AND 40),
            lease_id TEXT CHECK(lease_id IS NULL OR length(lease_id) = 36),
            last_error_code TEXT CHECK(last_error_code IS NULL OR length(last_error_code) BETWEEN 1 AND 128),
            updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40)
        );

        CREATE TABLE IF NOT EXISTS feed_full_text_host_state(
            host TEXT PRIMARY KEY CHECK(length(host) BETWEEN 1 AND 253),
            consecutive_failures INTEGER NOT NULL DEFAULT 0 CHECK(consecutive_failures >= 0),
            next_attempt_at TEXT NOT NULL CHECK(length(next_attempt_at) BETWEEN 20 AND 40),
            last_error_code TEXT NOT NULL CHECK(length(last_error_code) BETWEEN 1 AND 128),
            updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40)
        );

        CREATE INDEX IF NOT EXISTS ix_feed_full_text_jobs_due
            ON feed_full_text_jobs(status, next_attempt_at, lease_expires_at, entry_id);
        CREATE INDEX IF NOT EXISTS ix_feed_full_text_jobs_host
            ON feed_full_text_jobs(host, status, entry_id);
        CREATE INDEX IF NOT EXISTS ix_feed_full_text_host_due
            ON feed_full_text_host_state(next_attempt_at, host);
        """;

    private const string MigrationNineSql = """
        ALTER TABLE ai_reports
            ADD COLUMN content_hash TEXT
            CHECK(content_hash IS NULL OR length(content_hash) = 64);
        ALTER TABLE ai_reports
            ADD COLUMN target_language TEXT
            CHECK(target_language IS NULL OR length(target_language) BETWEEN 1 AND 32);
        ALTER TABLE ai_reports
            ADD COLUMN prompt_version TEXT
            CHECK(prompt_version IS NULL OR length(prompt_version) BETWEEN 1 AND 128);
        ALTER TABLE ai_reports
            ADD COLUMN prompt_tokens INTEGER NOT NULL DEFAULT 0
            CHECK(prompt_tokens >= 0);
        ALTER TABLE ai_reports
            ADD COLUMN completion_tokens INTEGER NOT NULL DEFAULT 0
            CHECK(completion_tokens >= 0);
        ALTER TABLE ai_reports
            ADD COLUMN duration_ms INTEGER NOT NULL DEFAULT 0
            CHECK(duration_ms >= 0);
        ALTER TABLE ai_reports
            ADD COLUMN error_code TEXT
            CHECK(error_code IS NULL OR length(error_code) BETWEEN 1 AND 128);
        ALTER TABLE ai_reports
            ADD COLUMN updated_at TEXT
            CHECK(updated_at IS NULL OR length(updated_at) BETWEEN 20 AND 40);

        CREATE UNIQUE INDEX IF NOT EXISTS ux_ai_reports_feed_cache_key
            ON ai_reports(
                entity_id, content_hash, report_type, target_language, model, prompt_version)
            WHERE entity_type='feed_entry'
              AND entity_id IS NOT NULL
              AND content_hash IS NOT NULL
              AND target_language IS NOT NULL
              AND prompt_version IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_ai_reports_feed_history
            ON ai_reports(
                entity_type, entity_id, report_type, target_language, created_at DESC);
        """;

    private const string MigrationTenSql = """
        ALTER TABLE feed_catalog_state
            ADD COLUMN ai_manual_summary_policy TEXT NOT NULL DEFAULT 'ENABLED'
            CHECK(ai_manual_summary_policy IN ('ENABLED', 'DISABLED'));
        ALTER TABLE feed_catalog_state
            ADD COLUMN ai_auto_summary_policy TEXT NOT NULL DEFAULT 'DISABLED'
            CHECK(ai_auto_summary_policy IN ('ENABLED', 'DISABLED'));
        ALTER TABLE feed_catalog_state
            ADD COLUMN ai_auto_translation_policy TEXT NOT NULL DEFAULT 'DISABLED'
            CHECK(ai_auto_translation_policy IN ('ENABLED', 'DISABLED'));
        ALTER TABLE feed_catalog_state
            ADD COLUMN ai_translation_target_language TEXT NOT NULL DEFAULT 'zh-Hans'
            CHECK(ai_translation_target_language IN ('zh-Hans', 'en', 'ja', 'ko'));
        ALTER TABLE feed_catalog_state
            ADD COLUMN ai_daily_entry_limit INTEGER NOT NULL DEFAULT 20
            CHECK(typeof(ai_daily_entry_limit) = 'integer' AND ai_daily_entry_limit BETWEEN 1 AND 1000);
        ALTER TABLE feed_catalog_state
            ADD COLUMN ai_max_concurrency INTEGER NOT NULL DEFAULT 1
            CHECK(typeof(ai_max_concurrency) = 'integer' AND ai_max_concurrency BETWEEN 1 AND 4);

        ALTER TABLE feed_categories
            ADD COLUMN ai_manual_summary_policy TEXT NOT NULL DEFAULT 'INHERIT'
            CHECK(ai_manual_summary_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));
        ALTER TABLE feed_categories
            ADD COLUMN ai_auto_summary_policy TEXT NOT NULL DEFAULT 'INHERIT'
            CHECK(ai_auto_summary_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));
        ALTER TABLE feed_categories
            ADD COLUMN ai_auto_translation_policy TEXT NOT NULL DEFAULT 'INHERIT'
            CHECK(ai_auto_translation_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));
        ALTER TABLE feed_categories
            ADD COLUMN ai_translation_target_language TEXT
            CHECK(ai_translation_target_language IS NULL OR ai_translation_target_language IN ('zh-Hans', 'en', 'ja', 'ko'));
        ALTER TABLE feed_categories
            ADD COLUMN ai_daily_entry_limit INTEGER
            CHECK(ai_daily_entry_limit IS NULL OR
                (typeof(ai_daily_entry_limit) = 'integer' AND ai_daily_entry_limit BETWEEN 1 AND 1000));
        ALTER TABLE feed_categories
            ADD COLUMN ai_max_concurrency INTEGER
            CHECK(ai_max_concurrency IS NULL OR
                (typeof(ai_max_concurrency) = 'integer' AND ai_max_concurrency BETWEEN 1 AND 4));

        ALTER TABLE feed_catalog
            ADD COLUMN ai_manual_summary_policy TEXT NOT NULL DEFAULT 'INHERIT'
            CHECK(ai_manual_summary_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));
        ALTER TABLE feed_catalog
            ADD COLUMN ai_auto_summary_policy TEXT NOT NULL DEFAULT 'INHERIT'
            CHECK(ai_auto_summary_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));
        ALTER TABLE feed_catalog
            ADD COLUMN ai_auto_translation_policy TEXT NOT NULL DEFAULT 'INHERIT'
            CHECK(ai_auto_translation_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));
        ALTER TABLE feed_catalog
            ADD COLUMN ai_translation_target_language TEXT
            CHECK(ai_translation_target_language IS NULL OR ai_translation_target_language IN ('zh-Hans', 'en', 'ja', 'ko'));
        ALTER TABLE feed_catalog
            ADD COLUMN ai_daily_entry_limit INTEGER
            CHECK(ai_daily_entry_limit IS NULL OR
                (typeof(ai_daily_entry_limit) = 'integer' AND ai_daily_entry_limit BETWEEN 1 AND 1000));
        ALTER TABLE feed_catalog
            ADD COLUMN ai_max_concurrency INTEGER
            CHECK(ai_max_concurrency IS NULL OR
                (typeof(ai_max_concurrency) = 'integer' AND ai_max_concurrency BETWEEN 1 AND 4));

        CREATE INDEX IF NOT EXISTS ix_feed_categories_ai_automation
            ON feed_categories(ai_auto_summary_policy, ai_auto_translation_policy, id);
        CREATE INDEX IF NOT EXISTS ix_feed_catalog_ai_automation
            ON feed_catalog(ai_auto_summary_policy, ai_auto_translation_policy, id);
        """;

    private const string MigrationElevenSql = """
        CREATE TABLE feed_ai_automation_jobs(
            id TEXT PRIMARY KEY CHECK(length(id) = 36),
            feed_id TEXT NOT NULL CHECK(length(feed_id) = 36),
            entry_id TEXT NOT NULL REFERENCES feed_entries(id) ON DELETE CASCADE,
            content_hash TEXT NOT NULL CHECK(length(content_hash) = 64),
            task_type TEXT NOT NULL CHECK(task_type IN ('SUMMARY', 'TRANSLATION')),
            target_language TEXT NOT NULL CHECK(length(target_language) BETWEEN 1 AND 32),
            status TEXT NOT NULL DEFAULT 'PENDING'
                CHECK(status IN ('PENDING', 'RUNNING', 'RETRY', 'SUCCEEDED', 'SKIPPED', 'SUPERSEDED')),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
            next_attempt_at TEXT NOT NULL CHECK(length(next_attempt_at) BETWEEN 20 AND 40),
            lease_token TEXT CHECK(lease_token IS NULL OR length(lease_token) = 32),
            lease_expires_at TEXT
                CHECK(lease_expires_at IS NULL OR length(lease_expires_at) BETWEEN 20 AND 40),
            last_error_code TEXT CHECK(last_error_code IS NULL OR length(last_error_code) BETWEEN 1 AND 128),
            created_at TEXT NOT NULL CHECK(length(created_at) BETWEEN 20 AND 40),
            updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40),
            UNIQUE(feed_id, entry_id, content_hash, task_type, target_language)
        );

        CREATE TABLE feed_ai_automation_daily_entries(
            usage_date TEXT NOT NULL CHECK(length(usage_date) = 10),
            feed_id TEXT NOT NULL CHECK(length(feed_id) = 36),
            entry_id TEXT NOT NULL CHECK(length(entry_id) BETWEEN 1 AND 256),
            reserved_at TEXT NOT NULL CHECK(length(reserved_at) BETWEEN 20 AND 40),
            PRIMARY KEY(usage_date, feed_id, entry_id)
        );

        CREATE INDEX ix_feed_ai_automation_jobs_due
            ON feed_ai_automation_jobs(status, next_attempt_at, lease_expires_at, created_at, id);
        CREATE INDEX ix_feed_ai_automation_jobs_entry
            ON feed_ai_automation_jobs(feed_id, entry_id, task_type, status);
        CREATE INDEX ix_feed_ai_automation_daily_limit
            ON feed_ai_automation_daily_entries(usage_date, feed_id, entry_id);
        """;

    private const string MigrationTwelveSql = """
        CREATE TABLE feed_automation_runs(
            entry_id TEXT NOT NULL CHECK(length(entry_id) BETWEEN 1 AND 256),
            rule_id TEXT NOT NULL CHECK(length(rule_id) = 36),
            rule_version INTEGER NOT NULL
                CHECK(typeof(rule_version) = 'integer' AND rule_version >= 1),
            evaluation_outcome TEXT NOT NULL
                CHECK(evaluation_outcome IN ('DISABLED', 'MATCHED', 'NOT_MATCHED')),
            plan_order INTEGER NOT NULL
                CHECK(typeof(plan_order) = 'integer' AND plan_order BETWEEN 0 AND 99),
            evaluated_at TEXT NOT NULL CHECK(length(evaluated_at) BETWEEN 20 AND 40),
            PRIMARY KEY(entry_id, rule_id, rule_version)
        ) WITHOUT ROWID;

        CREATE TABLE feed_automation_action_runs(
            idempotency_key TEXT PRIMARY KEY CHECK(length(idempotency_key) = 64),
            entry_id TEXT NOT NULL CHECK(length(entry_id) BETWEEN 1 AND 256),
            rule_id TEXT NOT NULL CHECK(length(rule_id) = 36),
            rule_version INTEGER NOT NULL
                CHECK(typeof(rule_version) = 'integer' AND rule_version >= 1),
            rule_priority INTEGER NOT NULL
                CHECK(typeof(rule_priority) = 'integer' AND rule_priority BETWEEN 0 AND 1000),
            rule_conflict_order INTEGER NOT NULL
                CHECK(typeof(rule_conflict_order) = 'integer' AND rule_conflict_order BETWEEN 0 AND 1000),
            action_type TEXT NOT NULL
                CHECK(action_type IN (
                    'ADD_TAG', 'HIDE', 'MARK_READ', 'GENERATE_SUMMARY',
                    'TRANSLATE', 'SEND_TO_MEDIA', 'NOTIFY')),
            action_order INTEGER NOT NULL
                CHECK(typeof(action_order) = 'integer' AND action_order BETWEEN 0 AND 1000),
            action_value TEXT CHECK(action_value IS NULL OR length(action_value) BETWEEN 1 AND 80),
            disposition TEXT NOT NULL CHECK(disposition IN ('PLANNED', 'SUPPRESSED')),
            suppression_reason TEXT NOT NULL
                CHECK(suppression_reason IN ('NONE', 'DUPLICATE_SINGLETON', 'DUPLICATE_TAG')),
            winning_rule_id TEXT CHECK(winning_rule_id IS NULL OR length(winning_rule_id) = 36),
            winning_rule_version INTEGER
                CHECK(winning_rule_version IS NULL OR
                    (typeof(winning_rule_version) = 'integer' AND winning_rule_version >= 1)),
            winning_action_order INTEGER
                CHECK(winning_action_order IS NULL OR
                    (typeof(winning_action_order) = 'integer' AND winning_action_order BETWEEN 0 AND 1000)),
            status TEXT NOT NULL
                CHECK(status IN ('PENDING', 'RUNNING', 'RETRY', 'SUCCEEDED', 'FAILED', 'SUPPRESSED')),
            attempt_count INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(attempt_count) = 'integer' AND attempt_count >= 0),
            next_attempt_at TEXT
                CHECK(next_attempt_at IS NULL OR length(next_attempt_at) BETWEEN 20 AND 40),
            lease_token TEXT CHECK(lease_token IS NULL OR length(lease_token) = 32),
            lease_expires_at TEXT
                CHECK(lease_expires_at IS NULL OR length(lease_expires_at) BETWEEN 20 AND 40),
            last_error_code TEXT
                CHECK(last_error_code IS NULL OR length(last_error_code) BETWEEN 1 AND 128),
            created_at TEXT NOT NULL CHECK(length(created_at) BETWEEN 20 AND 40),
            updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40),
            UNIQUE(entry_id, rule_id, rule_version, action_order),
            FOREIGN KEY(entry_id, rule_id, rule_version)
                REFERENCES feed_automation_runs(entry_id, rule_id, rule_version)
                ON DELETE CASCADE,
            CHECK(
                (disposition = 'PLANNED'
                    AND suppression_reason = 'NONE'
                    AND winning_rule_id IS NULL
                    AND winning_rule_version IS NULL
                    AND winning_action_order IS NULL
                    AND status <> 'SUPPRESSED')
                OR
                (disposition = 'SUPPRESSED'
                    AND suppression_reason <> 'NONE'
                    AND winning_rule_id IS NOT NULL
                    AND winning_rule_version IS NOT NULL
                    AND winning_action_order IS NOT NULL
                    AND status = 'SUPPRESSED')),
            CHECK(
                (status IN ('PENDING', 'RETRY') AND next_attempt_at IS NOT NULL)
                OR
                (status NOT IN ('PENDING', 'RETRY') AND next_attempt_at IS NULL))
        );

        CREATE INDEX ix_feed_automation_action_runs_due
            ON feed_automation_action_runs(
                status, next_attempt_at, lease_expires_at,
                rule_priority DESC, rule_conflict_order, created_at, idempotency_key);
        CREATE INDEX ix_feed_automation_action_runs_entry
            ON feed_automation_action_runs(
                entry_id, created_at, rule_priority DESC,
                rule_conflict_order, rule_id, action_order);
        """;

    private const string MigrationThirteenSql = """
        ALTER TABLE user_entry_states
            ADD COLUMN is_hidden INTEGER NOT NULL DEFAULT 0
            CHECK(is_hidden IN (0, 1));

        CREATE INDEX ix_user_entry_states_profile_hidden
            ON user_entry_states(local_profile, is_hidden, entry_id);
        """;

    private const string MigrationFourteenSql = """
        CREATE TABLE feed_automation_rule_state(
            singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
            rule_set_version INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(rule_set_version) = 'integer'
                    AND rule_set_version >= 0),
            generated_at TEXT
                CHECK(generated_at IS NULL
                    OR length(generated_at) BETWEEN 20 AND 40),
            last_synced_at TEXT
                CHECK(last_synced_at IS NULL
                    OR length(last_synced_at) BETWEEN 20 AND 40)
        );

        INSERT INTO feed_automation_rule_state(
            singleton_id, rule_set_version, generated_at, last_synced_at)
        VALUES(1, 0, NULL, NULL);

        CREATE TABLE feed_automation_rules(
            id TEXT PRIMARY KEY CHECK(length(id) = 36),
            version INTEGER NOT NULL
                CHECK(typeof(version) = 'integer' AND version >= 1),
            priority INTEGER NOT NULL
                CHECK(typeof(priority) = 'integer'
                    AND priority BETWEEN 0 AND 1000),
            conflict_order INTEGER NOT NULL
                CHECK(typeof(conflict_order) = 'integer'
                    AND conflict_order BETWEEN 0 AND 1000),
            rule_json TEXT NOT NULL
                CHECK(length(rule_json) BETWEEN 2 AND 65536)
        );

        CREATE INDEX ix_feed_automation_rules_order
            ON feed_automation_rules(
                priority DESC, conflict_order, id);
        """;

    private const string MigrationFifteenSql = """
        CREATE TABLE feed_media_deliveries(
            entry_id TEXT NOT NULL
                CHECK(length(entry_id) BETWEEN 1 AND 512),
            feed_id TEXT NOT NULL
                CHECK(length(feed_id) BETWEEN 1 AND 512),
            entry_title TEXT NOT NULL
                CHECK(length(entry_title) BETWEEN 1 AND 1024),
            source_url TEXT NOT NULL
                CHECK(length(source_url) BETWEEN 1 AND 4096),
            source_title TEXT
                CHECK(source_title IS NULL
                    OR length(source_title) <= 1024),
            media_type TEXT NOT NULL
                CHECK(length(media_type) BETWEEN 1 AND 255),
            source_length INTEGER
                CHECK(source_length IS NULL
                    OR (typeof(source_length) = 'integer'
                        AND source_length >= 0)),
            media_job_id TEXT NOT NULL
                REFERENCES media_jobs(id) ON DELETE CASCADE,
            created_at TEXT NOT NULL
                CHECK(length(created_at) BETWEEN 20 AND 40),
            PRIMARY KEY(entry_id, source_url)
        );

        CREATE UNIQUE INDEX ux_feed_media_deliveries_job
            ON feed_media_deliveries(media_job_id);
        """;

    private const string MigrationSixteenSql = """
        CREATE TABLE app_notifications(
            id TEXT PRIMARY KEY CHECK(length(id) = 64),
            entry_id TEXT NOT NULL
                CHECK(length(entry_id) BETWEEN 1 AND 512),
            feed_id TEXT NOT NULL
                CHECK(length(feed_id) BETWEEN 1 AND 512),
            rule_id TEXT NOT NULL CHECK(length(rule_id) = 36),
            rule_version INTEGER NOT NULL
                CHECK(typeof(rule_version) = 'integer'
                    AND rule_version >= 1),
            title TEXT NOT NULL
                CHECK(length(title) BETWEEN 1 AND 1024),
            source_label TEXT NOT NULL
                CHECK(length(source_label) BETWEEN 1 AND 160),
            created_at TEXT NOT NULL
                CHECK(length(created_at) BETWEEN 20 AND 40),
            read_at TEXT
                CHECK(read_at IS NULL
                    OR length(read_at) BETWEEN 20 AND 40)
        ) WITHOUT ROWID;

        CREATE INDEX ix_app_notifications_unread
            ON app_notifications(read_at, created_at DESC, id);
        """;

    private const string MigrationNineteenSql = """
        ALTER TABLE app_notifications
            ADD COLUMN kind TEXT NOT NULL DEFAULT 'CONTENT_MATCH'
            CHECK(kind IN (
                'CONTENT_MATCH', 'SYSTEM_HEALTH', 'TASK_COMPLETED'));

        CREATE INDEX ix_app_notifications_kind_created
            ON app_notifications(kind, created_at DESC, id);
        """;

    private const string MigrationTwentySql = """
        CREATE TABLE feed_smart_view_state(
            singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
            view_set_version INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(view_set_version) = 'integer'
                    AND view_set_version >= 0),
            generated_at TEXT
                CHECK(generated_at IS NULL
                    OR length(generated_at) BETWEEN 20 AND 40),
            last_synced_at TEXT
                CHECK(last_synced_at IS NULL
                    OR length(last_synced_at) BETWEEN 20 AND 40)
        );

        INSERT INTO feed_smart_view_state(
            singleton_id, view_set_version, generated_at, last_synced_at)
        VALUES(1, 0, NULL, NULL);

        CREATE TABLE feed_smart_views(
            id TEXT PRIMARY KEY CHECK(length(id) = 36),
            version INTEGER NOT NULL
                CHECK(typeof(version) = 'integer' AND version >= 1),
            name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
            sort_order INTEGER NOT NULL
                CHECK(typeof(sort_order) = 'integer'
                    AND sort_order BETWEEN 0 AND 1000),
            is_enabled INTEGER NOT NULL CHECK(is_enabled = 1),
            feed_id TEXT CHECK(feed_id IS NULL OR length(feed_id) = 36),
            category_id TEXT
                CHECK(category_id IS NULL OR length(category_id) = 36),
            view_kind TEXT CHECK(view_kind IS NULL OR view_kind IN (
                'ARTICLE', 'PICTURE', 'AUDIO', 'VIDEO', 'NOTIFICATION')),
            read_filter TEXT NOT NULL
                CHECK(read_filter IN ('ALL', 'UNREAD', 'READ')),
            favorites_only INTEGER NOT NULL
                CHECK(favorites_only IN (0, 1)),
            search_text TEXT
                CHECK(search_text IS NULL
                    OR length(search_text) BETWEEN 1 AND 200),
            published_within_days INTEGER
                CHECK(published_within_days IS NULL OR (
                    typeof(published_within_days) = 'integer'
                    AND published_within_days BETWEEN 1 AND 365))
        ) WITHOUT ROWID;

        CREATE INDEX ix_feed_smart_views_order
            ON feed_smart_views(sort_order, name, id);
        """;

    private const string MigrationTwentyOneSql = """
        -- 导出历史不引用 feed_entries，避免源条目按保留策略删除时连带丢失审计状态。
        -- 表中只保存重建请求所需的非秘密元数据，不保存凭据、正文或供应商响应。
        CREATE TABLE entry_export_tasks(
            idempotency_key TEXT PRIMARY KEY
                CHECK(length(idempotency_key) = 64
                    AND idempotency_key = lower(idempotency_key)
                    AND idempotency_key NOT GLOB '*[^0-9a-f]*'),
            exporter_id TEXT NOT NULL
                CHECK(length(exporter_id) BETWEEN 1 AND 64),
            target_id TEXT NOT NULL
                CHECK(length(target_id) BETWEEN 1 AND 256),
            entry_id TEXT NOT NULL
                CHECK(length(entry_id) BETWEEN 1 AND 128),
            content_hash TEXT NOT NULL
                CHECK(length(content_hash) BETWEEN 1 AND 128),
            view_kind TEXT NOT NULL CHECK(view_kind IN (
                'Article', 'Picture', 'Audio', 'Video', 'Notification')),
            content_bytes INTEGER NOT NULL
                CHECK(typeof(content_bytes) = 'integer'
                    AND content_bytes >= 0),
            status TEXT NOT NULL CHECK(status IN (
                'QUEUED', 'RUNNING', 'COMPLETED', 'FAILED', 'CANCELLED')),
            attempt_count INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(attempt_count) = 'integer'
                    AND attempt_count BETWEEN 0 AND 100),
            next_attempt_at TEXT
                CHECK(next_attempt_at IS NULL
                    OR length(next_attempt_at) BETWEEN 20 AND 40),
            lease_token TEXT
                CHECK(lease_token IS NULL OR length(lease_token) = 36),
            lease_expires_at TEXT
                CHECK(lease_expires_at IS NULL
                    OR length(lease_expires_at) BETWEEN 20 AND 40),
            cancellation_requested INTEGER NOT NULL DEFAULT 0
                CHECK(cancellation_requested IN (0, 1)),
            last_error_code TEXT CHECK(last_error_code IS NULL OR
                last_error_code IN (
                    'InvalidRequest', 'ExporterNotFound',
                    'UnsupportedContent', 'CredentialsRequired',
                    'ContentTooLarge', 'RateLimited',
                    'DestinationUnavailable', 'AccessDenied',
                    'Conflict', 'ProviderRejected', 'Unknown',
                    'EntryMissing', 'EntryChanged')),
            created_at TEXT NOT NULL
                CHECK(length(created_at) BETWEEN 20 AND 40),
            updated_at TEXT NOT NULL
                CHECK(length(updated_at) BETWEEN 20 AND 40),
            completed_at TEXT
                CHECK(completed_at IS NULL
                    OR length(completed_at) BETWEEN 20 AND 40),
            CHECK(
                (status = 'QUEUED'
                    AND next_attempt_at IS NOT NULL
                    AND lease_token IS NULL
                    AND lease_expires_at IS NULL
                    AND cancellation_requested = 0
                    AND completed_at IS NULL)
                OR
                (status = 'RUNNING'
                    AND attempt_count >= 1
                    AND next_attempt_at IS NULL
                    AND lease_token IS NOT NULL
                    AND lease_expires_at IS NOT NULL
                    AND completed_at IS NULL)
                OR
                (status = 'COMPLETED'
                    AND attempt_count >= 1
                    AND next_attempt_at IS NULL
                    AND lease_token IS NULL
                    AND lease_expires_at IS NULL
                    AND cancellation_requested = 0
                    AND last_error_code IS NULL
                    AND completed_at IS NOT NULL)
                OR
                (status = 'FAILED'
                    AND attempt_count >= 1
                    AND next_attempt_at IS NULL
                    AND lease_token IS NULL
                    AND lease_expires_at IS NULL
                    AND cancellation_requested = 0
                    AND last_error_code IS NOT NULL
                    AND completed_at IS NOT NULL)
                OR
                (status = 'CANCELLED'
                    AND next_attempt_at IS NULL
                    AND lease_token IS NULL
                    AND lease_expires_at IS NULL
                    AND cancellation_requested = 0
                    AND last_error_code IS NULL
                    AND completed_at IS NOT NULL))
        ) WITHOUT ROWID;

        CREATE INDEX ix_entry_export_tasks_due
            ON entry_export_tasks(
                status, next_attempt_at, created_at, idempotency_key);
        CREATE INDEX ix_entry_export_tasks_history
            ON entry_export_tasks(
                updated_at DESC, idempotency_key);
        """;

    private const string MigrationTwentyTwoSql = """
        -- P2-20 首个持久化片只保存调度定义和下一次 UTC 游标；
        -- 运行窗口、租约和执行结果将在后续迁移中独立建立。
        CREATE TABLE local_scheduled_tasks(
            id TEXT PRIMARY KEY
                CHECK(length(id) = 36 AND id = lower(id)),
            frequency TEXT NOT NULL
                CHECK(frequency IN ('ONCE', 'DAILY', 'WEEKLY', 'MONTHLY')),
            time_zone_id TEXT NOT NULL
                CHECK(length(time_zone_id) BETWEEN 1 AND 128
                    AND time_zone_id = trim(time_zone_id)),
            local_time TEXT NOT NULL
                CHECK(length(local_time) = 16),
            once_date TEXT
                CHECK(once_date IS NULL OR length(once_date) = 10),
            weekly_day INTEGER
                CHECK(weekly_day IS NULL OR (
                    typeof(weekly_day) = 'integer'
                    AND weekly_day BETWEEN 0 AND 6)),
            monthly_day INTEGER
                CHECK(monthly_day IS NULL OR (
                    typeof(monthly_day) = 'integer'
                    AND monthly_day BETWEEN 1 AND 31)),
            missed_run_policy TEXT NOT NULL
                CHECK(missed_run_policy IN ('RUN_ONCE', 'SKIP')),
            is_enabled INTEGER NOT NULL
                CHECK(is_enabled IN (0, 1)),
            next_run_at TEXT
                CHECK(next_run_at IS NULL OR (
                    length(next_run_at) = 33
                    AND substr(next_run_at, -6) = '+00:00')),
            created_at TEXT NOT NULL
                CHECK(length(created_at) = 33
                    AND substr(created_at, -6) = '+00:00'),
            updated_at TEXT NOT NULL
                CHECK(length(updated_at) = 33
                    AND substr(updated_at, -6) = '+00:00'),
            CHECK(
                (frequency = 'ONCE'
                    AND once_date IS NOT NULL
                    AND weekly_day IS NULL
                    AND monthly_day IS NULL)
                OR
                (frequency = 'DAILY'
                    AND once_date IS NULL
                    AND weekly_day IS NULL
                    AND monthly_day IS NULL)
                OR
                (frequency = 'WEEKLY'
                    AND once_date IS NULL
                    AND weekly_day IS NOT NULL
                    AND monthly_day IS NULL)
                OR
                (frequency = 'MONTHLY'
                    AND once_date IS NULL
                    AND weekly_day IS NULL
                    AND monthly_day IS NOT NULL)),
            CHECK(
                (is_enabled = 1 AND next_run_at IS NOT NULL)
                OR
                (is_enabled = 0 AND next_run_at IS NULL)),
            CHECK(created_at <= updated_at)
        ) WITHOUT ROWID;

        CREATE INDEX ix_local_scheduled_tasks_due
            ON local_scheduled_tasks(next_run_at, id)
            WHERE is_enabled = 1;
        """;

    private const string MigrationTwentyThreeSql = """
        -- 计划定义只表达未来游标；每个实际执行窗口单独持久化，
        -- 以 (schedule_id, scheduled_for) 唯一标识避免重复领取。
        CREATE TABLE local_schedule_runs(
            schedule_id TEXT NOT NULL
                CHECK(length(schedule_id) = 36
                    AND schedule_id = lower(schedule_id)),
            scheduled_for TEXT NOT NULL
                CHECK(length(scheduled_for) = 33
                    AND substr(scheduled_for, -6) = '+00:00'),
            status TEXT NOT NULL
                CHECK(status IN (
                    'PENDING', 'RUNNING', 'COMPLETED', 'CANCELLED')),
            attempt_count INTEGER NOT NULL
                CHECK(typeof(attempt_count) = 'integer'
                    AND attempt_count >= 1),
            lease_token TEXT
                CHECK(lease_token IS NULL OR (
                    length(lease_token) = 32
                    AND lease_token = lower(lease_token))),
            lease_expires_at TEXT
                CHECK(lease_expires_at IS NULL OR (
                    length(lease_expires_at) = 33
                    AND substr(lease_expires_at, -6) = '+00:00')),
            created_at TEXT NOT NULL
                CHECK(length(created_at) = 33
                    AND substr(created_at, -6) = '+00:00'),
            updated_at TEXT NOT NULL
                CHECK(length(updated_at) = 33
                    AND substr(updated_at, -6) = '+00:00'),
            completed_at TEXT
                CHECK(completed_at IS NULL OR (
                    length(completed_at) = 33
                    AND substr(completed_at, -6) = '+00:00')),
            PRIMARY KEY(schedule_id, scheduled_for),
            CHECK(created_at <= updated_at),
            CHECK(completed_at IS NULL OR completed_at >= created_at),
            CHECK(
                (status = 'PENDING'
                    AND lease_token IS NULL
                    AND lease_expires_at IS NULL
                    AND completed_at IS NULL)
                OR
                (status = 'RUNNING'
                    AND lease_token IS NOT NULL
                    AND lease_expires_at IS NOT NULL
                    AND lease_expires_at > updated_at
                    AND completed_at IS NULL)
                OR
                (status IN ('COMPLETED', 'CANCELLED')
                    AND lease_token IS NULL
                    AND lease_expires_at IS NULL
                    AND completed_at IS NOT NULL
                    AND completed_at = updated_at))
        ) WITHOUT ROWID;

        CREATE INDEX ix_local_schedule_runs_claim
            ON local_schedule_runs(
                status, lease_expires_at, updated_at,
                scheduled_for, schedule_id)
            WHERE status IN ('PENDING', 'RUNNING');

        CREATE INDEX ix_local_schedule_runs_history
            ON local_schedule_runs(schedule_id, scheduled_for DESC);
        """;

    private const string MigrationTwentyFourSql = """
        -- 摘要范围与计划定义保持逻辑分层，但由同一个仓储事务提交，避免多进程
        -- 交错后出现“时间属于 A、范围属于 B”。通用负载有严格大小上限。
        CREATE TABLE local_scheduled_task_payloads(
            schedule_id TEXT PRIMARY KEY
                REFERENCES local_scheduled_tasks(id) ON DELETE CASCADE,
            payload TEXT NOT NULL CHECK(length(payload) <= 4096)
        ) WITHOUT ROWID;

        -- PENDING 的重试可见时间与实际 updated_at 分离；计划代际更新仍使用
        -- 真实状态变更时间，不会被未来 Retry-After 时间干扰。
        CREATE TABLE local_schedule_run_retries(
            schedule_id TEXT NOT NULL,
            scheduled_for TEXT NOT NULL,
            retry_not_before TEXT NOT NULL
                CHECK(length(retry_not_before) = 33
                    AND substr(retry_not_before, -6) = '+00:00'),
            PRIMARY KEY(schedule_id, scheduled_for),
            FOREIGN KEY(schedule_id, scheduled_for)
                REFERENCES local_schedule_runs(schedule_id, scheduled_for)
                ON DELETE CASCADE
        ) WITHOUT ROWID;

        -- DeepSeek 没有本地可依赖的幂等键。调用前先写 STARTED；任何崩溃或
        -- 网络结果未知都保留耐久证据并阻止自动再次计费。
        CREATE TABLE feed_digest_requests(
            schedule_id TEXT NOT NULL
                CHECK(length(schedule_id) = 36
                    AND schedule_id = lower(schedule_id)),
            scheduled_for TEXT NOT NULL
                CHECK(length(scheduled_for) = 33
                    AND substr(scheduled_for, -6) = '+00:00'),
            report_id TEXT NOT NULL
                CHECK(length(report_id) = 76
                    AND report_id LIKE 'feed-digest-%'),
            attempt_count INTEGER NOT NULL
                CHECK(typeof(attempt_count) = 'integer'
                    AND attempt_count >= 1),
            status TEXT NOT NULL
                CHECK(status IN (
                    'STARTED', 'COMPLETED', 'AMBIGUOUS', 'DISCARDED')),
            created_at TEXT NOT NULL
                CHECK(length(created_at) = 33
                    AND substr(created_at, -6) = '+00:00'),
            updated_at TEXT NOT NULL
                CHECK(length(updated_at) = 33
                    AND substr(updated_at, -6) = '+00:00'),
            completed_at TEXT
                CHECK(completed_at IS NULL OR (
                    length(completed_at) = 33
                    AND substr(completed_at, -6) = '+00:00')),
            PRIMARY KEY(schedule_id, scheduled_for),
            UNIQUE(report_id),
            CHECK(created_at <= updated_at),
            CHECK(
                (status = 'STARTED' AND completed_at IS NULL)
                OR
                (status IN ('COMPLETED', 'AMBIGUOUS', 'DISCARDED')
                    AND completed_at = updated_at))
        ) WITHOUT ROWID;
        """;

}
