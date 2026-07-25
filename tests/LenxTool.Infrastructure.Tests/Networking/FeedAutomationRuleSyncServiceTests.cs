using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedAutomationRuleSyncServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UpdatedResponseMapsActiveRulesAndUsesIncrementalRoute()
    {
        string? target = null;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
            {
                return Task.FromResult(LoginResponse());
            }
            target = request.RequestUri?.PathAndQuery;
            return Task.FromResult(RuleResponse(version: 3));
        });
        var repository = new FakeRuleRepository(
            Snapshot(version: 2, synchronizedAt: Now.AddHours(-1)));
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        FeedAutomationRuleSyncResult result =
            await service.SyncAsync(CancellationToken.None);

        Assert.Equal(FeedAutomationRuleSyncOutcome.Updated, result.Outcome);
        Assert.Equal(
            "/v1/automation-rules?scope=ACTIVE&afterVersion=2",
            target);
        Assert.Equal(1, repository.ReplaceCount);
        FeedAutomationRule rule = Assert.Single(repository.Current.Rules);
        Assert.Equal("AI release digest", rule.Name);
        Assert.Equal(FeedAutomationMatchMode.All, rule.MatchMode);
        Assert.Equal(
            FeedAutomationField.Title,
            Assert.Single(rule.Conditions).Field);
        Assert.Equal(
            FeedAutomationActionType.GenerateSummary,
            Assert.Single(rule.Actions).Type);
        Assert.Equal(Now, repository.Current.LastSyncedAt);
    }

    [Fact]
    public async Task FirstEmptyRuleSetNotModifiedCreatesSynchronizedSnapshot()
    {
        var handler = new StubHandler((request, cancellationToken) =>
            Task.FromResult(
                request.RequestUri?.AbsolutePath == "/v1/auth/login"
                    ? LoginResponse()
                    : new HttpResponseMessage(HttpStatusCode.NotModified)));
        var repository = new FakeRuleRepository();
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        FeedAutomationRuleSyncResult result =
            await service.SyncAsync(CancellationToken.None);

        Assert.Equal(FeedAutomationRuleSyncOutcome.Unchanged, result.Outcome);
        Assert.Equal(1, repository.ReplaceCount);
        Assert.Equal(0, repository.Current.RuleSetVersion);
        Assert.Null(repository.Current.GeneratedAt);
        Assert.Equal(Now, repository.Current.LastSyncedAt);
        Assert.Empty(repository.Current.Rules);
    }

    [Fact]
    public async Task ExistingSnapshotNotModifiedOnlyAdvancesSynchronizationTime()
    {
        var handler = new StubHandler((request, cancellationToken) =>
            Task.FromResult(
                request.RequestUri?.AbsolutePath == "/v1/auth/login"
                    ? LoginResponse()
                    : new HttpResponseMessage(HttpStatusCode.NotModified)));
        var repository = new FakeRuleRepository(
            Snapshot(version: 2, synchronizedAt: Now.AddHours(-1)));
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(0, repository.ReplaceCount);
        Assert.Equal(1, repository.MarkSynchronizedCount);
        Assert.Equal(Now, repository.Current.LastSyncedAt);
        Assert.Equal(2, repository.Current.RuleSetVersion);
    }

    [Fact]
    public async Task InvalidResponsePreservesLastGoodSnapshot()
    {
        FeedAutomationRuleSnapshot existing =
            Snapshot(version: 4, synchronizedAt: Now.AddMinutes(-15));
        var handler = new StubHandler((request, cancellationToken) =>
            Task.FromResult(
                request.RequestUri?.AbsolutePath == "/v1/auth/login"
                    ? LoginResponse()
                    : RuleResponse(version: 5, scope: "ALL")));
        var repository = new FakeRuleRepository(existing);
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        AppException exception = await Assert.ThrowsAsync<AppException>(
            () => service.SyncAsync(CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, exception.Error.Code);
        Assert.Equal(existing, repository.Current);
        Assert.Equal(0, repository.ReplaceCount);
        Assert.Equal(0, repository.MarkSynchronizedCount);
    }

    [Fact]
    public async Task NullRuleInResponseIsRejectedWithoutChangingCache()
    {
        FeedAutomationRuleSnapshot existing =
            Snapshot(version: 4, synchronizedAt: Now.AddMinutes(-15));
        var handler = new StubHandler((request, cancellationToken) =>
            Task.FromResult(
                request.RequestUri?.AbsolutePath == "/v1/auth/login"
                    ? LoginResponse()
                    : JsonResponse(
                        HttpStatusCode.OK,
                        new
                        {
                            ruleSetVersion = 5,
                            scope = "ACTIVE",
                            generatedAt = "2026-07-25T17:55:00Z",
                            rules = new object?[] { null }
                        })));
        var repository = new FakeRuleRepository(existing);
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        AppException exception = await Assert.ThrowsAsync<AppException>(
            () => service.SyncAsync(CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, exception.Error.Code);
        Assert.Equal(existing, repository.Current);
        Assert.Equal(0, repository.ReplaceCount);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeReplacingCache()
    {
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
            {
                return Task.FromResult(LoginResponse());
            }
            HttpResponseMessage response = RuleResponse(version: 1);
            response.Content.Headers.ContentLength =
                4L * 1024 * 1024 + 1;
            return Task.FromResult(response);
        });
        var repository = new FakeRuleRepository();
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        AppException exception = await Assert.ThrowsAsync<AppException>(
            () => service.SyncAsync(CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, exception.Error.Code);
        Assert.Equal(0, repository.ReplaceCount);
    }

    [Fact]
    public async Task CallerCancellationStopsRequestWithoutChangingCache()
    {
        var requestStarted =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
            {
                return LoginResponse();
            }
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        FeedAutomationRuleSnapshot existing =
            Snapshot(version: 2, synchronizedAt: Now.AddMinutes(-10));
        var repository = new FakeRuleRepository(existing);
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);
        using var cancellation = new CancellationTokenSource();

        Task sync = service.SyncAsync(cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
        Assert.Equal(existing, repository.Current);
        Assert.Equal(0, repository.ReplaceCount);
        Assert.Equal(0, repository.MarkSynchronizedCount);
    }

    [Fact]
    public async Task SignedOutSyncSkipsNetworkAndCacheWrites()
    {
        int calls = 0;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            calls++;
            throw new InvalidOperationException("Network should not be called.");
        });
        var repository = new FakeRuleRepository();
        using WorkerAccountSessionService account = CreateAccount(handler);
        using var service = CreateService(account, repository);

        FeedAutomationRuleSyncResult result =
            await service.SyncAsync(CancellationToken.None);

        Assert.Equal(
            FeedAutomationRuleSyncOutcome.SkippedNotAuthenticated,
            result.Outcome);
        Assert.Equal(0, calls);
        Assert.Equal(0, repository.ReplaceCount);
    }

    private static FeedAutomationRuleSyncService CreateService(
        WorkerAccountSessionService account,
        IFeedAutomationRuleRepository repository) => new(
            account,
            repository,
            new FixedTimeProvider(Now));

    private static WorkerAccountSessionService CreateAccount(
        HttpMessageHandler handler) => new(
            new StubHttpClientFactory(handler),
            new FakeSecretStore(),
            new WorkerAccountOptions(new Uri("https://worker.test")));

    private static FeedAutomationRuleSnapshot Snapshot(
        long version,
        DateTimeOffset synchronizedAt) => new(
        version,
        Now.AddHours(-2),
        synchronizedAt,
        [
            new(
                "30000000-0000-4000-8000-000000000201",
                1,
                "Existing",
                100,
                0,
                true,
                FeedAutomationMatchMode.All,
                [
                    new(
                        FeedAutomationField.Title,
                        FeedAutomationOperator.Contains,
                        "AI")
                ],
                [
                    new(
                        FeedAutomationActionType.MarkRead,
                        10,
                        null)
                ])
        ]);

    private static HttpResponseMessage RuleResponse(
        long version,
        string scope = "ACTIVE") => JsonResponse(
        HttpStatusCode.OK,
        new
        {
            ruleSetVersion = version,
            scope,
            generatedAt = "2026-07-25T17:55:00Z",
            limits = new
            {
                maximumRules = 100,
                maximumConditions = 16,
                maximumActions = 8,
                maximumTextLength = 512,
                maximumRegexLength = 256,
                regexTimeoutMilliseconds = 100
            },
            rules = new[]
            {
                new
                {
                    id = "30000000-0000-4000-8000-000000000202",
                    version = 2,
                    name = "AI release digest",
                    priority = 200,
                    conflictOrder = 10,
                    isEnabled = true,
                    matchMode = "ALL",
                    conditions = new[]
                    {
                        new
                        {
                            field = "TITLE",
                            @operator = "CONTAINS",
                            value = "release notes"
                        }
                    },
                    actions = new[]
                    {
                        new
                        {
                            type = "GENERATE_SUMMARY",
                            order = 10,
                            value = (string?)null
                        }
                    }
                }
            }
        });

    private static HttpResponseMessage LoginResponse() => JsonResponse(
        HttpStatusCode.OK,
        new
        {
            user = new
            {
                id = "10000000-0000-4000-8000-000000000001",
                username = "reader",
                role = "USER"
            },
            quota = new
            {
                date = "2026-07-25",
                ai = new
                {
                    limit = 10,
                    used = 0,
                    reserved = 0,
                    remaining = 10
                },
                speechSeconds = new
                {
                    limit = 60,
                    used = 0,
                    reserved = 0,
                    remaining = 60
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
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

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values =
            new(StringComparer.Ordinal);

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

    private sealed class FakeRuleRepository(
        FeedAutomationRuleSnapshot? snapshot = null)
        : IFeedAutomationRuleRepository
    {
        public FeedAutomationRuleSnapshot Current { get; private set; } =
            snapshot ?? new(
                0,
                GeneratedAt: null,
                LastSyncedAt: null,
                Rules: Array.Empty<FeedAutomationRule>());
        public int ReplaceCount { get; private set; }
        public int MarkSynchronizedCount { get; private set; }

        public Task<FeedAutomationRuleSnapshot> GetAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task ReplaceAsync(
            FeedAutomationRuleSnapshot next,
            CancellationToken cancellationToken)
        {
            ReplaceCount++;
            Current = next;
            return Task.CompletedTask;
        }

        public Task<bool> MarkSynchronizedAsync(
            long expectedRuleSetVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken)
        {
            if (Current.RuleSetVersion != expectedRuleSetVersion)
            {
                return Task.FromResult(false);
            }
            MarkSynchronizedCount++;
            Current = Current with { LastSyncedAt = synchronizedAt };
            return Task.FromResult(true);
        }
    }

}
