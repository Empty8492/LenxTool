using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class SubtitleRepositoryTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReplaceAsyncPersistsAllFieldsAcrossDatabaseReopen()
    {
        SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await CreateMediaJobAsync(database, "job-roundtrip");
        var repository = new MediaJobRepository(database);
        SubtitleSegment[] expected =
        [
            new(
                TimeSpan.FromMilliseconds(1_250),
                TimeSpan.FromMilliseconds(3_500),
                "Hello world",
                "你好，世界",
                -0.27,
                0.03)
            {
                Sequence = 0
            },
            new(
                TimeSpan.FromMilliseconds(4_000),
                TimeSpan.FromMilliseconds(5_750),
                "Second line",
                null,
                -0.18,
                0.01)
            {
                Sequence = 9
            }
        ];

        await repository.ReplaceAsync("job-roundtrip", expected, CancellationToken.None);
        database.Dispose();

        using SqliteDatabase reopened = CreateDatabase();
        await reopened.InitializeAsync(CancellationToken.None);
        var reopenedRepository = new MediaJobRepository(reopened);

        Assert.Equal(
            expected,
            await reopenedRepository.GetByMediaJobIdAsync("job-roundtrip", CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceAsyncRemovesPreviousBatchAndReadsByPersistedSequence()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await CreateMediaJobAsync(database, "job-replace");
        var repository = new MediaJobRepository(database);
        await repository.ReplaceAsync(
            "job-replace",
            [Segment(1, 1, 2, "old-1"), Segment(2, 2, 3, "old-2")],
            CancellationToken.None);
        SubtitleSegment[] replacement =
        [
            Segment(20, 8, 9, "new-2") with { TranslatedText = "新二" },
            Segment(10, 5, 6, "new-1") with { TranslatedText = "新一" }
        ];

        await repository.ReplaceAsync("job-replace", replacement, CancellationToken.None);

        IReadOnlyList<SubtitleSegment> stored = await repository.GetByMediaJobIdAsync(
            "job-replace",
            CancellationToken.None);
        Assert.Equal([replacement[1], replacement[0]], stored);
        Assert.DoesNotContain(stored, segment => segment.Text.StartsWith("old", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplaceAsyncRejectsDuplicateSequenceOrTimelineWithoutChangingPreviousBatch(
        bool duplicateSequence)
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await CreateMediaJobAsync(database, "job-unique");
        var repository = new MediaJobRepository(database);
        SubtitleSegment[] original = [Segment(1, 1, 2, "original")];
        await repository.ReplaceAsync("job-unique", original, CancellationToken.None);
        SubtitleSegment second = duplicateSequence
            ? Segment(2, 12, 13, "duplicate-sequence") with { Sequence = 1 }
            : Segment(2, 10, 11, "duplicate-timeline");

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReplaceAsync(
            "job-unique",
            [Segment(1, 10, 11, "first"), second],
            CancellationToken.None));

        Assert.Equal(
            original,
            await repository.GetByMediaJobIdAsync("job-unique", CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceAsyncRollsBackDeleteAndPartialInsertWhenBatchFails()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await CreateMediaJobAsync(database, "job-rollback");
        var repository = new MediaJobRepository(database);
        SubtitleSegment[] original =
        [
            Segment(1, 1, 2, "original-1"),
            Segment(2, 2, 3, "original-2")
        ];
        await repository.ReplaceAsync("job-rollback", original, CancellationToken.None);
        await using (SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_subtitle_insert
                BEFORE INSERT ON subtitle_segments
                WHEN NEW.text = 'force-rollback'
                BEGIN
                    SELECT RAISE(ABORT, 'forced rollback');
                END;
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await Assert.ThrowsAsync<SqliteException>(() => repository.ReplaceAsync(
            "job-rollback",
            [
                Segment(10, 10, 11, "inserted-before-failure"),
                Segment(11, 11, 12, "force-rollback")
            ],
            CancellationToken.None));

        Assert.Equal(
            original,
            await repository.GetByMediaJobIdAsync("job-rollback", CancellationToken.None));
    }

    [Fact]
    public async Task VersionOneUpgradePreservesExistingSubtitleSegments()
    {
        AppPaths paths = CreatePaths();
        paths.EnsureCreated();
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_versions(
                    version INTEGER PRIMARY KEY,
                    applied_at TEXT NOT NULL,
                    checksum TEXT NOT NULL
                );
                INSERT INTO schema_versions(version, applied_at, checksum)
                VALUES (1, '2026-07-19T00:00:00Z', 'lenx-schema-v1');
                CREATE TABLE news_articles(
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
                CREATE TABLE subtitle_segments(
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
                INSERT INTO media_jobs(
                    id, kind, input_path, status, progress, engine,
                    shared_usage_seconds, ai_request_count, created_at, updated_at)
                VALUES (
                    'job-upgrade', 'Transcription', 'D:\\媒体\\旧任务.wav', 'Completed', 100,
                    'Groq', 0, 1, '2026-07-19T00:00:00Z', '2026-07-19T00:01:00Z');
                INSERT INTO subtitle_segments(
                    media_job_id, sequence, start_ms, end_ms, text, translated_text,
                    avg_log_probability, no_speech_probability)
                VALUES ('job-upgrade', 1, 1000, 2500, '升级前原文', '升级前译文', -0.2, 0.01);
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        using SqliteDatabase upgraded = CreateDatabase();
        await upgraded.InitializeAsync(CancellationToken.None);
        var repository = new MediaJobRepository(upgraded);

        SubtitleSegment stored = Assert.Single(await repository.GetByMediaJobIdAsync(
            "job-upgrade",
            CancellationToken.None));
        Assert.Equal("升级前原文", stored.Text);
        Assert.Equal("升级前译文", stored.TranslatedText);
        Assert.Equal(1, stored.Sequence);
        await using SqliteConnection upgradedConnection = await upgraded.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand version = upgradedConnection.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_versions;";
        Assert.Equal(2L, (long)(await version.ExecuteScalarAsync(CancellationToken.None))!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private SqliteDatabase CreateDatabase() =>
        new(CreatePaths(), NullLogger<SqliteDatabase>.Instance);

    private AppPaths CreatePaths() => new(_testRoot);

    private static SubtitleSegment Segment(
        int sequence,
        double startSeconds,
        double endSeconds,
        string text) =>
        new(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds), text)
        {
            Sequence = sequence
        };

    private static async Task CreateMediaJobAsync(SqliteDatabase database, string id)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var repository = new MediaJobRepository(database);
        await repository.UpsertAsync(
            new(
                id,
                "Transcription",
                $"D:\\媒体\\{id}.wav",
                null,
                MediaJobStatus.Completed,
                100,
                TranscriptionEngine.Groq,
                "whisper-large-v3",
                0,
                1,
                null,
                now,
                now),
            CancellationToken.None);
    }
}
