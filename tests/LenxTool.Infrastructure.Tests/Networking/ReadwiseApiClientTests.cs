using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Tests.Networking;

/// <summary>
/// 冻结 Reader Save API 的最小安全合同：固定官方主机、Token 仅进入认证头、
/// 重复 URL 以 200 成功收敛，且不伪造官方未承诺的幂等字段。
/// </summary>
public sealed class ReadwiseApiClientTests
{
    private static readonly IPAddress PublicAddress =
        IPAddress.Parse("104.20.20.31");
    private const string AccessToken = "readwise-token-23456789";

    [Fact]
    public void ProductionHandlerDisablesRedirectProxyCookieAndDecompression()
    {
        using SocketsHttpHandler handler =
            ReadwiseHttpClientSecurity.CreatePrimaryHandler([PublicAddress]);

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(1, handler.MaxConnectionsPerServer);
    }

    [Fact]
    public async Task ProbeUsesFixedAuthEndpointAndTokenHeader()
    {
        var requests = new List<RequestSnapshot>();
        var factory = new StubClientFactory(async (request, _) =>
        {
            requests.Add(await RequestSnapshot.CreateAsync(request));
            return new(HttpStatusCode.NoContent);
        });
        var resolver = new RecordingResolver([PublicAddress]);
        var client = CreateClient(factory, resolver);

        await client.ProbeAsync(AccessToken, CancellationToken.None);

        RequestSnapshot request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://readwise.io/api/v2/auth/",
            request.Uri.AbsoluteUri);
        Assert.Equal(
            $"Token {AccessToken}",
            Assert.Single(request.Header("Authorization")));
        Assert.Equal(["readwise.io"], resolver.Hosts);
        Assert.Equal([PublicAddress], factory.LastPinnedAddresses);
        Assert.Equal([ReadwiseApiClient.ApiRoot], factory.CreatedEndpoints);
    }

    [Fact]
    public async Task PinnedProbeReusesHealthAddressesWithoutResolvingAgain()
    {
        var resolver = new RecordingResolver(
            _ => throw new InvalidOperationException("DNS must not run"));
        var factory = new StubClientFactory(
            (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.NoContent)));
        var client = CreateClient(factory, resolver);

        await client.ProbePinnedAsync(
            AccessToken,
            [PublicAddress],
            CancellationToken.None);

        Assert.Empty(resolver.Hosts);
        Assert.Equal([PublicAddress], factory.LastPinnedAddresses);
    }

    [Fact]
    public async Task SavePostsOnlyDocumentedFieldsAndAcceptsCreatedResponse()
    {
        var requests = new List<RequestSnapshot>();
        var factory = new StubClientFactory(async (request, _) =>
        {
            requests.Add(await RequestSnapshot.CreateAsync(request));
            return JsonResponse(
                HttpStatusCode.Created,
                new
                {
                    id = "0000ffff2222eeee3333dddd4444",
                    url = "https://read.readwise.io/new/read/0000ffff2222eeee3333dddd4444"
                });
        });
        var client = CreateClient(factory);

        ReadwiseSaveResult result = await client.SaveAsync(
            AccessToken,
            Document(),
            CancellationToken.None);

        Assert.Equal("0000ffff2222eeee3333dddd4444", result.Id);
        Assert.Equal(
            "https://read.readwise.io/new/read/0000ffff2222eeee3333dddd4444",
            result.Url.AbsoluteUri);
        Assert.False(result.AlreadyExisted);
        RequestSnapshot request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://readwise.io/api/v3/save/",
            request.Uri.AbsoluteUri);
        Assert.Equal(
            $"Token {AccessToken}",
            Assert.Single(request.Header("Authorization")));
        Assert.Empty(request.Header("Idempotency-Key"));
        using JsonDocument json = JsonDocument.Parse(request.Body!);
        JsonElement root = json.RootElement;
        Assert.Equal(
            "https://news.example.com/articles/1",
            root.GetProperty("url").GetString());
        Assert.Equal("A safe article", root.GetProperty("title").GetString());
        Assert.Equal("Ada", root.GetProperty("author").GetString());
        Assert.Equal("Short summary", root.GetProperty("summary").GetString());
        Assert.Equal("2026-08-03T00:00:00Z", root.GetProperty("published_date").GetString());
        Assert.Equal("article", root.GetProperty("category").GetString());
        Assert.Equal("new", root.GetProperty("location").GetString());
        Assert.Equal("lenxtool", root.GetProperty("saved_using").GetString());
        Assert.Equal(
            ["rss", "research"],
            root.GetProperty("tags")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
        Assert.False(root.TryGetProperty("html", out _));
        Assert.False(root.TryGetProperty("should_clean_html", out _));
        Assert.False(root.TryGetProperty("duplicate_url", out _));
    }

    [Fact]
    public async Task SaveTreatsOkAsExistingDocumentSuccess()
    {
        var factory = new StubClientFactory(
            (_, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                new
                {
                    id = "0000ffff2222eeee3333dddd4444",
                    url = "https://read.readwise.io/new/read/0000ffff2222eeee3333dddd4444"
                })));
        var client = CreateClient(factory);

        ReadwiseSaveResult result = await client.SaveAsync(
            AccessToken,
            Document(),
            CancellationToken.None);

        Assert.True(result.AlreadyExisted);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ReadwiseApiFailure.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, ReadwiseApiFailure.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadRequest, ReadwiseApiFailure.Rejected, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ReadwiseApiFailure.Rejected, false)]
    [InlineData(HttpStatusCode.RequestTimeout, ReadwiseApiFailure.Unavailable, true)]
    [InlineData(HttpStatusCode.InternalServerError, ReadwiseApiFailure.Unavailable, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ReadwiseApiFailure.Unavailable, true)]
    public async Task SaveMapsDocumentedAndRetryableStatusesWithoutResponseBody(
        HttpStatusCode status,
        ReadwiseApiFailure expectedFailure,
        bool expectedRetryable)
    {
        var factory = new StubClientFactory((_, _) => Task.FromResult(
            new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    "provider-private-body",
                    Encoding.UTF8,
                    "text/plain")
            }));
        var client = CreateClient(factory);

        ReadwiseApiException exception =
            await Assert.ThrowsAsync<ReadwiseApiException>(
                () => client.SaveAsync(
                    AccessToken,
                    Document(),
                    CancellationToken.None));

        Assert.Equal(expectedFailure, exception.Failure);
        Assert.Equal(expectedRetryable, exception.IsRetryable);
        Assert.DoesNotContain(
            "provider-private-body",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            AccessToken,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimitReturnsRemainingPauseWithoutBlockingNextTask()
    {
        int requestCount = 0;
        var factory = new StubClientFactory((_, _) =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                var limited = new HttpResponseMessage(
                    HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter =
                    new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return Task.FromResult(limited);
            }
            return Task.FromResult(JsonResponse(
                HttpStatusCode.Created,
                new
                {
                    id = "0000ffff2222eeee3333dddd4444",
                    url = "https://read.readwise.io/new/read/0000ffff2222eeee3333dddd4444"
                }));
        });
        var clock = new RecordingClock();
        var client = CreateClient(factory, clock: clock);

        ReadwiseApiException exception =
            await Assert.ThrowsAsync<ReadwiseApiException>(
                () => client.SaveAsync(
                    AccessToken,
                    Document(),
                    CancellationToken.None));
        ReadwiseApiException paused =
            await Assert.ThrowsAsync<ReadwiseApiException>(
                () => client.SaveAsync(
                    AccessToken,
                    Document(),
                    CancellationToken.None));

        Assert.Equal(ReadwiseApiFailure.RateLimited, exception.Failure);
        Assert.True(exception.IsRetryable);
        Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
        Assert.Equal(ReadwiseApiFailure.RateLimited, paused.Failure);
        Assert.True(paused.IsRetryable);
        Assert.Equal(TimeSpan.FromSeconds(30), paused.RetryAfter);
        Assert.Equal(1, requestCount);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task ClientActivelySpacesRequestsAtFiftyPerMinute()
    {
        var factory = new StubClientFactory((_, _) => Task.FromResult(
            JsonResponse(
                HttpStatusCode.Created,
                new
                {
                    id = "0000ffff2222eeee3333dddd4444",
                    url = "https://read.readwise.io/new/read/0000ffff2222eeee3333dddd4444"
                })));
        var clock = new RecordingClock();
        var client = CreateClient(factory, clock: clock);

        await client.SaveAsync(
            AccessToken,
            Document(),
            CancellationToken.None);
        await client.SaveAsync(
            AccessToken,
            Document(),
            CancellationToken.None);

        // 串行客户端以 60/50 秒作为最小起始间隔，避免一分钟内主动发出第 51 个请求。
        Assert.Equal([TimeSpan.FromSeconds(1.2)], clock.Delays);
    }

    [Theory]
    [InlineData("http://read.readwise.io/new/read/abc")]
    [InlineData("https://evil.example.com/new/read/abc")]
    [InlineData("https://user@read.readwise.io/new/read/abc")]
    [InlineData("https://read.readwise.io/new/read/abc?token=private")]
    [InlineData("https://read.readwise.io/new/read/abc#fragment")]
    public async Task SaveRejectsUntrustedReaderResultUrl(string responseUrl)
    {
        var factory = new StubClientFactory((_, _) => Task.FromResult(
            JsonResponse(
                HttpStatusCode.Created,
                new
                {
                    id = "0000ffff2222eeee3333dddd4444",
                    url = responseUrl
                })));
        var client = CreateClient(factory);

        ReadwiseApiException exception =
            await Assert.ThrowsAsync<ReadwiseApiException>(
                () => client.SaveAsync(
                    AccessToken,
                    Document(),
                    CancellationToken.None));

        Assert.Equal(
            ReadwiseApiFailure.UnknownWriteOutcome,
            exception.Failure);
        Assert.True(exception.IsRetryable);
        Assert.DoesNotContain(responseUrl, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task UnexpectedSuccessStatusIsAnUnknownWriteOutcome(
        HttpStatusCode status)
    {
        var factory = new StubClientFactory(
            (_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var client = CreateClient(factory);

        ReadwiseApiException exception =
            await Assert.ThrowsAsync<ReadwiseApiException>(
                () => client.SaveAsync(
                    AccessToken,
                    Document(),
                    CancellationToken.None));

        Assert.Equal(
            ReadwiseApiFailure.UnknownWriteOutcome,
            exception.Failure);
        Assert.True(exception.IsRetryable);
    }

    [Fact]
    public async Task MalformedOrOversizedSuccessBodyDoesNotEscapeTheBoundary()
    {
        string privateBody = new('x', 64 * 1024 + 1);
        var factory = new StubClientFactory((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    privateBody,
                    Encoding.UTF8,
                    "application/json")
            }));
        var client = CreateClient(factory);

        ReadwiseApiException exception =
            await Assert.ThrowsAsync<ReadwiseApiException>(
                () => client.SaveAsync(
                    AccessToken,
                    Document(),
                    CancellationToken.None));

        Assert.Equal(
            ReadwiseApiFailure.UnknownWriteOutcome,
            exception.Failure);
        Assert.DoesNotContain(privateBody, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessBodyReadSharesTheBoundedRequestTimeout()
    {
        var factory = new StubClientFactory((_, _) =>
        {
            var content = new StreamContent(new BlockingReadStream());
            content.Headers.ContentType =
                new MediaTypeHeaderValue("application/json");
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = content
                });
        });
        var client = CreateClient(
            factory,
            requestTimeout: TimeSpan.FromMilliseconds(100));

        ReadwiseApiException exception =
            await Assert.ThrowsAsync<ReadwiseApiException>(
                () => client.SaveAsync(
                    AccessToken,
                    Document(),
                    CancellationToken.None));

        Assert.Equal(
            ReadwiseApiFailure.UnknownWriteOutcome,
            exception.Failure);
        Assert.True(exception.IsRetryable);
    }

    [Fact]
    public async Task CallerCancellationDuringSuccessBodyReadRemainsCancelled()
    {
        var factory = new StubClientFactory((_, _) =>
        {
            var content = new StreamContent(new BlockingReadStream());
            content.Headers.ContentType =
                new MediaTypeHeaderValue("application/json");
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = content
                });
        });
        var client = CreateClient(
            factory,
            requestTimeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        ReadwiseApiException exception =
            await Assert.ThrowsAsync<ReadwiseApiException>(
                () => client.SaveAsync(
                    AccessToken,
                    Document(),
                    cancellation.Token));

        Assert.Equal(ReadwiseApiFailure.Cancelled, exception.Failure);
        Assert.False(exception.IsRetryable);
    }

    [Fact]
    public async Task PrivateDnsAnswerIsBlockedBeforeClientCreation()
    {
        var factory = new StubClientFactory(
            (_, _) => throw new InvalidOperationException("must not send"));
        var resolver = new RecordingResolver(
            [PublicAddress, IPAddress.Loopback]);
        var client = CreateClient(factory, resolver);

        ReadwiseApiException exception =
            await Assert.ThrowsAsync<ReadwiseApiException>(
                () => client.ProbeAsync(
                    AccessToken,
                    CancellationToken.None));

        Assert.Equal(ReadwiseApiFailure.BlockedEndpoint, exception.Failure);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task NetworkFailureDistinguishesProbeFromUnknownWrite()
    {
        var factory = new StubClientFactory(
            (_, _) => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("private-transport-detail")));
        var probeClient = CreateClient(factory);
        var saveClient = CreateClient(factory);

        ReadwiseApiException probe =
            await Assert.ThrowsAsync<ReadwiseApiException>(
                () => probeClient.ProbeAsync(
                    AccessToken,
                    CancellationToken.None));
        ReadwiseApiException save =
            await Assert.ThrowsAsync<ReadwiseApiException>(
                () => saveClient.SaveAsync(
                    AccessToken,
                    Document(),
                    CancellationToken.None));

        Assert.Equal(ReadwiseApiFailure.Unavailable, probe.Failure);
        Assert.Equal(ReadwiseApiFailure.UnknownWriteOutcome, save.Failure);
        Assert.True(probe.IsRetryable);
        Assert.True(save.IsRetryable);
        Assert.DoesNotContain("private-transport-detail", save.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost/article")]
    [InlineData("https://127.0.0.1/article")]
    [InlineData("https://user@news.example.com/article")]
    [InlineData("https://news.example.com:444/article")]
    [InlineData("https://news.example.com/article#private")]
    public async Task UnsafeDocumentUrlFailsBeforeNetwork(string value)
    {
        var factory = new StubClientFactory(
            (_, _) => throw new InvalidOperationException("must not send"));
        var client = CreateClient(factory);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SaveAsync(
                AccessToken,
                Document() with { Url = value },
                CancellationToken.None));

        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task HealthProbeUsesSharedPinnedContextAndNeverCreatesDocument()
    {
        var client = new RecordingApiClient();
        var probe = new ReadwiseEntryIntegrationHealthProbe(client);
        var context = new EntryIntegrationProbeContext(
            ReadwiseApiClient.ApiRoot,
            [PublicAddress]);

        EntryIntegrationProbeResult result = await probe.ProbeAsync(
            context,
            AccessToken,
            CancellationToken.None);

        Assert.Equal(EntryIntegrationKind.Readwise, probe.Kind);
        Assert.Equal(EntryIntegrationHealthStatus.Healthy, result.Status);
        Assert.Equal([PublicAddress], client.ProbedAddresses);
        Assert.Equal(0, client.SaveCount);
    }

    [Fact]
    public async Task HealthProbeRejectsAnyEndpointOtherThanFixedOfficialRoot()
    {
        var client = new RecordingApiClient();
        var probe = new ReadwiseEntryIntegrationHealthProbe(client);

        EntryIntegrationProbeResult result = await probe.ProbeAsync(
            new(
                new Uri("https://readwise.io/api/"),
                [PublicAddress]),
            AccessToken,
            CancellationToken.None);

        Assert.Equal(
            EntryIntegrationHealthStatus.BlockedEndpoint,
            result.Status);
        Assert.Empty(client.ProbedAddresses);
    }

    [Fact]
    public void DependencyInjectionRegistersClientAndHealthProbeAsSingletons()
    {
        var services = new ServiceCollection();

        services.AddReadwiseExportInfrastructure();

        ServiceDescriptor client = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IReadwiseApiClient));
        ServiceDescriptor probe = Assert.Single(
            services,
            descriptor => descriptor.ServiceType
                == typeof(IEntryIntegrationHealthProbe));
        Assert.Equal(ServiceLifetime.Singleton, client.Lifetime);
        Assert.Equal(typeof(ReadwiseApiClient), client.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, probe.Lifetime);
        Assert.Equal(
            typeof(ReadwiseEntryIntegrationHealthProbe),
            probe.ImplementationType);
    }

    private static ReadwiseApiClient CreateClient(
        StubClientFactory factory,
        RecordingResolver? resolver = null,
        RecordingClock? clock = null,
        TimeSpan? requestTimeout = null) =>
        new(
            resolver ?? new RecordingResolver([PublicAddress]),
            factory,
            clock ?? new RecordingClock(),
            requestTimeout);

    private static ReadwiseDocument Document() => new(
        "https://news.example.com/articles/1",
        "A safe article",
        "Ada",
        "Short summary",
        "2026-08-03T00:00:00Z",
        ImageUrl: null,
        Tags: ["rss", "research"],
        Notes: null);

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        object body) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Headers,
        string? Body)
    {
        public IReadOnlyList<string> Header(string name) =>
            Headers.TryGetValue(name, out IReadOnlyList<string>? values)
                ? values
                : [];

        public static async Task<RequestSnapshot> CreateAsync(
            HttpRequestMessage request)
        {
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> contentHeaders =
                request.Content is null ? [] : request.Content.Headers;
            var headers = request.Headers
                .Concat(contentHeaders)
                .ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync();
            return new(request.Method, request.RequestUri!, headers, body);
        }
    }

    private sealed class StubClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : IReadwiseHttpClientFactory
    {
        public int CreateCount { get; private set; }
        public IReadOnlyList<IPAddress> LastPinnedAddresses { get; private set; } = [];
        public List<Uri> CreatedEndpoints { get; } = [];

        public HttpClient Create(
            Uri endpoint,
            IReadOnlyList<IPAddress> pinnedAddresses)
        {
            CreateCount++;
            CreatedEndpoints.Add(endpoint);
            LastPinnedAddresses = pinnedAddresses.ToArray();
            return new(new StubHandler(send), disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    /// <summary>
    /// 模拟已经返回成功响应头、但正文永不产生字节的第三方连接。
    /// </summary>
    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingResolver : IFeedHostResolver
    {
        private readonly Func<string, IReadOnlyList<IPAddress>> _resolve;

        public RecordingResolver(IReadOnlyList<IPAddress> addresses)
            : this(_ => addresses)
        {
        }

        public RecordingResolver(Func<string, IReadOnlyList<IPAddress>> resolve) =>
            _resolve = resolve;

        public List<string> Hosts { get; } = [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken)
        {
            Hosts.Add(host);
            return Task.FromResult(_resolve(host));
        }
    }

    private sealed class RecordingClock : IReadwiseClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingApiClient : IReadwiseApiClient
    {
        public IReadOnlyList<IPAddress> ProbedAddresses { get; private set; } = [];
        public int SaveCount { get; private set; }

        public Task ProbeAsync(
            string accessToken,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "健康探针必须复用共享服务固定的地址。");

        public Task ProbePinnedAsync(
            string accessToken,
            IReadOnlyList<IPAddress> pinnedAddresses,
            CancellationToken cancellationToken)
        {
            ProbedAddresses = pinnedAddresses.ToArray();
            return Task.CompletedTask;
        }

        public Task<ReadwiseSaveResult> SaveAsync(
            string accessToken,
            ReadwiseDocument document,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            throw new InvalidOperationException(
                "健康探针不得创建 Reader 文档。");
        }
    }
}
