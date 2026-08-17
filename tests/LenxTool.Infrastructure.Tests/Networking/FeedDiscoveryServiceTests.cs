using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedDiscoveryServiceTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");

    [Fact]
    public async Task HttpIsRejectedByDefaultBeforeDnsOrNetworkAccess()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            UnexpectedNetwork());
        var service = CreateService(resolver, transport);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync("http://feeds.example/rss.xml", CancellationToken.None));

        Assert.Equal(AppErrorCode.InvalidRequest, error.Error.Code);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task ExplicitHttpHostCanReturnDirectRss()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            Response(HttpStatusCode.OK, "application/rss+xml", Rss("Daily News")));
        FeedDiscoveryOptions options = TestOptions() with
        {
            AllowedHttpHosts = HostSet("feeds.example")
        };
        var service = new FeedDiscoveryService(resolver, transport, options);

        FeedDiscoveryResult result = await service.DiscoverAsync(
            "http://feeds.example/rss.xml?edition=cn",
            CancellationToken.None);

        DiscoveredFeed feed = Assert.Single(result.Feeds);
        Assert.Equal("http://feeds.example/rss.xml?edition=cn", feed.FeedUrl);
        Assert.Equal("Daily News", feed.Title);
        Assert.Equal(FeedDocumentKind.Rss20, feed.Kind);
        Assert.Equal([PublicAddress], transport.Calls.Single().Addresses);
    }

    [Fact]
    public async Task PrivateHttpFeedRequiresSeparateHttpAndPrivateHostTrust()
    {
        IPAddress privateAddress = IPAddress.Parse("10.20.30.40");
        var resolver = new FakeResolver((host, cancellationToken) => [privateAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            Response(HttpStatusCode.OK, "application/rss+xml", Rss("Intranet")));
        FeedDiscoveryOptions httpOnly = TestOptions() with
        {
            AllowedHttpHosts = HostSet("intranet.example")
        };

        AppException blocked = await Assert.ThrowsAsync<AppException>(() =>
            new FeedDiscoveryService(resolver, transport, httpOnly).DiscoverAsync(
                "http://intranet.example/feed",
                CancellationToken.None));

        Assert.Equal(AppErrorCode.AccessDenied, blocked.Error.Code);
        Assert.Equal(0, transport.CallCount);

        FeedDiscoveryOptions explicitlyTrusted = httpOnly with
        {
            TrustedPrivateHosts = HostSet("intranet.example")
        };
        FeedDiscoveryResult result = await new FeedDiscoveryService(resolver, transport, explicitlyTrusted)
            .DiscoverAsync("http://intranet.example/feed", CancellationToken.None);
        Assert.Single(result.Feeds);
        Assert.Equal([privateAddress], transport.Calls.Single().Addresses);
    }

    [Theory]
    [InlineData("https://feeds.example:8443/rss")]
    [InlineData("http://feeds.example:8080/rss")]
    public async Task NonDefaultPortsAreRejected(string url)
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) => UnexpectedNetwork());
        FeedDiscoveryOptions options = TestOptions() with { AllowedHttpHosts = HostSet("feeds.example") };
        var service = CreateService(resolver, transport, options);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync(url, CancellationToken.None));

        Assert.Equal(AppErrorCode.InvalidRequest, error.Error.Code);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, transport.CallCount);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.10.20")]
    [InlineData("100.64.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("64:ff9b::a00:1")]
    public async Task NonPublicLiteralAddressesAreRejected(string address)
    {
        string host = address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address;
        var resolver = new FakeResolver((resolvedHost, cancellationToken) =>
            throw new InvalidOperationException("Literal addresses must not use DNS"));
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            UnexpectedNetwork());
        var service = CreateService(resolver, transport);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync($"https://{host}/feed", CancellationToken.None));

        Assert.Equal(AppErrorCode.AccessDenied, error.Error.Code);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task MixedPublicAndPrivateDnsAnswerIsRejected()
    {
        var resolver = new FakeResolver((host, cancellationToken) =>
            [PublicAddress, IPAddress.Parse("10.0.0.8")]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            UnexpectedNetwork());
        var service = CreateService(resolver, transport);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync("https://feeds.example/rss", CancellationToken.None));

        Assert.Equal(AppErrorCode.AccessDenied, error.Error.Code);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task ValidatedDnsAddressIsPinnedIntoTransportRequest()
    {
        int resolverCalls = 0;
        var resolver = new FakeResolver((host, cancellationToken) =>
        {
            resolverCalls++;
            return resolverCalls == 1
                ? [PublicAddress]
                : [IPAddress.Parse("10.0.0.9")];
        });
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            Response(HttpStatusCode.OK, "application/atom+xml", Atom("Pinned")));
        var service = CreateService(resolver, transport);

        await service.DiscoverAsync("https://feeds.example/atom", CancellationToken.None);

        Assert.Equal(1, resolverCalls);
        Assert.Equal([PublicAddress], transport.Calls.Single().Addresses);
    }

    [Fact]
    public async Task DnsRebindingOnRedirectIsRejectedBeforeReconnect()
    {
        int resolution = 0;
        var resolver = new FakeResolver((host, cancellationToken) =>
            ++resolution == 1 ? [PublicAddress] : [IPAddress.Parse("10.0.0.9")]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) => Redirect("/feed"));
        var service = CreateService(resolver, transport);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync("https://feeds.example/start", CancellationToken.None));

        Assert.Equal(AppErrorCode.AccessDenied, error.Error.Code);
        Assert.Equal(2, resolver.CallCount);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task RedirectToPrivateAddressIsRejectedBeforeSecondRequest()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            Redirect("https://10.0.0.5/internal-feed"));
        var service = CreateService(resolver, transport);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync("https://feeds.example/start", CancellationToken.None));

        Assert.Equal(AppErrorCode.AccessDenied, error.Error.Code);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task RedirectLimitIsEnforced()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        int sequence = 0;
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            Redirect($"/redirect-{transportCall(uri)}"));
        string transportCall(Uri _) => (++sequence).ToString(CultureInfo.InvariantCulture);
        var service = CreateService(resolver, transport, TestOptions() with { MaximumRedirects = 2 });

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync("https://feeds.example/start", CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
        Assert.Equal(3, transport.CallCount);
    }

    [Fact]
    public async Task HtmlRelativeAlternateLinkIsFetchedAndVerifiedAsAtom()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            uri.AbsolutePath == "/blog/"
                ? Response(
                    HttpStatusCode.OK,
                    "text/html",
                    "<html><head><link title='Updates' href='../atom.xml?lang=zh' " +
                    "type='application/atom+xml' rel='alternate stylesheet'></head></html>")
                : Response(HttpStatusCode.OK, "application/atom+xml", Atom("Verified Atom")));
        var service = CreateService(resolver, transport);

        FeedDiscoveryResult result = await service.DiscoverAsync(
            "https://site.example/blog/",
            CancellationToken.None);

        DiscoveredFeed feed = Assert.Single(result.Feeds);
        Assert.Equal("https://site.example/atom.xml?lang=zh", feed.FeedUrl);
        Assert.Equal("Updates", feed.Title);
        Assert.Equal(FeedDocumentKind.Atom, feed.Kind);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task BrokenHtmlCandidateDoesNotHideLaterValidFeed()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
        {
            if (uri.AbsolutePath == "/")
            {
                return Response(
                    HttpStatusCode.OK,
                    "text/html",
                    "<link rel='alternate' type='application/rss+xml' href='/offline.xml'>" +
                    "<link rel='alternate' type='application/rss+xml' href='/working.xml'>");
            }
            if (uri.AbsolutePath == "/offline.xml") throw new HttpRequestException("offline candidate");
            return Response(HttpStatusCode.OK, "application/rss+xml", Rss("Working"));
        });
        var service = CreateService(resolver, transport);

        FeedDiscoveryResult result = await service.DiscoverAsync(
            "https://site.example/",
            CancellationToken.None);

        DiscoveredFeed feed = Assert.Single(result.Feeds);
        Assert.Equal("https://site.example/working.xml", feed.FeedUrl);
        Assert.Equal(3, transport.CallCount);
    }

    [Fact]
    public async Task WrongMimeTypeIsRejectedWithoutParsingBody()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            Response(HttpStatusCode.OK, "text/plain", Rss("Should Not Parse")));
        var service = CreateService(resolver, transport);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync("https://feeds.example/rss", CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
    }

    [Fact]
    public async Task DeclaredCompressedSizeLimitIsEnforced()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
        {
            FeedDiscoveryHttpResponse response = Response(HttpStatusCode.OK, "application/rss+xml", Rss("Large"));
            response.Message.Content.Headers.ContentLength = 1025;
            return response;
        });
        var service = CreateService(resolver, transport, TestOptions() with { MaximumCompressedBytes = 1024 });

        await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync("https://feeds.example/rss", CancellationToken.None));

        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task GzipExpansionCannotExceedDecompressedLimit()
    {
        byte[] expanded = Encoding.UTF8.GetBytes(Rss(new string('x', 4096)));
        byte[] compressed = Gzip(expanded);
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
        {
            var message = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(compressed)
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/rss+xml");
            message.Content.Headers.ContentEncoding.Add("gzip");
            return new(message);
        });
        var service = CreateService(resolver, transport, TestOptions() with
        {
            MaximumCompressedBytes = 1024,
            MaximumDecompressedBytes = 1024
        });

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync("https://feeds.example/rss", CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
    }

    [Fact]
    public async Task DtdAndExternalEntityAreProhibited()
    {
        const string malicious = "<!DOCTYPE rss [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]>" +
            "<rss version='2.0'><channel><title>&xxe;</title></channel></rss>";
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            Response(HttpStatusCode.OK, "application/rss+xml", malicious));
        var service = CreateService(resolver, transport);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync("https://feeds.example/rss", CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
        Assert.DoesNotContain("passwd", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedXmlAfterValidTitleIsStillRejected()
    {
        const string malformed = "<rss version='2.0'><channel><title>Looks Valid</title><item></channel></rss>";
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport((uri, addresses, cancellationToken) =>
            Response(HttpStatusCode.OK, "application/rss+xml", malformed));
        var service = CreateService(resolver, transport);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync("https://feeds.example/rss", CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
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
        var service = CreateService(resolver, transport);
        using var cancellation = new CancellationTokenSource();

        Task<FeedDiscoveryResult> discovery = service.DiscoverAsync(
            "https://feeds.example/rss",
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
    }

    [Fact]
    public async Task TotalTimeoutIsMappedToStructuredTimeoutError()
    {
        var resolver = new FakeResolver((host, cancellationToken) => [PublicAddress]);
        var transport = new FakeTransport(async (uri, addresses, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var service = CreateService(resolver, transport, TestOptions() with
        {
            TotalTimeout = TimeSpan.FromMilliseconds(30),
            ConnectTimeout = TimeSpan.FromMilliseconds(10)
        });

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.DiscoverAsync("https://feeds.example/rss", CancellationToken.None));

        Assert.Equal(AppErrorCode.Timeout, error.Error.Code);
    }

    [Fact]
    public async Task ProductionTransportConnectsToPinnedAddressWithoutResolvingUriHost()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token);
            NetworkStream stream = client.GetStream();
            byte[] requestBuffer = new byte[4096];
            int received = 0;
            while (received < requestBuffer.Length)
            {
                int read = await stream.ReadAsync(requestBuffer.AsMemory(received), timeout.Token);
                if (read == 0) break;
                received += read;
                if (Encoding.ASCII.GetString(requestBuffer, 0, received).Contains("\r\n\r\n", StringComparison.Ordinal))
                    break;
            }
            byte[] body = Encoding.UTF8.GetBytes(Rss("Pinned Socket"));
            byte[] headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/rss+xml\r\n" +
                $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
        }, timeout.Token);
        var transport = new PinnedFeedDiscoveryTransport(TestOptions());

        using FeedDiscoveryHttpResponse response = await transport.SendAsync(
            new Uri($"http://must-not-resolve.invalid:{port}/feed"),
            [IPAddress.Loopback],
            timeout.Token);
        string payload = await response.Message.Content.ReadAsStringAsync(timeout.Token);
        await server;
        listener.Stop();

        Assert.Equal(HttpStatusCode.OK, response.Message.StatusCode);
        Assert.Contains("Pinned Socket", payload, StringComparison.Ordinal);
    }

    private static FeedDiscoveryService CreateService(
        IFeedHostResolver resolver,
        IFeedDiscoveryTransport transport,
        FeedDiscoveryOptions? options = null) => new(resolver, transport, options ?? TestOptions());

    private static FeedDiscoveryOptions TestOptions() => FeedDiscoveryOptions.Default with
    {
        TotalTimeout = TimeSpan.FromSeconds(2),
        ConnectTimeout = TimeSpan.FromMilliseconds(250),
        MaximumCompressedBytes = 64 * 1024,
        MaximumDecompressedBytes = 128 * 1024,
        AllowedHttpHosts = HostSet(),
        TrustedPrivateHosts = HostSet()
    };

    private static HashSet<string> HostSet(params string[] hosts) =>
        new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);

    private static FeedDiscoveryHttpResponse UnexpectedNetwork() =>
        throw new InvalidOperationException("Network must not be reached");

    private static FeedDiscoveryHttpResponse Response(
        HttpStatusCode status,
        string mediaType,
        string content)
    {
        var message = new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        };
        return new(message);
    }

    private static FeedDiscoveryHttpResponse Redirect(string location)
    {
        var message = new HttpResponseMessage(HttpStatusCode.Redirect);
        message.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return new(message);
    }

    private static string Rss(string title) =>
        $"<?xml version='1.0'?><rss version='2.0'><channel><title>{title}</title></channel></rss>";

    private static string Atom(string title) =>
        $"<?xml version='1.0'?><feed xmlns='http://www.w3.org/2005/Atom'><title>{title}</title></feed>";

    private static byte[] Gzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(input);
        }
        return output.ToArray();
    }

    private sealed class FakeResolver(
        Func<string, CancellationToken, IReadOnlyList<IPAddress>> resolve) : IFeedHostResolver
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

    private sealed class FakeTransport(
        Func<Uri, IReadOnlyList<IPAddress>, CancellationToken, FeedDiscoveryHttpResponse>? send = null,
        Func<Uri, IReadOnlyList<IPAddress>, CancellationToken, Task<FeedDiscoveryHttpResponse>>? sendAsync = null)
        : IFeedDiscoveryTransport
    {
        public FakeTransport(Func<Uri, IReadOnlyList<IPAddress>, CancellationToken, FeedDiscoveryHttpResponse> send)
            : this(send, null)
        {
        }

        public FakeTransport(Func<Uri, IReadOnlyList<IPAddress>, CancellationToken, Task<FeedDiscoveryHttpResponse>> send)
            : this(null, send)
        {
        }

        public List<(Uri Uri, IReadOnlyList<IPAddress> Addresses)> Calls { get; } = [];
        public int CallCount => Calls.Count;

        public Task<FeedDiscoveryHttpResponse> SendAsync(
            Uri uri,
            IReadOnlyList<IPAddress> addresses,
            CancellationToken cancellationToken)
        {
            Calls.Add((uri, addresses));
            return sendAsync is not null
                ? sendAsync(uri, addresses, cancellationToken)
                : Task.FromResult(send!(uri, addresses, cancellationToken));
        }
    }
}
