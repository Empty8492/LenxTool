using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationMediaActionServiceTests
{
    private const string FeedId =
        "30000000-0000-4000-8000-000000000501";
    private const string CategoryId =
        "20000000-0000-4000-8000-000000000501";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 17, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsyncSelectsFirstVerifiedAudioOrVideoAttachment()
    {
        FeedEnclosure expected = new(
            "https://media.example/episode.mp3",
            "audio/mpeg",
            1_024,
            "播客");
        var delivery = new StubDeliveryService();
        var inbox = new MediaJobInbox();
        var published = new List<MediaJob>();
        inbox.JobQueued += _ => throw new InvalidOperationException(
            "A broken UI subscriber must not fail delivery.");
        inbox.JobQueued += published.Add;
        var service = CreateService(
            delivery,
            inbox,
            enclosures:
            [
                new(
                    "https://media.example/cover.jpg",
                    "image/jpeg",
                    128,
                    "封面"),
                new(
                    "https://media.example/spoofed.bin",
                    "audio/mpeg",
                    512,
                    "未验证"),
                expected,
                new(
                    "https://media.example/video.mp4",
                    "video/mp4",
                    2_048,
                    "视频")
            ]);

        FeedAutomationMediaActionResult result = await service.ExecuteAsync(
            Lease(),
            CancellationToken.None);

        Assert.Equal(FeedAutomationMediaActionResult.Completed, result);
        Assert.Same(expected, Assert.Single(delivery.Enclosures));
        Assert.Equal("entry-501", Assert.Single(delivery.Entries).Id);
        Assert.Equal("job-media", Assert.Single(published).Id);
    }

    [Fact]
    public async Task ExecuteAsyncReturnsTerminalResultsWithoutDelivery()
    {
        var delivery = new StubDeliveryService();
        FeedAutomationMediaActionService missingEntry = CreateService(
            delivery,
            entryExists: false);
        FeedAutomationMediaActionService disabledFeed = CreateService(
            delivery,
            feedEnabled: false);
        FeedAutomationMediaActionService disabledCategory = CreateService(
            delivery,
            categoryEnabled: false);
        FeedAutomationMediaActionService noMedia = CreateService(
            delivery,
            enclosures:
            [
                new(
                    "https://media.example/cover.jpg",
                    "image/jpeg",
                    128,
                    "封面"),
                new(
                    "https://media.example/unknown.bin",
                    "audio/mpeg",
                    null,
                    "未验证")
            ]);

        Assert.Equal(
            FeedAutomationMediaActionResult.EntryMissing,
            await missingEntry.ExecuteAsync(Lease(), CancellationToken.None));
        Assert.Equal(
            FeedAutomationMediaActionResult.FeedUnavailable,
            await disabledFeed.ExecuteAsync(Lease(), CancellationToken.None));
        Assert.Equal(
            FeedAutomationMediaActionResult.FeedUnavailable,
            await disabledCategory.ExecuteAsync(Lease(), CancellationToken.None));
        Assert.Equal(
            FeedAutomationMediaActionResult.NoSupportedMedia,
            await noMedia.ExecuteAsync(Lease(), CancellationToken.None));
        Assert.Empty(delivery.Enclosures);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsUnsupportedActionBeforeEntryLookup()
    {
        var delivery = new StubDeliveryService();
        var entries = new StubEntryRepository(Entry([]));
        var service = new FeedAutomationMediaActionService(
            new StubCatalogRepository(Catalog()),
            entries,
            delivery,
            new MediaJobInbox());

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync(
            Lease() with
            {
                Type = FeedAutomationActionType.GenerateSummary,
                Value = "unsafe"
            },
            CancellationToken.None));

        Assert.Equal(0, entries.GetByIdCalls);
        Assert.Empty(delivery.Enclosures);
    }

    [Fact]
    public async Task ExecuteAsyncTreatsMissingCatalogAsRetryableWithoutDelivery()
    {
        var delivery = new StubDeliveryService();
        var catalog = new StubCatalogRepository(Catalog())
        {
            ReturnNullCatalog = true
        };
        var service = new FeedAutomationMediaActionService(
            catalog,
            new StubEntryRepository(Entry(
                [
                    new(
                        "https://media.example/episode.mp3",
                        "audio/mpeg",
                        1_024,
                        "播客")
                ])),
            delivery,
            new MediaJobInbox());

        AppException error = await Assert.ThrowsAsync<AppException>(() =>
            service.ExecuteAsync(Lease(), CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
        Assert.True(error.Error.IsRetryable);
        Assert.Empty(delivery.Enclosures);
    }

    private static FeedAutomationMediaActionService CreateService(
        StubDeliveryService delivery,
        MediaJobInbox? inbox = null,
        bool entryExists = true,
        bool feedEnabled = true,
        bool categoryEnabled = true,
        IReadOnlyList<FeedEnclosure>? enclosures = null) =>
        new(
            new StubCatalogRepository(Catalog(
                feedEnabled,
                categoryEnabled)),
            new StubEntryRepository(
                entryExists
                    ? Entry(enclosures ??
                        [
                            new(
                                "https://media.example/episode.mp3",
                                "audio/mpeg",
                                1_024,
                                "播客")
                        ])
                    : null),
            delivery,
            inbox ?? new MediaJobInbox());

    private static FeedCatalogSnapshot Catalog(
        bool feedEnabled = true,
        bool categoryEnabled = true) =>
        new(
            new(1, FeedCatalogScope.Active, Now, Now),
            [
                new(
                    CategoryId,
                    "Tech",
                    "tech",
                    0,
                    categoryEnabled,
                    1,
                    Now,
                    Now)
            ],
            [
                new(
                    FeedId,
                    "https://news.example/feed.xml",
                    "https://news.example/feed.xml",
                    "News",
                    null,
                    CategoryId,
                    FeedViewKind.Article,
                    60,
                    0,
                    feedEnabled,
                    1,
                    Now,
                    Now)
            ],
            FeedAiPolicy.SafeDefaults);

    private static FeedEntry Entry(
        IReadOnlyList<FeedEnclosure> enclosures) => new(
        "entry-501",
        FeedId,
        "external-501",
        "https://news.example/articles/501",
        "带媒体的文章",
        null,
        Now,
        Now,
        "摘要",
        "正文",
        [],
        enclosures,
        new string('a', 64),
        Now);

    private static FeedAutomationActionLease Lease() => new(
        new string('a', 64),
        "entry-501",
        "40000000-0000-4000-8000-000000000501",
        1,
        100,
        0,
        FeedAutomationActionType.SendToMedia,
        10,
        null,
        1,
        new string('b', 32));

    private sealed class StubDeliveryService : IFeedMediaDeliveryService
    {
        public List<FeedEntry> Entries { get; } = [];
        public List<FeedEnclosure> Enclosures { get; } = [];

        public Task<FeedMediaDeliveryRegistration> DeliverAsync(
            FeedEntry entry,
            FeedEnclosure enclosure,
            CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            Enclosures.Add(enclosure);
            DateTimeOffset now = Now;
            var job = new MediaJob(
                "job-media",
                "FeedTranscription",
                @"C:\media.mp3",
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
            return Task.FromResult(new FeedMediaDeliveryRegistration(
                new(
                    entry.Id,
                    entry.FeedId,
                    entry.Title,
                    enclosure.Url,
                    enclosure.Title,
                    enclosure.MediaType!,
                    enclosure.Length,
                    job.Id,
                    now),
                job,
                Created: true));
        }
    }

    private sealed class StubEntryRepository(FeedEntry? entry)
        : IFeedEntryRepository
    {
        public int GetByIdCalls { get; private set; }

        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken)
        {
            GetByIdCalls++;
            return Task.FromResult(
                entry?.Id == entryId
                    ? entry
                    : null);
        }

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCatalogRepository(
        FeedCatalogSnapshot snapshot) : IFeedCatalogRepository
    {
        public bool ReturnNullCatalog { get; init; }

        public Task<FeedCatalogState> GetStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(snapshot.State);

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedCatalogSnapshot?>(
                ReturnNullCatalog ? null : snapshot);

        public Task ReplaceAsync(
            FeedCatalogSnapshot value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
