using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class WorkerAccountSessionServiceTests
{
    [Fact]
    public async Task LoginKeepsAccessTokenInMemoryAndPersistsOnlyRefreshToken()
    {
        var secrets = new FakeSecretStore();
        var handler = new StubHandler((request, cancellationToken) =>
        {
            Assert.Equal("/v1/auth/login", request.RequestUri?.AbsolutePath);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("access-one", "refresh-one", "ADMIN")));
        });
        var service = CreateService(handler, secrets);

        await service.LoginAsync("owner", "correct horse battery staple", CancellationToken.None);

        Assert.Equal(AccountSessionStatus.SignedIn, service.Current.Status);
        Assert.Equal(AccountRole.Admin, service.Current.User?.Role);
        Assert.Equal(88, service.Current.Quota?.Ai.Remaining);
        Assert.Equal("refresh-one", secrets.Values["account_refresh_token"]);
        Assert.DoesNotContain("access-one", secrets.Values.Values, StringComparer.Ordinal);
    }

    [Fact]
    public async Task InitializeRotatesSavedRefreshTokenAndRestoresMe()
    {
        var secrets = new FakeSecretStore
        {
            Values = { ["account_refresh_token"] = "saved-refresh-token-value-00000001" }
        };
        var handler = new StubHandler((request, cancellationToken) => Task.FromResult(
            request.RequestUri?.AbsolutePath switch
            {
                "/v1/auth/refresh" => JsonResponse(HttpStatusCode.OK, TokenJson("restored-access", "rotated-refresh")),
                "/v1/me" => JsonResponse(HttpStatusCode.OK, MeJson("reader", "USER")),
                _ => throw new InvalidOperationException("Unexpected request")
            }));
        var service = CreateService(handler, secrets);

        await service.InitializeAsync(CancellationToken.None);

        Assert.Equal(AccountSessionStatus.SignedIn, service.Current.Status);
        Assert.Equal("reader", service.Current.User?.Username);
        Assert.Equal("rotated-refresh", secrets.Values["account_refresh_token"]);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ConcurrentUnauthorizedRequestsTriggerOnlyOneRefresh()
    {
        var secrets = new FakeSecretStore();
        int refreshCalls = 0;
        int meCalls = 0;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/v1/auth/login")
                return JsonResponse(HttpStatusCode.OK, LoginJson("expired-access", "refresh-before", "USER"));
            if (path == "/v1/auth/refresh")
            {
                Interlocked.Increment(ref refreshCalls);
                await Task.Delay(40, cancellationToken);
                return JsonResponse(HttpStatusCode.OK, TokenJson("fresh-access", "refresh-after"));
            }
            if (path == "/v1/me")
            {
                Interlocked.Increment(ref meCalls);
                return request.Headers.Authorization?.Parameter == "fresh-access"
                    ? JsonResponse(HttpStatusCode.OK, MeJson("reader", "USER"))
                    : JsonResponse(HttpStatusCode.Unauthorized, ErrorJson("TOKEN_EXPIRED"));
            }
            throw new InvalidOperationException("Unexpected request");
        });
        var service = CreateService(handler, secrets);
        await service.LoginAsync("reader", "password", CancellationToken.None);

        await Task.WhenAll(
            service.RefreshAsync(CancellationToken.None),
            service.RefreshAsync(CancellationToken.None));

        Assert.Equal(1, refreshCalls);
        Assert.InRange(meCalls, 3, 4);
        Assert.Equal("refresh-after", secrets.Values["account_refresh_token"]);
        Assert.Equal(AccountSessionStatus.SignedIn, service.Current.Status);
    }

    [Fact]
    public async Task AuthorizedRequestIsReplayedAtMostOnceAndThenExpiresSession()
    {
        var secrets = new FakeSecretStore();
        int refreshCalls = 0;
        int meCalls = 0;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/v1/auth/login")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("access-old", "refresh-old", "USER")));
            if (path == "/v1/auth/refresh")
            {
                Interlocked.Increment(ref refreshCalls);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, TokenJson("access-new", "refresh-new")));
            }
            if (path == "/v1/me")
            {
                Interlocked.Increment(ref meCalls);
                return Task.FromResult(JsonResponse(HttpStatusCode.Unauthorized, ErrorJson("TOKEN_INVALID")));
            }
            throw new InvalidOperationException("Unexpected request");
        });
        var service = CreateService(handler, secrets);
        await service.LoginAsync("reader", "password", CancellationToken.None);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.RefreshAsync(CancellationToken.None));

        Assert.Equal(1, refreshCalls);
        Assert.Equal(2, meCalls);
        Assert.Equal(AccountSessionStatus.Expired, service.Current.Status);
        Assert.DoesNotContain("access-new", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-new", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("account_refresh_token", secrets.Values.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task InvalidSavedRefreshTokenIsRemovedAndReportedAsExpired()
    {
        const string invalidToken = "invalid-refresh-token-value-000000001";
        var secrets = new FakeSecretStore { Values = { ["account_refresh_token"] = invalidToken } };
        var handler = new StubHandler((request, cancellationToken) => Task.FromResult(
            JsonResponse(HttpStatusCode.Unauthorized, ErrorJson("TOKEN_INVALID", invalidToken))));
        var service = CreateService(handler, secrets);

        await service.InitializeAsync(CancellationToken.None);

        Assert.Equal(AccountSessionStatus.Expired, service.Current.Status);
        Assert.Empty(secrets.Values);
    }

    [Fact]
    public async Task LogoutClearsLocalTokensEvenWhenWorkerIsOffline()
    {
        var secrets = new FakeSecretStore();
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("access", "refresh", "USER")));
            throw new HttpRequestException("offline");
        });
        var service = CreateService(handler, secrets);
        await service.LoginAsync("reader", "password", CancellationToken.None);

        await service.LogoutAsync(CancellationToken.None);

        Assert.Equal(AccountSessionStatus.SignedOut, service.Current.Status);
        Assert.Empty(secrets.Values);
    }

    [Fact]
    public async Task LogoutClearsInMemorySessionWhenEncryptedTokenDeletionFails()
    {
        var secrets = new FakeSecretStore();
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("access", "refresh", "USER")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var service = CreateService(handler, secrets);
        await service.LoginAsync("reader", "password", CancellationToken.None);
        secrets.ThrowOnDelete = true;

        await Assert.ThrowsAsync<IOException>(() => service.LogoutAsync(CancellationToken.None));

        Assert.Equal(AccountSessionStatus.SignedOut, service.Current.Status);
        await Assert.ThrowsAsync<AppException>(() => service.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MissingWorkerAddressFailsWithoutPersistingCredentials()
    {
        var secrets = new FakeSecretStore();
        var handler = new StubHandler((request, cancellationToken) =>
            throw new InvalidOperationException("HTTP should not be called"));
        var service = new WorkerAccountSessionService(
            new StubHttpClientFactory(handler), secrets, new WorkerAccountOptions(null));

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.LoginAsync("reader", "password", CancellationToken.None));

        Assert.Equal(AppErrorCode.InvalidRequest, error.Error.Code);
        Assert.Empty(secrets.Values);
        Assert.Equal(0, handler.RequestCount);
    }

    private static WorkerAccountSessionService CreateService(HttpMessageHandler handler, ISecretStore secrets) =>
        new(new StubHttpClientFactory(handler), secrets, new WorkerAccountOptions(new Uri("https://worker.test")));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string LoginJson(string access, string refresh, string role) => JsonSerializer.Serialize(new
    {
        user = new { id = "10000000-0000-4000-8000-000000000001", username = "owner", role },
        quota = Quota(),
        accessToken = access,
        refreshToken = refresh,
        expiresInSeconds = 900
    });

    private static string MeJson(string username, string role) => JsonSerializer.Serialize(new
    {
        user = new { id = "10000000-0000-4000-8000-000000000001", username, role },
        quota = Quota(),
        serverTime = "2026-07-22T08:00:00Z"
    });

    private static object Quota() => new
    {
        date = "2026-07-22",
        ai = new { limit = 100, used = 12, reserved = 0, remaining = 88 },
        speechSeconds = new { limit = 3600, used = 45, reserved = 0, remaining = 3555 }
    };

    private static string TokenJson(string access, string refresh) => JsonSerializer.Serialize(new
    {
        accessToken = access,
        refreshToken = refresh,
        expiresInSeconds = 900
    });

    private static string ErrorJson(string code, string? injectedSecret = null) => JsonSerializer.Serialize(new
    {
        error = new
        {
            code,
            title = "认证失败",
            userMessage = "登录已失效",
            suggestion = "请重新登录",
            provider = "LenxTool Worker",
            requestId = "request-test",
            retryAfterSeconds = (int?)null,
            isRetryable = false,
            ignored = injectedSecret
        }
    });

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool ThrowOnDelete { get; set; }

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(name));

        public Task SetAsync(string name, string value, CancellationToken cancellationToken)
        {
            Values[name] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            if (ThrowOnDelete) throw new IOException("test delete failure");
            Values.Remove(name);
            return Task.CompletedTask;
        }
    }
}
