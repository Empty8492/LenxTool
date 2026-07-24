using System.Net;
using System.Net.Http.Headers;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class CachedArticleImageDownloaderTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task CacheHitWorksWithoutDnsOrNetwork()
    {
        var store = new FakeAssetStore();
        store.Seed("entry-1", "https://images.example/one.png", "image/png", PngBytes);
        var resolver = new FakeResolver((_, _) => [PublicAddress]);
        var transport = new FakeTransport((_, _, _, _) => throw new IOException("offline"));
        var downloader = CreateDownloader(store, resolver, transport);

        ArticleImageContent? content = await downloader.GetAsync(
            "entry-1",
            "https://images.example/one.png",
            "https://site.example/article",
            new ArticleImageDownloadBudget(10, 1024),
            CancellationToken.None);

        Assert.NotNull(content);
        Assert.True(content.FromCache);
        Assert.Equal("image/png", content.MimeType);
        Assert.Equal(PngBytes, content.Bytes);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task ValidDownloadUsesPinnedAddressAndIsPersisted()
    {
        var store = new FakeAssetStore();
        var resolver = new FakeResolver((host, _) =>
        {
            Assert.Equal("images.example", host);
            return [PublicAddress];
        });
        var transport = new FakeTransport((uri, addresses, referrer, _) =>
        {
            Assert.Equal("https://images.example/one.png", uri.AbsoluteUri);
            Assert.Equal([PublicAddress], addresses);
            Assert.Equal("https://site.example/", referrer?.AbsoluteUri);
            return Response(HttpStatusCode.OK, "image/png", PngBytes);
        });
        var downloader = CreateDownloader(store, resolver, transport);

        ArticleImageContent? content = await downloader.GetAsync(
            "entry-1",
            "https://images.example/one.png",
            "https://site.example/article",
            new ArticleImageDownloadBudget(10, 1024),
            CancellationToken.None);

        Assert.NotNull(content);
        Assert.False(content.FromCache);
        Assert.Equal(PngBytes, content.Bytes);
        Assert.Equal(1, store.PutCount);
        Assert.Equal("image/png", store.Asset?.MimeType);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    public async Task UnsupportedOrSpoofedMimeIsRejectedWithoutCaching(string mediaType)
    {
        var store = new FakeAssetStore();
        var resolver = new FakeResolver((_, _) => [PublicAddress]);
        var transport = new FakeTransport((_, _, _, _) =>
            Response(HttpStatusCode.OK, mediaType, PngBytes));
        var downloader = CreateDownloader(store, resolver, transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.GetAsync(
            "entry-1",
            "https://images.example/spoofed",
            null,
            new ArticleImageDownloadBudget(10, 1024),
            CancellationToken.None));

        Assert.Equal(0, store.PutCount);
    }

    [Fact]
    public async Task SvgPayloadDisguisedAsPngIsRejectedWithoutCaching()
    {
        var store = new FakeAssetStore();
        var resolver = new FakeResolver((_, _) => [PublicAddress]);
        var transport = new FakeTransport((_, _, _, _) =>
            Response(
                HttpStatusCode.OK,
                "image/png",
                "<svg xmlns='http://www.w3.org/2000/svg'><script/></svg>"u8.ToArray()));
        var downloader = CreateDownloader(store, resolver, transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.GetAsync(
            "entry-1",
            "https://images.example/disguised.png",
            null,
            new ArticleImageDownloadBudget(10, 1024),
            CancellationToken.None));

        Assert.Equal(0, store.PutCount);
    }

    [Fact]
    public async Task RedirectToPrivateAddressIsRejectedBeforeSecondRequest()
    {
        var store = new FakeAssetStore();
        var resolver = new FakeResolver((_, _) => [PublicAddress]);
        var transport = new FakeTransport((_, _, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://10.0.0.5/private.png");
            return new(response);
        });
        var downloader = CreateDownloader(store, resolver, transport);

        AppException error = await Assert.ThrowsAsync<AppException>(() => downloader.GetAsync(
            "entry-1",
            "https://images.example/start",
            null,
            new ArticleImageDownloadBudget(10, 1024),
            CancellationToken.None));

        Assert.Equal(AppErrorCode.AccessDenied, error.Error.Code);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task RecentFailureSuppressesRepeatedOfflineRequest()
    {
        var store = new FakeAssetStore();
        var resolver = new FakeResolver((_, _) => [PublicAddress]);
        var transport = new FakeTransport((_, _, _, _) =>
            throw new HttpRequestException("offline"));
        var downloader = CreateDownloader(store, resolver, transport);
        var budget = new ArticleImageDownloadBudget(10, 1024);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.GetAsync(
            "entry-1",
            "https://images.example/offline.png",
            null,
            budget,
            CancellationToken.None));
        ArticleImageContent? retry = await downloader.GetAsync(
            "entry-1",
            "https://images.example/offline.png",
            null,
            budget,
            CancellationToken.None);

        Assert.Null(retry);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task PerArticleResourceAndNetworkBudgetsStopAdditionalDownloads()
    {
        var store = new FakeAssetStore();
        var resolver = new FakeResolver((_, _) => [PublicAddress]);
        var transport = new FakeTransport((_, _, _, _) =>
            Response(HttpStatusCode.OK, "image/png", PngBytes));
        var downloader = CreateDownloader(store, resolver, transport);
        var oneResource = new ArticleImageDownloadBudget(1, 1024);

        Assert.NotNull(await downloader.GetAsync(
            "entry-1",
            "https://images.example/one.png",
            null,
            oneResource,
            CancellationToken.None));
        Assert.Null(await downloader.GetAsync(
            "entry-1",
            "https://images.example/two.png",
            null,
            oneResource,
            CancellationToken.None));

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.GetAsync(
            "entry-2",
            "https://images.example/three.png",
            null,
            new ArticleImageDownloadBudget(1, PngBytes.Length - 1),
            CancellationToken.None));
        Assert.Equal(2, transport.CallCount);
        Assert.Equal(1, store.PutCount);
    }

    [Fact]
    public async Task CallerCancellationIsPropagatedAndNotNegativeCached()
    {
        var store = new FakeAssetStore();
        var resolver = new FakeResolver((_, _) => [PublicAddress]);
        int sequence = 0;
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(async (_, _, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref sequence) == 1)
            {
                requestStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return Response(HttpStatusCode.OK, "image/png", PngBytes);
        }, isAsync: true);
        var downloader = CreateDownloader(store, resolver, transport);
        using var cancellation = new CancellationTokenSource();

        Task<ArticleImageContent?> cancelled = downloader.GetAsync(
            "entry-1",
            "https://images.example/cancel.png",
            null,
            new ArticleImageDownloadBudget(10, 1024),
            cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        ArticleImageContent? retry = await downloader.GetAsync(
            "entry-1",
            "https://images.example/cancel.png",
            null,
            new ArticleImageDownloadBudget(10, 1024),
            CancellationToken.None);

        Assert.NotNull(retry);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task GlobalConcurrencyLimitQueuesSecondNetworkRequest()
    {
        var store = new FakeAssetStore();
        var resolver = new FakeResolver((_, _) => [PublicAddress]);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sequence = 0;
        var transport = new FakeTransport(async (_, _, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref sequence) == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            return Response(HttpStatusCode.OK, "image/png", PngBytes);
        }, isAsync: true);
        var downloader = CreateDownloader(
            store,
            resolver,
            transport,
            TestDownloadOptions() with { MaximumConcurrentDownloads = 1 });
        var budget = new ArticleImageDownloadBudget(10, 1024);

        Task<ArticleImageContent?> first = downloader.GetAsync(
            "entry-1",
            "https://images.example/one.png",
            null,
            budget,
            CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ArticleImageContent?> second = downloader.GetAsync(
            "entry-1",
            "https://images.example/two.png",
            null,
            budget,
            CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(1, transport.CallCount);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, transport.CallCount);
    }

    private static CachedArticleImageDownloader CreateDownloader(
        IEntryAssetStore store,
        IFeedHostResolver resolver,
        IArticleImageTransport transport,
        ArticleImageDownloadOptions? options = null) =>
        new(
            store,
            resolver,
            transport,
            FeedDiscoveryOptions.Default,
            options ?? TestDownloadOptions(),
            AssetCacheOptions.Default,
            TimeProvider.System);

    private static ArticleImageDownloadOptions TestDownloadOptions() => new(
        TimeSpan.FromSeconds(5),
        MaximumRedirects: 3,
        MaximumConcurrentDownloads: 4,
        FailureRetryDelay: TimeSpan.FromMinutes(5));

    private static ArticleImageHttpResponse Response(
        HttpStatusCode status,
        string mediaType,
        byte[] bytes)
    {
        var message = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(bytes)
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new(message);
    }

    private sealed class FakeResolver(
        Func<string, CancellationToken, IReadOnlyList<IPAddress>> resolve)
        : IFeedHostResolver
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(resolve(host, cancellationToken));
        }
    }

    private sealed class FakeTransport : IArticleImageTransport
    {
        private readonly Func<
            Uri,
            IReadOnlyList<IPAddress>,
            Uri?,
            CancellationToken,
            Task<ArticleImageHttpResponse>> _send;

        public FakeTransport(
            Func<
                Uri,
                IReadOnlyList<IPAddress>,
                Uri?,
                CancellationToken,
                ArticleImageHttpResponse> send)
        {
            _send =
                (uri, addresses, referrer, cancellationToken) =>
                    Task.FromResult(send(uri, addresses, referrer, cancellationToken));
        }

        public FakeTransport(
            Func<
                Uri,
                IReadOnlyList<IPAddress>,
                Uri?,
                CancellationToken,
                Task<ArticleImageHttpResponse>> send,
            bool isAsync)
        {
            _ = isAsync;
            _send = send;
        }

        public int CallCount { get; private set; }

        public async Task<ArticleImageHttpResponse> SendAsync(
            Uri uri,
            IReadOnlyList<IPAddress> addresses,
            Uri? referrer,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return await _send(uri, addresses, referrer, cancellationToken);
        }
    }

    private sealed class FakeAssetStore : IEntryAssetStore
    {
        private byte[]? _bytes;

        public EntryAsset? Asset { get; private set; }
        public int PutCount { get; private set; }

        public void Seed(
            string entryId,
            string sourceUrl,
            string mimeType,
            byte[] bytes)
        {
            _bytes = bytes.ToArray();
            Asset = new(
                entryId,
                sourceUrl,
                new string('a', 64),
                mimeType,
                bytes.Length,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
        }

        public Task<EntryAsset?> GetAsync(
            string entryId,
            string sourceUrl,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Asset is not null
                && Asset.EntryId == entryId
                && Asset.SourceUrl == sourceUrl
                    ? Asset
                    : null);

        public async Task<EntryAsset> PutAsync(
            string entryId,
            string sourceUrl,
            string mimeType,
            Stream content,
            CancellationToken cancellationToken)
        {
            using var destination = new MemoryStream();
            await content.CopyToAsync(destination, cancellationToken);
            PutCount++;
            Seed(entryId, sourceUrl, mimeType, destination.ToArray());
            return Asset!;
        }

        public Task<Stream?> OpenReadAsync(
            EntryAsset asset,
            CancellationToken cancellationToken) =>
            Task.FromResult<Stream?>(
                _bytes is null ? null : new MemoryStream(_bytes, writable: false));

        public Task<int> PruneAsync(
            IReadOnlyCollection<string> protectedContentHashes,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
