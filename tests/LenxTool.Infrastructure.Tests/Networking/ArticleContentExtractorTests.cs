using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class ArticleContentExtractorTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");

    [Fact]
    public async Task DownloadsParsesAndReturnsFinalUrl()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            uri.AbsolutePath == "/start"
                ? Redirect("/final")
                : HtmlResponse("<article><h1>Final</h1><p>The final article body is readable and complete.</p></article>"));
        ArticleContentExtractor extractor = CreateExtractor(resolver, transport);

        ArticleContentResult result = await extractor.ExtractAsync(
            "https://news.example/start",
            CancellationToken.None);

        Assert.Equal("https://news.example/start", result.RequestedUrl);
        Assert.Equal("https://news.example/final", result.FinalUrl);
        Assert.Equal("Final", result.Title);
        Assert.Equal(2, transport.CallCount);
        Assert.Equal([PublicAddress], transport.Calls[0].Addresses);
    }

    [Fact]
    public async Task DeclaredChineseEncodingIsDecoded()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.GetEncoding(
            "gb18030",
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        byte[] body = encoding.GetBytes(
            "<html><head><title>中文标题</title></head><body><article>" +
            "<p>这是一段使用 GB18030 编码的中文正文，解码后应保持完整。</p>" +
            "</article></body></html>");
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            HtmlResponse(body, "gb18030"));
        ArticleContentExtractor extractor = CreateExtractor(resolver, transport);

        ArticleContentResult result = await extractor.ExtractAsync(
            "https://news.example/chinese",
            CancellationToken.None);

        Assert.Equal("中文标题", result.Title);
        Assert.Contains(result.Blocks, block => block.Text.Contains("保持完整", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrivateDnsAnswerIsRejectedBeforeNetwork()
    {
        var resolver = new FakeResolver((host, cancellationToken) =>
            [IPAddress.Parse("10.0.0.8")]);
        var transport = new FakeTransport(
            (Func<
                Uri,
                IReadOnlyList<IPAddress>,
                CancellationToken,
                ArticleContentHttpResponse>)(
                (uri, addresses, cancellationToken) =>
                    throw new InvalidOperationException(
                        "Network must not be reached")));
        ArticleContentExtractor extractor = CreateExtractor(resolver, transport);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => extractor.ExtractAsync(
                "https://news.example/private",
                CancellationToken.None));

        Assert.Equal(AppErrorCode.AccessDenied, error.Error.Code);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task RedirectToPrivateAddressIsRejectedBeforeSecondRequest()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            Redirect("https://10.0.0.4/internal"));
        ArticleContentExtractor extractor = CreateExtractor(resolver, transport);

        await Assert.ThrowsAsync<AppException>(
            () => extractor.ExtractAsync(
                "https://news.example/start",
                CancellationToken.None));

        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task DeclaredResponseSizeLimitIsEnforced()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
        {
            ArticleContentHttpResponse response = HtmlResponse(
                "<article><p>small body</p></article>");
            response.Message.Content.Headers.ContentLength = 1025;
            return response;
        });
        ArticleContentExtractionOptions options =
            TestOptions() with { MaximumDownloadBytes = 1024 };
        ArticleContentExtractor extractor = CreateExtractor(resolver, transport, options);

        await Assert.ThrowsAsync<AppException>(
            () => extractor.ExtractAsync(
                "https://news.example/large",
                CancellationToken.None));
    }

    [Fact]
    public async Task CallerCancellationIsPropagated()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport(async (uri, addresses, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        ArticleContentExtractor extractor = CreateExtractor(resolver, transport);
        using var cancellation = new CancellationTokenSource();

        Task<ArticleContentResult> extraction = extractor.ExtractAsync(
            "https://news.example/article",
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extraction);
    }

    [Fact]
    public async Task TotalTimeoutMapsToStructuredTimeout()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport(async (uri, addresses, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        ArticleContentExtractionOptions options =
            TestOptions() with { TotalTimeout = TimeSpan.FromMilliseconds(30) };
        ArticleContentExtractor extractor = CreateExtractor(resolver, transport, options);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => extractor.ExtractAsync(
                "https://news.example/slow",
                CancellationToken.None));

        Assert.Equal(AppErrorCode.Timeout, error.Error.Code);
    }

    [Fact]
    public async Task SameHostRequestsRespectConcurrencyLimit()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximumActive = 0;
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport(async (uri, addresses, cancellationToken) =>
        {
            int nowActive = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, nowActive);
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref active);
            return HtmlResponse(
                "<article><p>This article has enough text for the extraction test.</p></article>");
        });
        ArticleContentExtractionOptions options =
            TestOptions() with { MaximumConcurrentRequestsPerHost = 1 };
        using ArticleContentExtractor extractor = CreateExtractor(
            resolver,
            transport,
            options);

        Task<ArticleContentResult> first = extractor.ExtractAsync(
            "https://news.example/one",
            CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ArticleContentResult> second = extractor.ExtractAsync(
            "https://news.example/two",
            CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(1, transport.CallCount);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, maximumActive);
    }

    private static ArticleContentExtractor CreateExtractor(
        IFeedHostResolver resolver,
        IArticleContentTransport transport,
        ArticleContentExtractionOptions? options = null)
    {
        ArticleContentExtractionOptions extractionOptions = options ?? TestOptions();
        return new(
            resolver,
            transport,
            FeedOptions(),
            extractionOptions,
            new HtmlArticleContentParser(extractionOptions));
    }

    private static ArticleContentExtractionOptions TestOptions() =>
        ArticleContentExtractionOptions.Default with
        {
            TotalTimeout = TimeSpan.FromSeconds(2),
            MaximumDownloadBytes = 64 * 1024,
            MaximumDecodedBytes = 128 * 1024
        };

    private static FeedDiscoveryOptions FeedOptions() =>
        FeedDiscoveryOptions.Default with
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(250),
            AllowedHttpHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            TrustedPrivateHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

    private static ArticleContentHttpResponse HtmlResponse(
        string content,
        string charset = "utf-8") =>
        HtmlResponse(Encoding.UTF8.GetBytes(content), charset);

    private static ArticleContentHttpResponse HtmlResponse(
        byte[] content,
        string charset)
    {
        var message = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html")
        {
            CharSet = charset
        };
        return new(message);
    }

    private static ArticleContentHttpResponse Redirect(string location)
    {
        var message = new HttpResponseMessage(HttpStatusCode.Redirect);
        message.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return new(message);
    }

    private sealed class FakeResolver(
        Func<string, CancellationToken, IReadOnlyList<IPAddress>> resolve)
        : IFeedHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            Task.FromResult(resolve(host, cancellationToken));
    }

    private sealed class FakeTransport(
        Func<Uri, IReadOnlyList<IPAddress>, CancellationToken, Task<ArticleContentHttpResponse>> send)
        : IArticleContentTransport
    {
        public FakeTransport(
            Func<Uri, IReadOnlyList<IPAddress>, CancellationToken, ArticleContentHttpResponse> send)
            : this((uri, addresses, cancellationToken) =>
                Task.FromResult(send(uri, addresses, cancellationToken)))
        {
        }

        public List<(Uri Uri, IReadOnlyList<IPAddress> Addresses)> Calls { get; } = [];
        public int CallCount => Calls.Count;

        public async Task<ArticleContentHttpResponse> SendAsync(
            Uri uri,
            IReadOnlyList<IPAddress> addresses,
            CancellationToken cancellationToken)
        {
            Calls.Add((uri, addresses));
            return await send(uri, addresses, cancellationToken);
        }
    }
}
