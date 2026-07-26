using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedMediaDeliveryRepositoryTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateOrGetQueuedAsyncPersistsTraceableQueuedMediaJob()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedMediaDeliveryRepository(database);
        FeedMediaDelivery delivery = CreateDelivery("entry-1", "job-1");
        MediaJob job = CreateQueuedJob("job-1");

        FeedMediaDeliveryRegistration result = await repository.CreateOrGetQueuedAsync(
            delivery,
            job,
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(delivery, result.Delivery);
        Assert.Equal(job, result.Job);
        Assert.Equal(
            job,
            Assert.Single(await new MediaJobRepository(database).GetQueuedAsync(
                CancellationToken.None)));
    }

    [Fact]
    public async Task CreateOrGetQueuedAsyncReturnsExistingRegistrationForSameEntryAndEnclosure()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedMediaDeliveryRepository(database);
        FeedMediaDelivery original = CreateDelivery("entry-duplicate", "job-original");
        MediaJob originalJob = CreateQueuedJob("job-original");
        await repository.CreateOrGetQueuedAsync(
            original,
            originalJob,
            CancellationToken.None);

        FeedMediaDeliveryRegistration duplicate = await repository.CreateOrGetQueuedAsync(
            original with
            {
                MediaJobId = "job-duplicate",
                EntryTitle = "不应覆盖原始来源",
                CreatedAt = original.CreatedAt.AddMinutes(1)
            },
            CreateQueuedJob("job-duplicate"),
            CancellationToken.None);

        Assert.False(duplicate.Created);
        Assert.Equal(original, duplicate.Delivery);
        Assert.Equal(originalJob, duplicate.Job);
        Assert.Single(await new MediaJobRepository(database).GetQueuedAsync(
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateOrGetQueuedAsyncSerializesConcurrentDuplicateRegistrations()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedMediaDeliveryRepository(database);
        FeedMediaDelivery delivery = CreateDelivery("entry-concurrent", "job-concurrent");
        MediaJob job = CreateQueuedJob("job-concurrent");

        FeedMediaDeliveryRegistration[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => repository.CreateOrGetQueuedAsync(
                delivery,
                job,
                CancellationToken.None)));

        Assert.Single(results, result => result.Created);
        Assert.All(results, result =>
        {
            Assert.Equal(delivery, result.Delivery);
            Assert.Equal(job, result.Job);
        });
        Assert.Single(await new MediaJobRepository(database).GetQueuedAsync(
            CancellationToken.None));
    }

    [Fact]
    public async Task GetAsyncRestoresDeliveryAndJobAfterDatabaseReopen()
    {
        FeedMediaDelivery expectedDelivery = CreateDelivery("entry-reopen", "job-reopen");
        MediaJob expectedJob = CreateQueuedJob("job-reopen");
        using (SqliteDatabase database = CreateDatabase())
        {
            await database.InitializeAsync(CancellationToken.None);
            await new FeedMediaDeliveryRepository(database).CreateOrGetQueuedAsync(
                expectedDelivery,
                expectedJob,
                CancellationToken.None);
        }

        using SqliteDatabase reopened = CreateDatabase();
        await reopened.InitializeAsync(CancellationToken.None);
        FeedMediaDeliveryRegistration? restored =
            await new FeedMediaDeliveryRepository(reopened).GetAsync(
                expectedDelivery.EntryId,
                expectedDelivery.SourceUrl,
                CancellationToken.None);

        Assert.NotNull(restored);
        Assert.False(restored.Created);
        Assert.Equal(expectedDelivery, restored.Delivery);
        Assert.Equal(expectedJob, restored.Job);
    }

    [Fact]
    public async Task CreateOrGetQueuedAsyncRollsBackMediaJobWhenDeliveryInsertFails()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await using (SqliteConnection connection =
                     await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_feed_media_delivery
                BEFORE INSERT ON feed_media_deliveries
                BEGIN
                    SELECT RAISE(ABORT, 'forced rollback');
                END;
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var repository = new FeedMediaDeliveryRepository(database);
        await Assert.ThrowsAsync<SqliteException>(() => repository.CreateOrGetQueuedAsync(
            CreateDelivery("entry-rollback", "job-rollback"),
            CreateQueuedJob("job-rollback"),
            CancellationToken.None));

        Assert.Empty(await new MediaJobRepository(database).GetRecentAsync(
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateOrGetQueuedAsyncRejectsNonQueuedOrMismatchedJob()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedMediaDeliveryRepository(database);
        FeedMediaDelivery delivery = CreateDelivery("entry-invalid", "job-expected");

        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateOrGetQueuedAsync(
            delivery,
            CreateQueuedJob("job-other"),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateOrGetQueuedAsync(
            delivery,
            CreateQueuedJob("job-expected") with { Status = MediaJobStatus.Running },
            CancellationToken.None));

        Assert.Empty(await new MediaJobRepository(database).GetRecentAsync(
            10,
            CancellationToken.None));
    }

    private SqliteDatabase CreateDatabase() => new(
        new AppPaths(_testRoot),
        NullLogger<SqliteDatabase>.Instance);

    private static FeedMediaDelivery CreateDelivery(string entryId, string mediaJobId) => new(
        entryId,
        "feed-tech",
        "AI 新闻播客",
        "https://media.example.com/episodes/daily.mp3",
        "每日音频",
        "audio/mpeg",
        1_024,
        mediaJobId,
        new DateTimeOffset(2026, 7, 26, 9, 30, 0, TimeSpan.Zero));

    private static MediaJob CreateQueuedJob(string id)
    {
        DateTimeOffset now = new(2026, 7, 26, 9, 30, 0, TimeSpan.Zero);
        return new(
            id,
            "FeedTranscription",
            $@"C:\Lenx\FeedMedia\{id}.mp3",
            null,
            MediaJobStatus.Queued,
            0,
            TranscriptionEngine.Groq,
            "whisper-large-v3",
            0,
            0,
            null,
            now,
            now);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
