using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

/// <summary>
/// 冻结 Eagle Web API V2 的最小官方契约：只探测本机应用与当前资源库，
/// 并以已经验证的 Base64 图片写入，绝不把原始图片 URL 交给 Eagle 下载。
/// </summary>
public sealed class EagleApiClientTests
{
    [Fact]
    public void ProductionHandlerCannotRedirectProxyOrPersistCookies()
    {
        using SocketsHttpHandler handler =
            EagleHttpClientSecurity.CreatePrimaryHandler();

        // 本机假服务即使返回 30x，也不能把 Base64 图片转发到外部地址。
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(2, handler.MaxConnectionsPerServer);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost:41595/")]
    [InlineData("http://0.0.0.0:41595/")]
    [InlineData("https://127.0.0.1:41595/")]
    [InlineData("http://127.0.0.1:41595/api/")]
    public void EndpointRequiresExplicitLoopbackHttpRoot(string value)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => EagleApiClient.ValidateEndpoint(new Uri(value)));

        Assert.Contains(
            "loopback HTTP",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "http://127.0.0.1:41595/",
        "http://127.0.0.1:41595/")]
    [InlineData(
        "http://[::1]:41595/",
        "http://[::1]:41595/")]
    public void EndpointPreservesExplicitNumericLoopback(
        string value,
        string expected)
    {
        Uri normalized = EagleApiClient.ValidateEndpoint(new Uri(value));

        Assert.Equal(expected, normalized.AbsoluteUri);
    }

    [Fact]
    public async Task ProbeRequiresWindowsV2CapabilityAndOpenLibrary()
    {
        var paths = new List<string>();
        var handler = new StubHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/api/v2/app/info" => JsonResponse(new
                {
                    status = "success",
                    data = new
                    {
                        version = "4.0.0",
                        prereleaseVersion = (string?)null,
                        buildVersion = "build21",
                        platform = "win32"
                    }
                }),
                "/api/v2/library/info" => JsonResponse(new
                {
                    status = "success",
                    data = new
                    {
                        name = "Local Library",
                        path = "D:\\Eagle\\Local.library"
                    }
                }),
                _ => new(HttpStatusCode.NotFound)
            };
        });
        var client = new EagleApiClient(new StubClientFactory(handler));

        EagleApiCapability result = await client.ProbeAsync(
            new("http://127.0.0.1:41595/"),
            CancellationToken.None);

        Assert.Equal("4.0.0", result.Version);
        Assert.Equal(21, result.BuildNumber);
        Assert.Matches("^[0-9a-f]{24}$", result.LibraryRevision);
        Assert.DoesNotContain(
            "Local Library",
            result.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "D:\\Eagle\\Local.library",
            result.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["/api/v2/app/info", "/api/v2/library/info"],
            paths);
    }

    [Fact]
    public async Task ProbeLibraryRevisionChangesWhenCurrentLibraryChanges()
    {
        var libraryPaths = new Queue<string>(
        [
            "D:\\Eagle\\Library-A.library",
            "D:\\Eagle\\Library-B.library"
        ]);
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/api/v2/app/info" => JsonResponse(new
                {
                    status = "success",
                    data = new
                    {
                        version = "4.0.0",
                        buildVersion = "build21",
                        platform = "win32"
                    }
                }),
                "/api/v2/library/info" => JsonResponse(new
                {
                    status = "success",
                    data = new
                    {
                        name = "Local Library",
                        path = libraryPaths.Dequeue()
                    }
                }),
                _ => new(HttpStatusCode.NotFound)
            });
        var client = new EagleApiClient(new StubClientFactory(handler));

        EagleApiCapability first = await client.ProbeAsync(
            new("http://127.0.0.1:41595/"),
            CancellationToken.None);
        EagleApiCapability second = await client.ProbeAsync(
            new("http://127.0.0.1:41595/"),
            CancellationToken.None);

        Assert.NotEqual(first.LibraryRevision, second.LibraryRevision);
        Assert.Matches("^[0-9a-f]{24}$", first.LibraryRevision);
        Assert.Matches("^[0-9a-f]{24}$", second.LibraryRevision);
        Assert.DoesNotContain(
            "Library-A",
            first.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Library-B",
            second.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "D:\\Eagle\\Local.library")]
    [InlineData("Local Library", "")]
    public async Task ProbeRejectsLibraryWithoutRequiredMetadata(
        string name,
        string path)
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/api/v2/app/info" => JsonResponse(new
                {
                    status = "success",
                    data = new
                    {
                        version = "4.0.0",
                        buildVersion = "build21",
                        platform = "win32"
                    }
                }),
                "/api/v2/library/info" => JsonResponse(new
                {
                    status = "success",
                    data = new { name, path }
                }),
                _ => new(HttpStatusCode.NotFound)
            });
        var client = new EagleApiClient(new StubClientFactory(handler));

        EagleApiException exception =
            await Assert.ThrowsAsync<EagleApiException>(
                () => client.ProbeAsync(
                    new("http://127.0.0.1:41595/"),
                    CancellationToken.None));

        Assert.Equal(EagleApiFailure.Incompatible, exception.Failure);
    }

    [Fact]
    public async Task ProbeAcceptsVersionLaterThanFourPointZero()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/api/v2/app/info" => JsonResponse(new
                {
                    status = "success",
                    data = new
                    {
                        version = "4.0.1",
                        buildVersion = "build1",
                        platform = "win32"
                    }
                }),
                "/api/v2/library/info" => JsonResponse(new
                {
                    status = "success",
                    data = new
                    {
                        name = "Local Library",
                        path = "D:\\Eagle\\Local.library"
                    }
                }),
                _ => new(HttpStatusCode.NotFound)
            });
        var client = new EagleApiClient(new StubClientFactory(handler));

        EagleApiCapability result = await client.ProbeAsync(
            new("http://127.0.0.1:41595/"),
            CancellationToken.None);

        Assert.Equal("4.0.1", result.Version);
        Assert.Equal(1, result.BuildNumber);
    }

    [Fact]
    public async Task ProbeMapsUnavailableEndpointAsRetryable()
    {
        var client = new EagleApiClient(new StubClientFactory(
            new StubHandler(_ => throw new HttpRequestException(
                "connection refused at private endpoint"))));

        EagleApiException exception =
            await Assert.ThrowsAsync<EagleApiException>(
                () => client.ProbeAsync(
                    new("http://127.0.0.1:41595/"),
                    CancellationToken.None));

        Assert.Equal(EagleApiFailure.Unavailable, exception.Failure);
        Assert.True(exception.IsRetryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ProbeMapsTransientHttpStatusAsRetryable(
        HttpStatusCode statusCode)
    {
        var client = new EagleApiClient(new StubClientFactory(
            new StubHandler(_ => new(statusCode))));

        EagleApiException exception =
            await Assert.ThrowsAsync<EagleApiException>(
                () => client.ProbeAsync(
                    new("http://127.0.0.1:41595/"),
                    CancellationToken.None));

        Assert.Equal(EagleApiFailure.Unavailable, exception.Failure);
        Assert.True(exception.IsRetryable);
    }

    [Theory]
    [InlineData("3.9.9", "build99", "win32")]
    [InlineData("4.0.0", "build20", "win32")]
    [InlineData("4.0.0", "build21", "darwin")]
    public async Task ProbeRejectsUnsupportedProcessWithoutLeakingBody(
        string version,
        string build,
        string platform)
    {
        var handler = new StubHandler(_ => JsonResponse(new
        {
            status = "success",
            data = new
            {
                version,
                buildVersion = build,
                platform,
                secretDetail = "provider-private-detail"
            }
        }));
        var client = new EagleApiClient(new StubClientFactory(handler));

        EagleApiException exception = await Assert.ThrowsAsync<EagleApiException>(
            () => client.ProbeAsync(
                new("http://127.0.0.1:41595/"),
                CancellationToken.None));

        Assert.Equal(EagleApiFailure.Incompatible, exception.Failure);
        Assert.DoesNotContain(
            "provider-private-detail",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddSendsVerifiedBase64MetadataWithoutCredentialsOrRemoteImageUrl()
    {
        string? postedJson = null;
        var handler = new StubHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(new
                {
                    status = "success",
                    data = new
                    {
                        data = Array.Empty<object>(),
                        total = 0,
                        offset = 0,
                        limit = 1
                    }
                });
            }
            postedJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new
            {
                status = "success",
                data = new { id = "LT0123456789ABCDEF0123456789ABCD" }
            });
        });
        var client = new EagleApiClient(new StubClientFactory(handler));
        var item = new EagleAddItem(
            "LT0123456789ABCDEF0123456789ABCD",
            "data:image/png;base64,iVBORw0KGgo=",
            "中文图片",
            "https://news.example.com/item/1",
            ["资讯", "设计"]);

        string itemId = await client.AddAsync(
            new("http://127.0.0.1:41595/"),
            item,
            CancellationToken.None);

        Assert.Equal(item.ItemId, itemId);
        Assert.NotNull(postedJson);
        using JsonDocument document = JsonDocument.Parse(postedJson);
        JsonElement root = document.RootElement;
        Assert.Equal(item.DataUri, root.GetProperty("base64").GetString());
        Assert.Equal(item.Website, root.GetProperty("website").GetString());
        Assert.False(root.TryGetProperty("url", out _));
        Assert.False(root.TryGetProperty("headers", out _));
        Assert.False(root.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task AddTreatsExistingStableItemIdAsSuccessfulReplay()
    {
        int postCount = 0;
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postCount++;
            }
            return JsonResponse(new
            {
                status = "success",
                data = new
                {
                    data = new[]
                    {
                        new { id = "LT0123456789ABCDEF0123456789ABCD" }
                    },
                    total = 1,
                    offset = 0,
                    limit = 1
                }
            });
        });
        var client = new EagleApiClient(new StubClientFactory(handler));
        var item = new EagleAddItem(
            "LT0123456789ABCDEF0123456789ABCD",
            "data:image/png;base64,iVBORw0KGgo=",
            "image",
            null,
            []);

        string itemId = await client.AddAsync(
            new("http://127.0.0.1:41595/"),
            item,
            CancellationToken.None);

        Assert.Equal(item.ItemId, itemId);
        Assert.Equal(0, postCount);
    }

    [Fact]
    public async Task AddReconcilesUncertainPostWithStableItemId()
    {
        const string ItemId = "LT0123456789ABCDEF0123456789ABCD";
        int getCount = 0;
        int postCount = 0;
        bool itemWasCreated = false;
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                getCount++;
                return JsonResponse(new
                {
                    status = "success",
                    data = new
                    {
                        data = itemWasCreated
                            ? new[] { new { id = ItemId } }
                            : Array.Empty<object>(),
                        total = itemWasCreated ? 1 : 0,
                        offset = 0,
                        limit = 1
                    }
                });
            }

            postCount++;
            itemWasCreated = true;
            return new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "duplicate or uncertain provider response",
                    Encoding.UTF8,
                    "text/plain")
            };
        });
        var client = new EagleApiClient(new StubClientFactory(handler));

        string result = await client.AddAsync(
            new("http://127.0.0.1:41595/"),
            new(
                ItemId,
                "data:image/png;base64,iVBORw0KGgo=",
                "image",
                null,
                []),
            CancellationToken.None);

        Assert.Equal(ItemId, result);
        Assert.Equal(2, getCount);
        Assert.Equal(1, postCount);
    }

    [Fact]
    public async Task AddMapsUnknownPostReconciliationAsRetryable()
    {
        int requestCount = 0;
        var handler = new StubHandler(_ =>
        {
            requestCount++;
            return requestCount switch
            {
                1 => JsonResponse(new
                {
                    status = "success",
                    data = new
                    {
                        data = Array.Empty<object>(),
                        total = 0,
                        offset = 0,
                        limit = 1
                    }
                }),
                2 => new(HttpStatusCode.BadRequest),
                _ => new(HttpStatusCode.ServiceUnavailable)
            };
        });
        var client = new EagleApiClient(new StubClientFactory(handler));

        EagleApiException exception =
            await Assert.ThrowsAsync<EagleApiException>(
                () => client.AddAsync(
                    new("http://127.0.0.1:41595/"),
                    new(
                        "LT0123456789ABCDEF0123456789ABCD",
                        "data:image/png;base64,iVBORw0KGgo=",
                        "image",
                        null,
                        []),
                    CancellationToken.None));

        Assert.Equal(3, requestCount);
        Assert.Equal(EagleApiFailure.Unavailable, exception.Failure);
        Assert.True(exception.IsRetryable);
    }

    [Fact]
    public async Task ProviderErrorIsClosedAndDoesNotExposeResponseText()
    {
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Get
                ? JsonResponse(new
                {
                    status = "success",
                    data = new
                    {
                        data = Array.Empty<object>(),
                        total = 0,
                        offset = 0,
                        limit = 1
                    }
                })
                : new(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        "provider-secret-response",
                        Encoding.UTF8,
                        "text/plain")
                });
        var client = new EagleApiClient(new StubClientFactory(handler));

        EagleApiException exception = await Assert.ThrowsAsync<EagleApiException>(
            () => client.AddAsync(
                new("http://127.0.0.1:41595/"),
                new(
                    "LT0123456789ABCDEF0123456789ABCD",
                    "data:image/png;base64,iVBORw0KGgo=",
                    "image",
                    null,
                    []),
                CancellationToken.None));

        Assert.Equal(EagleApiFailure.Rejected, exception.Failure);
        Assert.DoesNotContain(
            "provider-secret-response",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task JSendErrorMessageIsNeverExposed()
    {
        const string sensitiveMessage =
            "private-library-name-from-provider";
        var client = new EagleApiClient(new StubClientFactory(
            new StubHandler(_ => JsonResponse(new
            {
                status = "error",
                message = sensitiveMessage
            }))));

        EagleApiException exception =
            await Assert.ThrowsAsync<EagleApiException>(
                () => client.ProbeAsync(
                    new("http://127.0.0.1:41595/"),
                    CancellationToken.None));

        Assert.Equal(EagleApiFailure.Rejected, exception.Failure);
        Assert.DoesNotContain(
            sensitiveMessage,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedJsonResponseFailsClosedAtTheStreamingBoundary()
    {
        string oversized = new('A', 256 * 1024 + 1);
        var client = new EagleApiClient(new StubClientFactory(
            new StubHandler(_ => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    oversized,
                    Encoding.UTF8,
                    "application/json")
            })));

        EagleApiException exception =
            await Assert.ThrowsAsync<EagleApiException>(
                () => client.ProbeAsync(
                    new("http://127.0.0.1:41595/"),
                    CancellationToken.None));

        Assert.Equal(EagleApiFailure.Incompatible, exception.Failure);
        Assert.DoesNotContain(
            oversized[..128],
            exception.ToString(),
            StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class StubClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
    }
}
