using System.Globalization;
using System.Net;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Exports;

public sealed class ManagedEntryExporterTests
{
    private static readonly Uri Endpoint =
        new("https://integration.example.com/");
    private static readonly EntryIntegrationProbeContext Context = new(
        Endpoint,
        [IPAddress.Parse("203.0.113.10")]);

    [Fact]
    public async Task ReadeckUsesStableVisibleLabelAndConfiguredArchive()
    {
        var api = new FakeReadeckApi();
        var target = new ReadeckExportTarget("default", Endpoint, true, 1);
        var exporter = new ReadeckEntryExporter(
            new FakeTargetStore<ReadeckExportTarget>(target),
            Policy(EntryIntegrationKind.Readeck),
            new FakeCredentials("token"),
            new FakeAuthorizer(),
            api);

        EntryExportResult result = await exporter.ExportAsync(
            Request(ReadeckEntryExporter.ExporterId, target.CreateQueueTargetId()),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(api.Bookmark);
        Assert.StartsWith("lenxtool:", api.Bookmark.StableLabel, StringComparison.Ordinal);
        Assert.Contains(api.Bookmark.StableLabel, api.Bookmark.Labels);
        Assert.True(api.Bookmark.IsArchived);
        Assert.Equal(
            ReadeckEntryExporter.CreateStableLabel(Entry().Id),
            api.Bookmark.StableLabel);
    }

    [Fact]
    public async Task LegacyPlaceholderCredentialCannotActivateNewReadeckTarget()
    {
        var credentials = new FakeCredentials("legacy-token");
        var api = new FakeReadeckApi();
        var target = new ReadeckExportTarget("default", Endpoint, false);
        var exporter = new ReadeckEntryExporter(
            new FakeTargetStore<ReadeckExportTarget>(target),
            Policy(EntryIntegrationKind.Readeck),
            credentials,
            new FakeAuthorizer(),
            api);

        EntryExportException error = await Assert.ThrowsAsync<EntryExportException>(
            () => exporter.ExportAsync(
                Request(ReadeckEntryExporter.ExporterId, target.CreateQueueTargetId()),
                CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.CredentialsRequired, error.Error.Code);
        Assert.Equal(0, credentials.GetCount);
        Assert.Equal(0, api.CallCount);
    }

    [Fact]
    public async Task UnsignedWebhookDoesNotReadCredential()
    {
        var credentials = new FakeCredentials("old-secret");
        var api = new FakeWebhookApi();
        var target = new WebhookExportTarget(
            "default",
            new Uri("https://integration.example.com/hooks/entry"),
            UseHmac: false);
        var exporter = new WebhookEntryExporter(
            new FakeTargetStore<WebhookExportTarget>(target),
            Policy(EntryIntegrationKind.Webhook),
            credentials,
            new FakeAuthorizer(),
            api);

        EntryExportResult result = await exporter.ExportAsync(
            Request(WebhookEntryExporter.ExporterId, target.CreateQueueTargetId()),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, credentials.GetCount);
        Assert.Equal(1, api.ProbeCount);
        Assert.Equal(1, api.SendCount);
        Assert.Null(api.Secret);
        Assert.Equal(result.IdempotencyKey, api.Payload?.EventId);
    }

    [Fact]
    public async Task SignedWebhookRequiresExplicitlyConfirmedCredential()
    {
        var credentials = new FakeCredentials("old-secret");
        var target = new WebhookExportTarget(
            "default",
            new Uri("https://integration.example.com/hooks/entry"),
            UseHmac: true);
        var api = new FakeWebhookApi();
        var exporter = new WebhookEntryExporter(
            new FakeTargetStore<WebhookExportTarget>(target),
            Policy(EntryIntegrationKind.Webhook),
            credentials,
            new FakeAuthorizer(),
            api);

        EntryExportException error = await Assert.ThrowsAsync<EntryExportException>(
            () => exporter.ExportAsync(
                Request(WebhookEntryExporter.ExporterId, target.CreateQueueTargetId()),
                CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.CredentialsRequired, error.Error.Code);
        Assert.Equal(0, credentials.GetCount);
        Assert.Equal(0, api.ProbeCount);
    }

    [Fact]
    public async Task QBittorrentAcceptsValidatedMagnetOnlyForAllowedCategory()
    {
        var api = new FakeQBittorrentApi();
        var target = new QBittorrentExportTarget(
            "default",
            Endpoint,
            "downloads",
            CredentialVersion: 1);
        var policy = new EntryIntegrationPolicy(
            EntryIntegrationKind.QBittorrent,
            true,
            [Endpoint.IdnHost])
        {
            AllowedResources = ["downloads"]
        };
        var exporter = new QBittorrentEntryExporter(
            new FakeTargetStore<QBittorrentExportTarget>(target),
            new FakePolicyService(policy),
            new FakeCredentials("qbt_1234567890123456789012345678"),
            new FakeAuthorizer(),
            new RejectingTorrentFetcher(),
            api);
        FeedEntry entry = Entry() with
        {
            NormalizedUrl =
                "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&tr=https%3A%2F%2Ftracker.example.com%2Fprivate"
        };

        EntryExportResult result = await exporter.ExportAsync(
            Request(
                QBittorrentEntryExporter.ExporterId,
                target.CreateQueueTargetId(),
                entry),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            "0123456789abcdef0123456789abcdef01234567",
            result.RemoteId);
        Assert.IsType<QBittorrentMagnetSource>(api.Source);
        Assert.Equal("downloads", api.Category);
    }

    [Fact]
    public async Task QBittorrentTorrentFetchUnavailableRemainsRetryable()
    {
        var target = new QBittorrentExportTarget(
            "default",
            Endpoint,
            "downloads",
            CredentialVersion: 1);
        var policy = new EntryIntegrationPolicy(
            EntryIntegrationKind.QBittorrent,
            true,
            [Endpoint.IdnHost])
        {
            AllowedResources = ["downloads"]
        };
        var exporter = new QBittorrentEntryExporter(
            new FakeTargetStore<QBittorrentExportTarget>(target),
            new FakePolicyService(policy),
            new FakeCredentials("qbt_1234567890123456789012345678"),
            new FakeAuthorizer(),
            new FailingTorrentFetcher(new TorrentFileFetchException(
                TorrentFileFetchFailure.Unavailable)),
            new FakeQBittorrentApi());
        FeedEntry entry = Entry() with
        {
            NormalizedUrl = "https://news.example.com/item",
            Enclosures =
            [
                new(
                    "https://downloads.example.com/file.torrent",
                    "application/x-bittorrent",
                    1024,
                    null)
            ]
        };

        EntryExportException error = await Assert.ThrowsAsync<EntryExportException>(
            () => exporter.ExportAsync(
                Request(
                    QBittorrentEntryExporter.ExporterId,
                    target.CreateQueueTargetId(),
                    entry),
                CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.DestinationUnavailable,
            error.Error.Code);
        Assert.True(error.Error.IsRetryable);
    }

    [Fact]
    public async Task QBittorrentWrongCategoryStopsBeforeCredentialAndSource()
    {
        var credentials = new FakeCredentials("qbt_1234567890123456789012345678");
        var target = new QBittorrentExportTarget(
            "default",
            Endpoint,
            "private",
            CredentialVersion: 1);
        var exporter = new QBittorrentEntryExporter(
            new FakeTargetStore<QBittorrentExportTarget>(target),
            new FakePolicyService(new EntryIntegrationPolicy(
                EntryIntegrationKind.QBittorrent,
                true,
                [Endpoint.IdnHost])
            {
                AllowedResources = ["downloads"]
            }),
            credentials,
            new FakeAuthorizer(),
            new RejectingTorrentFetcher(),
            new FakeQBittorrentApi());

        EntryExportException error = await Assert.ThrowsAsync<EntryExportException>(
            () => exporter.ExportAsync(
                Request(QBittorrentEntryExporter.ExporterId, target.CreateQueueTargetId()),
                CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.AccessDenied, error.Error.Code);
        Assert.Equal(0, credentials.GetCount);
    }

    private static EntryExportRequest Request(
        string exporterId,
        string targetId,
        FeedEntry? entry = null) =>
        EntryExportRequest.Create(
            exporterId,
            targetId,
            entry ?? Entry(),
            EntryViewKind.Article,
            1024);

    private static FeedEntry Entry() => new(
        "entry-1",
        "feed-1",
        "external-1",
        "https://news.example.com/articles/1",
        "A title",
        "Ada",
        DateTimeOffset.Parse(
            "2026-08-13T00:00:00Z",
            CultureInfo.InvariantCulture),
        null,
        "Summary",
        "<p>Body</p>",
        ["research"],
        [],
        new string('a', 64),
        DateTimeOffset.Parse(
            "2026-08-13T00:01:00Z",
            CultureInfo.InvariantCulture));

    private static FakePolicyService Policy(EntryIntegrationKind kind) =>
        new(new EntryIntegrationPolicy(kind, true, [Endpoint.IdnHost]));

    private sealed class FakeTargetStore<T>(T target)
        : IIntegrationExportTargetStore<T>
        where T : class
    {
        public Task<T?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<T?>(target);
        public Task<IIntegrationExportTargetLease<T>> AcquireExportLeaseAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IIntegrationExportTargetLease<T>>(new Lease(target));
        public Task SaveAsync(T value, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        private sealed class Lease(T value) : IIntegrationExportTargetLease<T>
        {
            public T? Target { get; } = value;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakePolicyService(EntryIntegrationPolicy policy)
        : IEntryIntegrationPolicyService
    {
        public Task<EntryIntegrationPolicySnapshot> GetAsync(
            EntryIntegrationPolicyScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EntryIntegrationPolicySnapshot(1, [policy]));
        public Task<EntryIntegrationPolicyMutationResult> ReplaceAsync(
            IReadOnlyList<EntryIntegrationPolicyInput> policies,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCredentials(string? value)
        : IEntryIntegrationCredentialStore
    {
        public int GetCount { get; private set; }
        public Task<string?> GetAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            GetCount++;
            return Task.FromResult(value);
        }
        public Task<bool> ExistsAsync(EntryIntegrationKind kind, string targetId, CancellationToken cancellationToken) =>
            Task.FromResult(value is not null);
        public Task SetAsync(EntryIntegrationKind kind, string targetId, string newValue, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task DeleteAsync(EntryIntegrationKind kind, string targetId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeAuthorizer : IEntryIntegrationEndpointAuthorizer
    {
        public Task<EntryIntegrationProbeContext?> AuthorizeAsync(
            EntryIntegrationTarget target,
            EntryIntegrationPolicy policy,
            CancellationToken cancellationToken) =>
            Task.FromResult<EntryIntegrationProbeContext?>(
                Context with { Endpoint = target.Endpoint });
    }

    private sealed class FakeReadeckApi : IReadeckApiClient
    {
        public int CallCount { get; private set; }
        public ReadeckBookmark Bookmark { get; private set; } = null!;
        public Task ProbeAsync(EntryIntegrationProbeContext context, string token, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<ReadeckBookmarkResult> UpsertAsync(
            EntryIntegrationProbeContext context,
            string token,
            ReadeckBookmark bookmark,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Bookmark = bookmark;
            return Task.FromResult(new ReadeckBookmarkResult(
                "bookmark-1",
                new Uri(context.Endpoint, "/api/bookmarks/bookmark-1")));
        }
    }

    private sealed class FakeWebhookApi : IWebhookApiClient
    {
        public int ProbeCount { get; private set; }
        public int SendCount { get; private set; }
        public string? Secret { get; private set; }
        public WebhookEntryPayload? Payload { get; private set; }
        public Task ProbeAsync(EntryIntegrationProbeContext context, CancellationToken cancellationToken)
        {
            ProbeCount++;
            return Task.CompletedTask;
        }
        public Task SendAsync(EntryIntegrationProbeContext context, string? hmacSecret, WebhookEntryPayload payload, CancellationToken cancellationToken)
        {
            SendCount++;
            Secret = hmacSecret;
            Payload = payload;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeQBittorrentApi : IQBittorrentApiClient
    {
        public QBittorrentSource? Source { get; private set; }
        public string? Category { get; private set; }
        public Task ProbeAsync(EntryIntegrationProbeContext context, string apiKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task AddAsync(EntryIntegrationProbeContext context, string apiKey, QBittorrentSource source, string category, CancellationToken cancellationToken)
        {
            Source = source;
            Category = category;
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingTorrentFetcher : ITorrentFileFetcher
    {
        public Task<QBittorrentFileSource> FetchAsync(FeedEnclosure enclosure, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Fetcher must not be used for a magnet.");
    }

    private sealed class FailingTorrentFetcher(Exception exception)
        : ITorrentFileFetcher
    {
        public Task<QBittorrentFileSource> FetchAsync(
            FeedEnclosure enclosure,
            CancellationToken cancellationToken) =>
            Task.FromException<QBittorrentFileSource>(exception);
    }
}
