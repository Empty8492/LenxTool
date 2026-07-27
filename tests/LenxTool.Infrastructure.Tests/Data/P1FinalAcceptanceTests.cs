using System.Diagnostics;
using System.Globalization;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class P1FinalAcceptanceTests : IDisposable
{
    private const int EntryCount = 10_000;
    private const int FavoriteCount = 1_000;
    private const int ProtectedActiveTaskCount = 4;
    private const string CategoryId =
        "10000000-0000-4000-8000-000000000001";
    private const string FeedId =
        "30000000-0000-4000-8000-000000000001";
    private const string FullTextEntryId = "entry-01001";
    private const string AiEntryId = "entry-01002";
    private const string RuleEntryId = "entry-01003";
    private const string MixedMediaEntryId = "entry-01004";
    private const string RuleId =
        "70000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OfflineReadBudget =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CleanupPreviewBudget =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupExecutionBudget =
        TimeSpan.FromSeconds(60);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools P1 final acceptance tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Category", "P1FinalAcceptance")]
    public async Task LargeOfflineLibraryRemainsQueryableAndCleanupProtectsPrivateAndActiveWork()
    {
        FeedFullTextWorkItem fullTextWork;
        using (SqliteDatabase database = await CreateDatabaseAsync())
        {
            await SeedLargeLibraryAsync(database);
            var entries = new FeedEntryRepository(database);

            using var fullText = new FeedFullTextRepository(database);
            fullTextWork = Assert.IsType<FeedFullTextWorkItem>(
                await fullText.ClaimOnOpenAsync(
                    FullTextEntryId,
                    Now,
                    TimeSpan.FromMinutes(5),
                    CancellationToken.None));

            FeedEntry aiEntry = Assert.IsType<FeedEntry>(
                await entries.GetByIdAsync(AiEntryId, CancellationToken.None));
            Assert.Equal(1, await new FeedAiAutomationJobRepository(database)
                .EnqueueAsync(
                    FeedId,
                    [aiEntry],
                    new(true, true, false, "zh-Hans", 20, 1),
                    Now,
                    CancellationToken.None));

            FeedAutomationStageResult staged =
                await new FeedAutomationRunRepository(database).StageAsync(
                    RulePlan(),
                    Now,
                    CancellationToken.None);
            Assert.Equal(new(1, 1), staged);

            FeedEntry mediaEntry = Assert.IsType<FeedEntry>(
                await entries.GetByIdAsync(
                    MixedMediaEntryId,
                    CancellationToken.None));
            Assert.Equal(3, mediaEntry.Enclosures.Count);
            FeedMediaDeliveryRegistration mediaRegistration =
                await new FeedMediaDeliveryRepository(database)
                    .CreateOrGetQueuedAsync(
                        MediaDelivery(mediaEntry),
                        QueuedMediaJob(),
                        CancellationToken.None);
            Assert.True(mediaRegistration.Created);
        }

        using SqliteDatabase reopened = await OpenDatabaseAsync();
        var reopenedEntries = new FeedEntryRepository(reopened);
        var favorites = new FavoriteRepository(reopened);
        var search = new NewsRepository(reopened);

        Stopwatch readTimer = Stopwatch.StartNew();
        int storedFavorites = await favorites.GetCountAsync(
            CancellationToken.None);
        FeedEntryPage favoritePage = await reopenedEntries.QueryAsync(
            Query(favoritesOnly: true, limit: 200),
            CancellationToken.None);
        readTimer.Stop();

        Assert.Equal(FavoriteCount, storedFavorites);
        Assert.Equal(200, favoritePage.Items.Count);
        Assert.True(favoritePage.HasMore);
        AssertWithin(readTimer.Elapsed, OfflineReadBudget, "offline favorite query");

        Stopwatch searchTimer = Stopwatch.StartNew();
        ContentSearchPage searchPage = await search.SearchContentAsync(
            new(
                "p1needle",
                Type: ContentSearchResultType.FeedEntry,
                FeedId: FeedId,
                CategoryId: CategoryId,
                FavoritesOnly: true,
                Limit: 20),
            CancellationToken.None);
        searchTimer.Stop();

        Assert.Equal("entry-00001", Assert.Single(searchPage.Items).EntityId);
        Assert.False(searchPage.HasMore);
        AssertWithin(searchTimer.Elapsed, OfflineReadBudget, "offline unified search");

        AppPaths paths = new(_testRoot);
        using var assets = new EntryAssetStore(
            reopened,
            paths,
            new(MaximumBytes: 1024, MaximumAssetBytes: 128));
        var maintenance = new DatabaseMaintenanceService(
            paths,
            reopened,
            reopenedEntries,
            assets)
        {
            VacuumCapacityProbe = () => false
        };

        Stopwatch previewTimer = Stopwatch.StartNew();
        StorageCleanupPreview preview = await maintenance.PreviewCleanupAsync(
            Now.AddDays(-180),
            CancellationToken.None);
        previewTimer.Stop();

        Assert.Equal(
            EntryCount - FavoriteCount - ProtectedActiveTaskCount,
            preview.ExpiredFeedEntryCount);
        AssertWithin(
            previewTimer.Elapsed,
            CleanupPreviewBudget,
            "large-library cleanup preview");

        Stopwatch cleanupTimer = Stopwatch.StartNew();
        StorageCleanupResult cleanup = await maintenance.RunCleanupAsync(
            Now.AddDays(-180),
            CancellationToken.None);
        cleanupTimer.Stop();

        Assert.Equal(preview.ExpiredFeedEntryCount, cleanup.DeletedFeedEntryCount);
        Assert.False(cleanup.DatabaseOptimized);
        AssertWithin(
            cleanupTimer.Elapsed,
            CleanupExecutionBudget,
            "large-library bounded cleanup");
        Assert.Equal(
            FavoriteCount + ProtectedActiveTaskCount,
            await CountEntriesAsync(reopened));
        Assert.Equal(
            FavoriteCount,
            await favorites.GetCountAsync(CancellationToken.None));

        await AssertActiveAssociationsSurviveAsync(
            reopened,
            reopenedEntries,
            fullTextWork);

        ContentSearchResult retainedSearchResult = Assert.Single(
            (await search.SearchContentAsync(
                new(
                    "p1needle",
                    Type: ContentSearchResultType.FeedEntry,
                    FavoritesOnly: true,
                    Limit: 20),
                CancellationToken.None)).Items);
        Assert.Equal("entry-00001", retainedSearchResult.EntityId);
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        SqliteDatabase database = await OpenDatabaseAsync();
        await new FeedCatalogRepository(database).ReplaceAsync(
            new(
                new(1, FeedCatalogScope.Active, Now, Now),
                [
                    new(
                        CategoryId,
                        "Technology",
                        "technology",
                        1,
                        true,
                        1,
                        Now,
                        Now)
                ],
                [
                    new(
                        FeedId,
                        "https://offline.example/feed.xml",
                        "https://offline.example/feed.xml",
                        "Offline Acceptance Feed",
                        "https://offline.example/",
                        CategoryId,
                        FeedViewKind.Article,
                        60,
                        1,
                        true,
                        1,
                        Now,
                        Now,
                        FeedFullTextPolicy.OnOpen)
                ]),
            CancellationToken.None);
        return database;
    }

    private async Task<SqliteDatabase> OpenDatabaseAsync()
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        return database;
    }

    private static async Task SeedLargeLibraryAsync(SqliteDatabase database)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE sequence(value) AS (
                VALUES(1)
                UNION ALL
                SELECT value + 1
                FROM sequence
                WHERE value < $entryCount
            )
            INSERT INTO feed_entries(
                id, feed_id, external_id, normalized_url, title, author,
                published_at, updated_at, summary, sanitized_content,
                enclosure_json, content_hash, fetched_at, has_full_content)
            SELECT
                printf('entry-%05d', value),
                $feedId,
                printf('external-%05d', value),
                printf('https://offline.example/articles/%05d', value),
                printf('Offline library entry %05d', value),
                'Lenx',
                $oldTimestamp,
                $oldTimestamp,
                CASE value
                    WHEN 1 THEN 'p1needle offline acceptance favorite'
                    ELSE 'offline acceptance library'
                END,
                CASE value
                    WHEN 1 THEN 'p1needle cached article body'
                    ELSE 'cached article body'
                END,
                CASE value
                    WHEN 1004 THEN '[{"url":"https://media.example/audio.mp3","mediaType":"audio/mpeg","length":4096,"title":"Audio"},{"url":"https://media.example/video.mp4","mediaType":"video/mp4","length":8192,"title":"Video"},{"url":"https://media.example/cover.jpg","mediaType":"image/jpeg","length":1024,"title":"Cover"}]'
                    ELSE '[]'
                END,
                lower(printf('%064x', value)),
                $fetchedAt,
                0
            FROM sequence;

            WITH RECURSIVE sequence(value) AS (
                VALUES(1)
                UNION ALL
                SELECT value + 1
                FROM sequence
                WHERE value < $favoriteCount
            )
            INSERT INTO favorites(
                id, entity_type, entity_id, note, created_at)
            SELECT
                printf('favorite-%05d', value),
                'feed_entry',
                printf('entry-%05d', value),
                CASE value
                    WHEN 1 THEN 'p1needle retained favorite'
                    ELSE 'offline retained favorite'
                END,
                $fetchedAt
            FROM sequence;
            """;
        command.Parameters.AddWithValue("$entryCount", EntryCount);
        command.Parameters.AddWithValue("$favoriteCount", FavoriteCount);
        command.Parameters.AddWithValue("$feedId", FeedId);
        command.Parameters.AddWithValue(
            "$oldTimestamp",
            Format(Now.AddDays(-200)));
        command.Parameters.AddWithValue("$fetchedAt", Format(Now));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static FeedAutomationPlan RulePlan() => new(
        RuleEntryId,
        [
            new(
                RuleId,
                1,
                FeedAutomationRuleEvaluationOutcome.Matched)
        ],
        [
            new(
                RuleId,
                1,
                100,
                0,
                FeedAutomationActionType.Hide,
                0,
                null,
                FeedAutomationActionDisposition.Planned,
                FeedAutomationActionSuppressionReason.None,
                null,
                null,
                null)
        ]);

    private static FeedMediaDelivery MediaDelivery(FeedEntry entry) => new(
        entry.Id,
        entry.FeedId,
        entry.Title,
        "https://media.example/audio.mp3",
        "Audio",
        "audio/mpeg",
        4096,
        "media-job-p1-final",
        Now);

    private static MediaJob QueuedMediaJob() => new(
        "media-job-p1-final",
        "FeedTranscription",
        @"C:\Lenx\FeedMedia\p1-final.mp3",
        null,
        MediaJobStatus.Queued,
        0,
        TranscriptionEngine.Groq,
        "whisper-large-v3",
        0,
        0,
        null,
        Now,
        Now);

    private static async Task AssertActiveAssociationsSurviveAsync(
        SqliteDatabase database,
        FeedEntryRepository entries,
        FeedFullTextWorkItem fullTextWork)
    {
        Assert.NotNull(await entries.GetByIdAsync(
            FullTextEntryId,
            CancellationToken.None));
        Assert.NotNull(await entries.GetByIdAsync(
            AiEntryId,
            CancellationToken.None));
        Assert.NotNull(await entries.GetByIdAsync(
            RuleEntryId,
            CancellationToken.None));
        FeedEntry mediaEntry = Assert.IsType<FeedEntry>(
            await entries.GetByIdAsync(
                MixedMediaEntryId,
                CancellationToken.None));
        Assert.Equal(
            ["audio/mpeg", "video/mp4", "image/jpeg"],
            mediaEntry.Enclosures
                .Select(item => Assert.IsType<string>(item.MediaType))
                .ToArray());

        using var fullText = new FeedFullTextRepository(database);
        await fullText.ReleaseAsync(fullTextWork, Now, CancellationToken.None);
        Assert.NotNull(await fullText.ClaimOnOpenAsync(
            FullTextEntryId,
            Now,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));

        FeedAiAutomationJob aiJob = Assert.Single(
            await new FeedAiAutomationJobRepository(database).ClaimDueAsync(
                Now,
                10,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        Assert.Equal(AiEntryId, aiJob.EntryId);

        FeedAutomationRunSnapshot ruleSnapshot =
            await new FeedAutomationRunRepository(database).GetAsync(
                RuleEntryId,
                CancellationToken.None);
        Assert.Equal(
            FeedAutomationActionRunStatus.Pending,
            Assert.Single(ruleSnapshot.ActionRuns).Status);

        FeedMediaDeliveryRegistration media =
            Assert.IsType<FeedMediaDeliveryRegistration>(
                await new FeedMediaDeliveryRepository(database).GetAsync(
                    MixedMediaEntryId,
                    "https://media.example/audio.mp3",
                    CancellationToken.None));
        Assert.Equal(MediaJobStatus.Queued, media.Job.Status);
    }

    private static FeedEntryQuery Query(
        bool favoritesOnly,
        int limit) => new(
        null,
        FeedId,
        CategoryId,
        null,
        null,
        FeedEntryReadFilter.All,
        0,
        limit,
        ActiveOnly: true,
        FavoritesOnly: favoritesOnly);

    private static async Task<int> CountEntriesAsync(SqliteDatabase database)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM feed_entries;";
        return checked((int)(long)(
            await command.ExecuteScalarAsync(CancellationToken.None)
            ?? throw new InvalidDataException(
                "Feed entry count was unavailable.")));
    }

    private static void AssertWithin(
        TimeSpan elapsed,
        TimeSpan budget,
        string operation) => Assert.True(
        elapsed <= budget,
        $"{operation} took {elapsed.TotalMilliseconds:N0} ms; " +
        $"budget is {budget.TotalMilliseconds:N0} ms.");

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
