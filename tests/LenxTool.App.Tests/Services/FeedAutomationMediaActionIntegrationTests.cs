using System.Net;
using System.Net.Http.Headers;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationMediaActionIntegrationTests : IDisposable
{
    private const string FeedId =
        "30000000-0000-4000-8000-000000000601";
    private const string CategoryId =
        "20000000-0000-4000-8000-000000000601";
    private const string RuleId =
        "40000000-0000-4000-8000-000000000601";
    private const string EntryId = "entry-media-e2e";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Mp3Bytes =
        "ID3\u0004\u0000\u0000\u0000\u0000\u0000\u0015e2e-audio"u8.ToArray();
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools media action integration tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DurableSendToMediaActionCreatesRecoverableGateZeroJob()
    {
        var paths = new AppPaths(_testRoot);
        using (var database = new SqliteDatabase(
                   paths,
                   NullLogger<SqliteDatabase>.Instance))
        {
            await database.InitializeAsync(CancellationToken.None);
            await SeedCatalogAndEntryAsync(database);
            await new FeedAutomationRunRepository(database).StageAsync(
                Plan(),
                Now,
                CancellationToken.None);

            var transport = new FakeTransport();
            using var delivery = new FeedMediaDeliveryService(
                new FeedMediaDeliveryRepository(database),
                new FakeResolver(),
                transport,
                FeedDiscoveryOptions.Default,
                new(
                    MaximumBytes: 1_024,
                    TotalTimeout: TimeSpan.FromSeconds(5),
                    MaximumRedirects: 3,
                    MaximumConcurrentDownloads: 1),
                paths,
                new FixedTimeProvider(Now));
            var inbox = new MediaJobInbox();
            var published = new List<MediaJob>();
            inbox.JobQueued += published.Add;
            var actions = new FeedAutomationMediaActionService(
                new FeedCatalogRepository(database),
                new FeedEntryRepository(database),
                delivery,
                inbox);
            var processor = new FeedAutomationMediaActionProcessor(
                new FeedAutomationActionQueueRepository(database),
                actions,
                new FixedTimeProvider(Now),
                FeedAutomationActionProcessorOptions.Default with
                {
                    BatchSize = 1,
                    InitialDelay = TimeSpan.Zero
                });

            Assert.Equal(
                1,
                await processor.ProcessBackgroundBatchAsync(
                    CancellationToken.None));

            FeedAutomationActionRun action = Assert.Single(
                (await new FeedAutomationRunRepository(database).GetAsync(
                    EntryId,
                    CancellationToken.None)).ActionRuns);
            Assert.Equal(FeedAutomationActionRunStatus.Succeeded, action.Status);
            Assert.Null(action.LastErrorCode);
            MediaJob queued = Assert.Single(
                await new MediaJobRepository(database).GetQueuedAsync(
                    CancellationToken.None));
            Assert.Equal("FeedTranscription", queued.Kind);
            Assert.Equal(queued, Assert.Single(published));
            Assert.Equal(Mp3Bytes, await File.ReadAllBytesAsync(queued.InputPath));
            FeedMediaDeliveryRegistration registration = Assert.IsType<
                FeedMediaDeliveryRegistration>(
                await new FeedMediaDeliveryRepository(database).GetAsync(
                    EntryId,
                    "https://media.example/episode.mp3",
                    CancellationToken.None));
            Assert.Equal(queued, registration.Job);
            Assert.Equal(1, transport.CallCount);
        }

        using var reopened = new SqliteDatabase(
            paths,
            NullLogger<SqliteDatabase>.Instance);
        await reopened.InitializeAsync(CancellationToken.None);

        MediaJob restored = Assert.Single(
            await new MediaJobRepository(reopened).GetQueuedAsync(
                CancellationToken.None));
        Assert.True(File.Exists(restored.InputPath));
        Assert.NotNull(await new FeedMediaDeliveryRepository(reopened).GetAsync(
            EntryId,
            "https://media.example/episode.mp3",
            CancellationToken.None));
    }

    private static async Task SeedCatalogAndEntryAsync(
        SqliteDatabase database)
    {
        var catalog = new FeedCatalogSnapshot(
            new(1, FeedCatalogScope.All, Now, Now),
            [
                new(
                    CategoryId,
                    "Tech",
                    "tech",
                    0,
                    true,
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
                    true,
                    1,
                    Now,
                    Now)
            ],
            FeedAiPolicy.SafeDefaults);
        await new FeedCatalogRepository(database).ReplaceAsync(
            catalog,
            CancellationToken.None);
        FeedEntry entry = new(
            EntryId,
            FeedId,
            "external-media-e2e",
            "https://news.example/articles/media-e2e",
            "端到端音频",
            null,
            Now,
            Now,
            "摘要",
            "正文",
            [],
            [
                new(
                    "https://media.example/episode.mp3",
                    "audio/mpeg",
                    Mp3Bytes.Length,
                    "小音频")
            ],
            new string('c', 64),
            Now);
        await new FeedEntryRepository(database).UpsertAsync(
            FeedId,
            [entry],
            CancellationToken.None);
    }

    private static FeedAutomationPlan Plan() => new(
        EntryId,
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
                FeedAutomationActionType.SendToMedia,
                10,
                null,
                FeedAutomationActionDisposition.Planned,
                FeedAutomationActionSuppressionReason.None,
                null,
                null,
                null)
        ]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeResolver : IFeedHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(
                [IPAddress.Parse("93.184.216.34")]);
    }

    private sealed class FakeTransport : IFeedMediaTransport
    {
        public int CallCount { get; private set; }

        public Task<FeedMediaHttpResponse> SendAsync(
            Uri uri,
            IReadOnlyList<IPAddress> addresses,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Mp3Bytes)
            };
            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue("audio/mpeg");
            return Task.FromResult(new FeedMediaHttpResponse(response));
        }
    }
}
