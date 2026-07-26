using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedAutomationRuleAdminServiceTests
{
    private const string RuleId = "10000000-0000-4000-8000-000000000010";

    [Fact]
    public async Task GetAllMapsDisabledRulesFromAdminScope()
    {
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("ADMIN")));
            Assert.Equal(
                "/v1/automation-rules?scope=ALL",
                request.RequestUri?.PathAndQuery);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, SnapshotJson(false)));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("owner", "password", CancellationToken.None);
        var service = new FeedAutomationRuleAdminService(account);

        FeedAutomationRuleSnapshot snapshot =
            await service.GetAllAsync(CancellationToken.None);

        Assert.Equal(7, snapshot.RuleSetVersion);
        FeedAutomationRule rule = Assert.Single(snapshot.Rules);
        Assert.False(rule.IsEnabled);
        Assert.Equal("发布摘要", rule.Name);
        Assert.Equal(FeedAutomationField.Title, Assert.Single(rule.Conditions).Field);
        Assert.Equal(FeedAutomationActionType.Notify, Assert.Single(rule.Actions).Type);
    }

    [Fact]
    public async Task CreateSendsGraphicalDefinitionWithAutomationVersionHeaders()
    {
        string? idempotencyKey = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return JsonResponse(HttpStatusCode.OK, LoginJson("ADMIN"));
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/admin/automation-rules", request.RequestUri?.AbsolutePath);
            Assert.Equal("\"automation-all-7\"", request.Headers.GetValues("If-Match").Single());
            idempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            using JsonDocument body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.False(body.RootElement.TryGetProperty("script", out _));
            Assert.Equal(
                "TITLE",
                body.RootElement.GetProperty("conditions")[0].GetProperty("field").GetString());
            Assert.Equal(
                "CONTAINS",
                body.RootElement.GetProperty("conditions")[0].GetProperty("operator").GetString());
            Assert.Equal(
                "NOTIFY",
                body.RootElement.GetProperty("actions")[0].GetProperty("type").GetString());
            return JsonResponse(HttpStatusCode.Created, MutationJson(8, true));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("owner", "password", CancellationToken.None);
        var service = new FeedAutomationRuleAdminService(account);

        FeedAutomationRuleMutationResult result = await service.CreateAsync(
            Definition(),
            7,
            CancellationToken.None);

        Assert.Equal(8, result.RuleSetVersion);
        Assert.Equal(RuleId, result.Rule.Id);
        Assert.NotNull(idempotencyKey);
        Assert.Matches("^[A-Za-z0-9._:-]{16,128}$", idempotencyKey);
    }

    [Fact]
    public async Task UpdateUsesCanonicalRuleRoute()
    {
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("ADMIN")));
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal(
                $"/v1/admin/automation-rules/{RuleId}",
                request.RequestUri?.AbsolutePath);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, MutationJson(9, true)));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("owner", "password", CancellationToken.None);
        var service = new FeedAutomationRuleAdminService(account);

        FeedAutomationRuleMutationResult result = await service.UpdateAsync(
            RuleId,
            Definition(),
            8,
            CancellationToken.None);

        Assert.Equal(9, result.RuleSetVersion);
        Assert.Equal(RuleId, result.Rule.Id);
    }

    [Fact]
    public async Task InvalidDefinitionIsRejectedBeforeNetworkMutation()
    {
        int adminCalls = 0;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("ADMIN")));
            adminCalls++;
            return Task.FromResult(JsonResponse(HttpStatusCode.Created, MutationJson(1, true)));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("owner", "password", CancellationToken.None);
        var service = new FeedAutomationRuleAdminService(account);
        FeedAutomationRuleDefinition invalid = Definition() with
        {
            Actions =
            [
                new(
                    FeedAutomationActionType.Notify,
                    0,
                    "https://example.com/injected")
            ]
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.CreateAsync(invalid, 0, CancellationToken.None));

        Assert.Equal(0, adminCalls);
    }

    [Fact]
    public async Task UserConstructedPublishReachesServerAndIsRejected()
    {
        int adminCalls = 0;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LoginJson("USER")));
            adminCalls++;
            return Task.FromResult(JsonResponse(
                HttpStatusCode.Forbidden,
                ErrorJson("ADMIN_REQUIRED")));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        var service = new FeedAutomationRuleAdminService(account);

        AppException exception = await Assert.ThrowsAsync<AppException>(
            () => service.CreateAsync(Definition(), 0, CancellationToken.None));

        Assert.Equal(AppErrorCode.AccessDenied, exception.Error.Code);
        Assert.Equal(1, adminCalls);
    }

    private static FeedAutomationRuleDefinition Definition() => new(
        "发布摘要",
        200,
        10,
        true,
        FeedAutomationMatchMode.All,
        [
            new(
                FeedAutomationField.Title,
                FeedAutomationOperator.Contains,
                "release")
        ],
        [new(FeedAutomationActionType.Notify, 0, null)]);

    private static string SnapshotJson(bool enabled) => JsonSerializer.Serialize(new
    {
        ruleSetVersion = 7,
        scope = "ALL",
        generatedAt = "2026-07-26T08:00:00Z",
        rules = new[] { RulePayload(enabled) }
    });

    private static string MutationJson(long version, bool enabled) =>
        JsonSerializer.Serialize(new
        {
            ruleSetVersion = version,
            rule = RulePayload(enabled)
        });

    private static object RulePayload(bool enabled) => new
    {
        id = RuleId,
        version = 2,
        name = "发布摘要",
        priority = 200,
        conflictOrder = 10,
        isEnabled = enabled,
        matchMode = "ALL",
        conditions = new[]
        {
            new { field = "TITLE", @operator = "CONTAINS", value = "release" }
        },
        actions = new[]
        {
            new { type = "NOTIFY", order = 0, value = (string?)null }
        }
    };

    private static WorkerAccountSessionService CreateAccount(HttpMessageHandler handler) => new(
        new StubHttpClientFactory(handler),
        new FakeSecretStore(),
        new WorkerAccountOptions(new Uri("https://worker.test")));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string LoginJson(string role) => JsonSerializer.Serialize(new
    {
        user = new
        {
            id = "10000000-0000-4000-8000-000000000001",
            username = "owner",
            role
        },
        quota = new
        {
            date = "2026-07-26",
            ai = new { limit = 100, used = 0, reserved = 0, remaining = 100 },
            speechSeconds = new { limit = 3600, used = 0, reserved = 0, remaining = 3600 }
        },
        accessToken = "access-token",
        refreshToken = "refresh-token",
        expiresInSeconds = 900
    });

    private static string ErrorJson(string code) => JsonSerializer.Serialize(new
    {
        error = new
        {
            code,
            title = "规则发布失败",
            userMessage = "需要管理员权限",
            suggestion = "请使用管理员账号",
            requestId = "request-rule-admin-test"
        }
    });

    private sealed class StubHttpClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(
            handler,
            disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responseFactory(request, cancellationToken);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetAsync(
            string name,
            CancellationToken cancellationToken) =>
            Task.FromResult(_values.GetValueOrDefault(name));

        public Task SetAsync(
            string name,
            string value,
            CancellationToken cancellationToken)
        {
            _values[name] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            string name,
            CancellationToken cancellationToken)
        {
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }
}
