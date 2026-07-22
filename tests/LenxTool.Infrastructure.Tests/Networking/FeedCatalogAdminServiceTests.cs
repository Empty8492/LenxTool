using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedCatalogAdminServiceTests
{
    [Fact]
    public async Task BatchSerializesCategoryReferenceAndReturnsOrderedResults()
    {
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return JsonResponse(HttpStatusCode.OK, LoginJson("ADMIN"));

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/admin/feed-catalog-batches", request.RequestUri?.AbsolutePath);
            Assert.Equal("\"catalog-all-41\"", request.Headers.GetValues("If-Match").Single());
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            JsonElement operations = body.RootElement.GetProperty("operations");
            Assert.Equal(2, operations.GetArrayLength());
            Assert.Equal("CREATE_CATEGORY", operations[0].GetProperty("type").GetString());
            Assert.Equal("技术", operations[0].GetProperty("input").GetProperty("name").GetString());
            Assert.Equal("CREATE_FEED", operations[1].GetProperty("type").GetString());
            Assert.Equal(
                "category-1",
                operations[1].GetProperty("input").GetProperty("categoryRef").GetProperty("operationId").GetString());
            Assert.False(operations[1].GetProperty("input").TryGetProperty("categoryId", out _));
            return JsonResponse(HttpStatusCode.OK, """
                {"catalogVersion":42,"results":[
                  {"operationId":"category-1","resourceType":"FEED_CATEGORY","resourceId":"10000000-0000-4000-8000-000000000002"},
                  {"operationId":"feed-1","resourceType":"FEED","resourceId":"10000000-0000-4000-8000-000000000003"}
                ]}
                """);
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("owner", "password", CancellationToken.None);
        var service = new FeedCatalogAdminService(account);

        FeedCatalogBatchResult result = await service.ApplyAsync(
            [
                new("category-1", FeedCatalogBatchOperationType.CreateCategory,
                    CategoryInput: new("技术", 100, true)),
                new("feed-1", FeedCatalogBatchOperationType.CreateFeed,
                    FeedInput: new(
                        "https://feeds.example/rss.xml",
                        "示例源",
                        "https://feeds.example/",
                        null,
                        FeedViewKind.Article,
                        60,
                        100,
                        true),
                    CategoryOperationId: "category-1")
            ],
            41,
            CancellationToken.None);

        Assert.Equal(42, result.CatalogVersion);
        Assert.Equal(["category-1", "feed-1"], result.Results.Select(item => item.OperationId));
    }

    [Fact]
    public async Task BatchRejectsDuplicateOperationIdsBeforeSending()
    {
        int adminCalls = 0;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("ADMIN")));
            adminCalls++;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("owner", "password", CancellationToken.None);
        var service = new FeedCatalogAdminService(account);
        FeedCatalogBatchOperation[] operations =
        [
            new("same", FeedCatalogBatchOperationType.CreateCategory, CategoryInput: new("技术", 0, true)),
            new("same", FeedCatalogBatchOperationType.CreateCategory, CategoryInput: new("产品", 1, true))
        ];

        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyAsync(operations, 1, CancellationToken.None));

        Assert.Equal(0, adminCalls);
    }

    [Fact]
    public async Task CreateCategorySendsVersionAndIdempotencyHeadersAndReturnsNewVersion()
    {
        string? idempotencyKey = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return JsonResponse(HttpStatusCode.OK, LoginJson("ADMIN"));

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/admin/feed-categories", request.RequestUri?.AbsolutePath);
            Assert.Equal("\"catalog-all-7\"", request.Headers.GetValues("If-Match").Single());
            idempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Assert.Matches("^[A-Za-z0-9._:-]{16,128}$", idempotencyKey);
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("技术", body.RootElement.GetProperty("name").GetString());
            Assert.Equal(100, body.RootElement.GetProperty("sortOrder").GetInt32());
            Assert.True(body.RootElement.GetProperty("isEnabled").GetBoolean());
            return JsonResponse(HttpStatusCode.Created, "{\"catalogVersion\":8,\"category\":{}}");
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("owner", "password", CancellationToken.None);
        var service = new FeedCatalogAdminService(account);

        long version = await service.CreateCategoryAsync(
            new("技术", 100, true),
            7,
            CancellationToken.None);

        Assert.Equal(8, version);
        Assert.NotNull(idempotencyKey);
    }

    [Fact]
    public async Task UnauthorizedReplayKeepsSameIdempotencyKeyAfterSingleTokenRefresh()
    {
        var mutationKeys = new List<string>();
        int mutationCalls = 0;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/v1/auth/login")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("ADMIN", "expired", "refresh-old")));
            if (path == "/v1/auth/refresh")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, TokenJson("fresh", "refresh-new")));
            if (path == "/v1/admin/feeds")
            {
                mutationCalls++;
                mutationKeys.Add(request.Headers.GetValues("Idempotency-Key").Single());
                return Task.FromResult(request.Headers.Authorization?.Parameter == "fresh"
                    ? JsonResponse(HttpStatusCode.Created, "{\"catalogVersion\":12,\"feed\":{}}")
                    : JsonResponse(HttpStatusCode.Unauthorized, ErrorJson("TOKEN_EXPIRED")));
            }
            throw new InvalidOperationException($"Unexpected request: {path}");
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("owner", "password", CancellationToken.None);
        var service = new FeedCatalogAdminService(account);

        long version = await service.CreateFeedAsync(
            new(
                "https://feeds.example/rss.xml",
                "Example",
                "https://feeds.example/",
                null,
                FeedViewKind.Article,
                60,
                100,
                true),
            11,
            CancellationToken.None);

        Assert.Equal(12, version);
        Assert.Equal(2, mutationCalls);
        Assert.Equal(2, mutationKeys.Count);
        Assert.Equal(mutationKeys[0], mutationKeys[1]);
    }

    [Fact]
    public async Task VersionConflictIsMappedAndNeverAutomaticallyOverwritesNewerCatalog()
    {
        int mutationCalls = 0;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("ADMIN")));
            mutationCalls++;
            return Task.FromResult(JsonResponse(
                HttpStatusCode.Conflict,
                ErrorJson("CATALOG_VERSION_CONFLICT")));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("owner", "password", CancellationToken.None);
        var service = new FeedCatalogAdminService(account);

        AppException error = await Assert.ThrowsAsync<AppException>(() => service.UpdateCategoryAsync(
            "10000000-0000-4000-8000-000000000002",
            new("工程", 200, false),
            4,
            CancellationToken.None));

        Assert.Equal(AppErrorCode.Conflict, error.Error.Code);
        Assert.Contains("CATALOG_VERSION_CONFLICT", error.Error.TechnicalDetails, StringComparison.Ordinal);
        Assert.Equal(1, mutationCalls);
    }

    [Fact]
    public async Task UserConstructedAdminCallStillReachesServerAndIsRejectedThere()
    {
        int adminCalls = 0;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("USER")));
            adminCalls++;
            return Task.FromResult(JsonResponse(HttpStatusCode.Forbidden, ErrorJson("ADMIN_REQUIRED")));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        var service = new FeedCatalogAdminService(account);

        AppException error = await Assert.ThrowsAsync<AppException>(() => service.DeleteFeedAsync(
            "10000000-0000-4000-8000-000000000003",
            9,
            CancellationToken.None));

        Assert.Equal(AppErrorCode.AccessDenied, error.Error.Code);
        Assert.Equal(1, adminCalls);
    }

    private static WorkerAccountSessionService CreateAccount(HttpMessageHandler handler) => new(
        new StubHttpClientFactory(handler),
        new FakeSecretStore(),
        new WorkerAccountOptions(new Uri("https://worker.test")));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string LoginJson(
        string role,
        string accessToken = "access-token",
        string refreshToken = "refresh-token") => JsonSerializer.Serialize(new
    {
        user = new { id = "10000000-0000-4000-8000-000000000001", username = "owner", role },
        quota = new
        {
            date = "2026-07-23",
            ai = new { limit = 100, used = 0, reserved = 0, remaining = 100 },
            speechSeconds = new { limit = 3600, used = 0, reserved = 0, remaining = 3600 }
        },
        accessToken,
        refreshToken,
        expiresInSeconds = 900
    });

    private static string TokenJson(string accessToken, string refreshToken) => JsonSerializer.Serialize(new
    {
        accessToken,
        refreshToken,
        expiresInSeconds = 900
    });

    private static string ErrorJson(string code) => JsonSerializer.Serialize(new
    {
        error = new
        {
            code,
            title = "目录写入失败",
            userMessage = "目录已发生变化",
            suggestion = "请刷新后重试",
            requestId = "request-admin-test"
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request, cancellationToken);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(_values.GetValueOrDefault(name));

        public Task SetAsync(string name, string value, CancellationToken cancellationToken)
        {
            _values[name] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }
}
