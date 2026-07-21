using LenxTool.Core.Errors;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace LenxTool.Infrastructure.Data;

public sealed partial class SqliteDatabase(
    AppPaths paths,
    ILogger<SqliteDatabase> logger) : IDisposable
{
    private const int CurrentSchemaVersion = 2;
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
}
