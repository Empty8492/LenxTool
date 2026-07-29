using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.Security;

namespace LenxTool.Infrastructure.Tests.Networking;

/// <summary>
/// 验证 Worker 映射、哈希 DPAPI 槽位及健康检查的封闭网络边界。
/// </summary>
public sealed class EntryIntegrationServicesTests
{
    private static readonly string[] ReadwiseHosts =
        ["api.readwise.io"];
    private static readonly string[] WebhookHosts =
        ["hooks.example.com"];

    [Fact]
    public async Task PolicyServiceReadsAllAndStrictlyMapsExactHosts()
    {
        string? path = null;
        var handler = new HttpStubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
            {
                return Task.FromResult(LoginResponse("ADMIN"));
            }
            path = request.RequestUri?.PathAndQuery;
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                new
                {
                    policySetVersion = 4,
                    scope = "ALL",
                    generatedAt = "2026-07-29T08:00:00Z",
                    policies = new[]
                    {
                        new
                        {
                            kind = "READWISE",
                            isEnabled = true,
                            allowedHosts = ReadwiseHosts
                        }
                    }
                }));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync(
            "owner",
            "password",
            CancellationToken.None);
        var service = new WorkerEntryIntegrationPolicyService(account);

        EntryIntegrationPolicySnapshot snapshot =
            await service.GetAsync(
                EntryIntegrationPolicyScope.All,
                CancellationToken.None);

        Assert.Equal("/v1/integration-policies?scope=ALL", path);
        Assert.Equal(4, snapshot.Version);
        Assert.Equal(
            "api.readwise.io",
            Assert.Single(snapshot.Policies).AllowedHosts.Single());
    }

    [Fact]
    public async Task PolicyServiceReplacesOnlySharedPolicyFields()
    {
        var handler = new HttpStubHandler(
            async (request, cancellationToken) =>
            {
                if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                {
                    return LoginResponse("ADMIN");
                }
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal(
                    "/v1/admin/integration-policies",
                    request.RequestUri?.AbsolutePath);
                Assert.Equal(
                    "\"integration-policies-all-7\"",
                    request.Headers.GetValues("If-Match").Single());
                string json = await request.Content!
                    .ReadAsStringAsync(cancellationToken);
                Assert.DoesNotContain(
                    "token",
                    json,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "password",
                    json,
                    StringComparison.OrdinalIgnoreCase);
                return JsonResponse(
                    HttpStatusCode.OK,
                    new
                    {
                        policySetVersion = 8,
                        policies = new[]
                        {
                            new
                            {
                                kind = "WEBHOOK",
                                isEnabled = true,
                                allowedHosts = WebhookHosts
                            }
                        }
                    });
            });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync(
            "owner",
            "password",
            CancellationToken.None);
        var service = new WorkerEntryIntegrationPolicyService(account);

        EntryIntegrationPolicyMutationResult result =
            await service.ReplaceAsync(
                [
                    new(
                        EntryIntegrationKind.Webhook,
                        true,
                        ["hooks.example.com"])
                ],
                expectedVersion: 7,
                CancellationToken.None);

        Assert.Equal(8, result.Version);
        Assert.False(result.IsReplay);
        Assert.Single(result.Policies);
    }

    [Fact]
    public async Task CredentialStoreUsesHashedDpapiSlotAndNeverReturnsSecretForPresence()
    {
        var secrets = new RecordingSecretStore();
        var store = new EntryIntegrationCredentialStore(secrets);

        await store.SetAsync(
            EntryIntegrationKind.Readwise,
            "personal target/name",
            "private-token",
            CancellationToken.None);
        bool exists = await store.ExistsAsync(
            EntryIntegrationKind.Readwise,
            "personal target/name",
            CancellationToken.None);

        Assert.True(exists);
        Assert.DoesNotContain("target", secrets.LastName);
        Assert.DoesNotContain("readwise", secrets.LastName);
        Assert.Matches("^int\\.[0-9a-f]{48}$", secrets.LastName);
        Assert.Equal("private-token", secrets.Value);
    }

    [Fact]
    public async Task HealthCheckDeniesDisabledOrUnlistedTargetsBeforeProbe()
    {
        var probe = new StubProbe(EntryIntegrationKind.Webhook);
        var service = CreateHealthService(
            [
                new(
                    EntryIntegrationKind.Webhook,
                    IsEnabled: false,
                    ["hooks.example.com"])
            ],
            probe,
            credential: "secret");

        EntryIntegrationHealthResult disabled =
            await service.CheckAsync(
                Target("https://hooks.example.com/check"),
                CancellationToken.None);

        Assert.Equal(
            EntryIntegrationHealthStatus.PolicyDisabled,
            disabled.Status);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task HealthCheckRequiresCredentialAndRejectsPrivateDnsBeforeProbe()
    {
        var probe = new StubProbe(EntryIntegrationKind.Webhook);
        EntryIntegrationHealthService missingCredential =
            CreateHealthService(
                [EnabledWebhookPolicy()],
                probe,
                credential: null);
        EntryIntegrationHealthResult missing =
            await missingCredential.CheckAsync(
                Target("https://hooks.example.com/check"),
                CancellationToken.None);

        EntryIntegrationHealthService privateTarget =
            CreateHealthService(
                [EnabledWebhookPolicy()],
                probe,
                credential: "secret",
                [IPAddress.Loopback]);
        EntryIntegrationHealthResult blocked =
            await privateTarget.CheckAsync(
                Target("https://hooks.example.com/check"),
                CancellationToken.None);

        Assert.Equal(
            EntryIntegrationHealthStatus.CredentialsMissing,
            missing.Status);
        Assert.Equal(
            EntryIntegrationHealthStatus.BlockedEndpoint,
            blocked.Status);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task HealthCheckMapsTimeoutAndRateLimitToClosedRedactedResults()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(
                2026,
                7,
                29,
                8,
                0,
                0,
                TimeSpan.Zero));
        var probe = new StubProbe(
            EntryIntegrationKind.Webhook,
            async (_, _, cancellationToken) =>
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    clock,
                    cancellationToken);
                return EntryIntegrationProbeResult.Healthy();
            });
        EntryIntegrationHealthService service =
            CreateHealthService(
                [EnabledWebhookPolicy()],
                probe,
                credential: "secret",
                timeProvider: clock,
                timeout: TimeSpan.FromSeconds(5),
                cooldown: TimeSpan.FromSeconds(30));

        Task<EntryIntegrationHealthResult> pending =
            service.CheckAsync(
                Target("https://hooks.example.com/check"),
                CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(6));
        EntryIntegrationHealthResult timedOut = await pending;
        EntryIntegrationHealthResult limited =
            await service.CheckAsync(
                Target("https://hooks.example.com/check"),
                CancellationToken.None);

        Assert.Equal(
            EntryIntegrationHealthStatus.TimedOut,
            timedOut.Status);
        Assert.Equal(
            EntryIntegrationHealthStatus.RateLimited,
            limited.Status);
        Assert.InRange(
            limited.RetryAfter ?? TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(
            typeof(EntryIntegrationHealthResult).GetProperties(),
            property => property.Name.Contains(
                "Detail",
                StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "Response",
                    StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "Exception",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HealthCheckPropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        EntryIntegrationHealthService service =
            CreateHealthService(
                [EnabledWebhookPolicy()],
                new StubProbe(EntryIntegrationKind.Webhook),
                credential: "secret");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CheckAsync(
                Target("https://hooks.example.com/check"),
                cancellation.Token));
    }

    private static EntryIntegrationHealthService CreateHealthService(
        IReadOnlyList<EntryIntegrationPolicy> policies,
        IEntryIntegrationHealthProbe probe,
        string? credential,
        IReadOnlyList<IPAddress>? addresses = null,
        TimeProvider? timeProvider = null,
        TimeSpan? timeout = null,
        TimeSpan? cooldown = null) =>
        new(
            new StubPolicyService(policies),
            new StubCredentialStore(credential),
            [probe],
            new StubResolver(addresses ?? [IPAddress.Parse("93.184.216.34")]),
            new(
                timeout ?? TimeSpan.FromSeconds(8),
                cooldown ?? TimeSpan.FromSeconds(30),
                MaximumConcurrency: 2),
            timeProvider ?? TimeProvider.System);

    private static EntryIntegrationTarget Target(string endpoint) =>
        new(
            "target-1",
            EntryIntegrationKind.Webhook,
            new(endpoint));

    private static EntryIntegrationPolicy EnabledWebhookPolicy() =>
        new(
            EntryIntegrationKind.Webhook,
            IsEnabled: true,
            ["hooks.example.com"]);

    private sealed class StubPolicyService(
        IReadOnlyList<EntryIntegrationPolicy> policies)
        : IEntryIntegrationPolicyService
    {
        public Task<EntryIntegrationPolicySnapshot> GetAsync(
            EntryIntegrationPolicyScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new EntryIntegrationPolicySnapshot(1, policies));

        public Task<EntryIntegrationPolicyMutationResult> ReplaceAsync(
            IReadOnlyList<EntryIntegrationPolicyInput> inputs,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCredentialStore(string? value)
        : IEntryIntegrationCredentialStore
    {
        public Task<string?> GetAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken) =>
            Task.FromResult(value);

        public Task<bool> ExistsAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken) =>
            Task.FromResult(value is not null);

        public Task SetAsync(
            EntryIntegrationKind kind,
            string targetId,
            string value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubProbe(
        EntryIntegrationKind kind,
        Func<
            EntryIntegrationProbeContext,
            string,
            CancellationToken,
            Task<EntryIntegrationProbeResult>>? handler = null)
        : IEntryIntegrationHealthProbe
    {
        public EntryIntegrationKind Kind { get; } = kind;
        public int CallCount { get; private set; }

        public async Task<EntryIntegrationProbeResult> ProbeAsync(
            EntryIntegrationProbeContext context,
            string credential,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return handler is null
                ? EntryIntegrationProbeResult.Healthy()
                : await handler(context, credential, cancellationToken);
        }
    }

    private sealed class StubResolver(
        IReadOnlyList<IPAddress> addresses)
        : IFeedHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            Task.FromResult(addresses);
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public string LastName { get; private set; } = string.Empty;
        public string? Value { get; private set; }

        public Task<string?> GetAsync(
            string name,
            CancellationToken cancellationToken)
        {
            LastName = name;
            return Task.FromResult(Value);
        }

        public Task SetAsync(
            string name,
            string value,
            CancellationToken cancellationToken)
        {
            LastName = name;
            Value = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            string name,
            CancellationToken cancellationToken)
        {
            LastName = name;
            Value = null;
            return Task.CompletedTask;
        }
    }

    private static WorkerAccountSessionService CreateAccount(
        HttpMessageHandler handler) => new(
        new HttpClientFactoryStub(handler),
        new RecordingSecretStore(),
        new WorkerAccountOptions(new Uri("https://worker.test")));

    private static HttpResponseMessage LoginResponse(string role) =>
        JsonResponse(
            HttpStatusCode.OK,
            new
            {
                user = new
                {
                    id = "10000000-0000-4000-8000-000000000001",
                    username = "owner",
                    role
                },
                quota = new
                {
                    date = "2026-07-29",
                    ai = new
                    {
                        limit = 100,
                        used = 0,
                        reserved = 0,
                        remaining = 100
                    },
                    speechSeconds = new
                    {
                        limit = 3600,
                        used = 0,
                        reserved = 0,
                        remaining = 3600
                    }
                },
                accessToken = "access-token",
                refreshToken = "refresh-token",
                expiresInSeconds = 900
            });

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        object body) => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class HttpClientFactoryStub(
        HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(
            handler,
            disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private sealed class HttpStubHandler(
        Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) =>
            TimeProvider.System.CreateTimer(
                callback,
                state,
                TimeSpan.Zero,
                Timeout.InfiniteTimeSpan);
    }
}
