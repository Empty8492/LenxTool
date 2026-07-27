using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedCatalogSyncServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task FirstSyncStoresActiveSnapshotThroughReadOnlyCatalogRoute()
    {
        HttpMethod? catalogMethod = null;
        string? catalogTarget = null;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(LoginResponse("USER"));

            catalogMethod = request.Method;
            catalogTarget = request.RequestUri?.PathAndQuery;
            return Task.FromResult(CatalogResponse(version: 3, scope: "ACTIVE"));
        });
        var repository = new FakeFeedCatalogRepository();
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        FeedCatalogSyncResult result = await service.SyncAsync(CancellationToken.None);

        Assert.Equal(FeedCatalogSyncOutcome.Updated, result.Outcome);
        Assert.Equal(HttpMethod.Get, catalogMethod);
        Assert.Equal("/v1/feeds/catalog?afterVersion=0&scope=ACTIVE", catalogTarget);
        Assert.Equal(1, repository.ReplaceCount);
        Assert.Equal(3, repository.State.Version);
        Assert.Equal(FeedCatalogScope.Active, repository.State.Scope);
        Assert.Equal(Now, repository.State.LastSyncedAt);
        Assert.Equal("technology", repository.Snapshot?.Categories.Single().NormalizedName);
        Assert.False(service.Current.IsStale);
    }

    [Fact]
    public async Task LegacyNonArticleResponseWithoutExplicitFlagKeepsOverride()
    {
        var handler = new StubHandler((request, cancellationToken) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/v1/auth/login"
                ? LoginResponse("USER")
                : LegacyPictureCatalogResponse()));
        var repository = new FakeFeedCatalogRepository();
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        await service.SyncAsync(CancellationToken.None);

        FeedCatalogItem feed = Assert.Single(repository.Snapshot!.Feeds);
        Assert.Equal(FeedViewKind.Picture, feed.ViewKind);
        Assert.True(feed.IsViewKindExplicit);
    }

    [Fact]
    public async Task SyncMapsVersionedAiPolicyDefaultsAndResourceOverrides()
    {
        var handler = new StubHandler((request, cancellationToken) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/v1/auth/login"
                ? LoginResponse("USER")
                : PolicyCatalogResponse(autoSummary: "ENABLED")));
        var repository = new FakeFeedCatalogRepository();
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        await service.SyncAsync(CancellationToken.None);

        FeedCatalogSnapshot snapshot = Assert.IsType<FeedCatalogSnapshot>(repository.Snapshot);
        Assert.Equal(FeedAiPolicy.SafeDefaults, snapshot.AiPolicyDefaults);
        Assert.Equal(FeedAiPolicySwitch.Disabled, snapshot.Categories.Single().AiPolicy?.ManualSummary);
        Assert.Equal(FeedAiPolicySwitch.Enabled, snapshot.Categories.Single().AiPolicy?.AutoSummary);
        Assert.Equal(12, snapshot.Categories.Single().AiPolicy?.DailyEntryLimit);
        Assert.Equal("ko", snapshot.Feeds.Single().AiPolicy?.TranslationTargetLanguage);
        Assert.Equal(2, snapshot.Feeds.Single().AiPolicy?.MaxConcurrency);
    }

    [Fact]
    public async Task InvalidAiPolicyResponsePreservesLastGoodCatalog()
    {
        FeedCatalogSnapshot existing = CreateSnapshot(4, FeedCatalogScope.Active, Now.AddMinutes(-5));
        var repository = new FakeFeedCatalogRepository(existing);
        var handler = new StubHandler((request, cancellationToken) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/v1/auth/login"
                ? LoginResponse("USER")
                : PolicyCatalogResponse(autoSummary: "ALWAYS")));
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => service.SyncAsync(CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
        Assert.Equal(existing, repository.Snapshot);
        Assert.Equal(0, repository.ReplaceCount);
    }

    [Fact]
    public async Task UnchangedResponseOnlyAdvancesLastSynchronizedTime()
    {
        var repository = new FakeFeedCatalogRepository(CreateSnapshot(7, FeedCatalogScope.Active, Now.AddHours(-2)));
        var handler = new StubHandler((request, cancellationToken) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/v1/auth/login"
                ? LoginResponse("USER")
                : new HttpResponseMessage(HttpStatusCode.NotModified)));
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        FeedCatalogSyncResult result = await service.SyncAsync(CancellationToken.None);

        Assert.Equal(FeedCatalogSyncOutcome.Unchanged, result.Outcome);
        Assert.Equal(0, repository.ReplaceCount);
        Assert.Equal(1, repository.MarkSynchronizedCount);
        Assert.Equal(Now, repository.State.LastSyncedAt);
        Assert.NotNull(repository.Snapshot);
    }

    [Fact]
    public async Task AdminWithActiveOnlyCacheRequestsFullAllSnapshot()
    {
        string? catalogTarget = null;
        var repository = new FakeFeedCatalogRepository(CreateSnapshot(7, FeedCatalogScope.Active, Now.AddMinutes(-5)));
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(LoginResponse("ADMIN"));
            catalogTarget = request.RequestUri?.PathAndQuery;
            return Task.FromResult(CatalogResponse(7, "ALL", includeDisabled: true));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("owner", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        await service.SyncAsync(CancellationToken.None);

        Assert.Equal("/v1/feeds/catalog?afterVersion=0&scope=ALL", catalogTarget);
        Assert.Equal(FeedCatalogScope.All, repository.State.Scope);
        Assert.Contains(repository.Snapshot!.Feeds, feed => !feed.IsEnabled);
    }

    [Fact]
    public async Task UnauthorizedCatalogRequestRefreshesAndReplaysOnce()
    {
        int refreshCalls = 0;
        int catalogCalls = 0;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/v1/auth/login") return Task.FromResult(LoginResponse("USER", "old-access", "old-refresh"));
            if (path == "/v1/auth/refresh")
            {
                refreshCalls++;
                return Task.FromResult(TokenResponse("fresh-access", "fresh-refresh"));
            }
            if (path == "/v1/feeds/catalog")
            {
                catalogCalls++;
                return Task.FromResult(request.Headers.Authorization?.Parameter == "fresh-access"
                    ? CatalogResponse(2, "ACTIVE")
                    : ErrorResponse(HttpStatusCode.Unauthorized, "TOKEN_EXPIRED"));
            }
            throw new InvalidOperationException($"Unexpected route {path}");
        });
        var repository = new FakeFeedCatalogRepository();
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(1, refreshCalls);
        Assert.Equal(2, catalogCalls);
        Assert.Equal(2, repository.State.Version);
        Assert.Equal(AccountSessionStatus.SignedIn, account.Current.Status);
    }

    [Fact]
    public async Task OfflineFailurePreservesSnapshotAndSchedulesExponentialBackoff()
    {
        FeedCatalogSnapshot existing = CreateSnapshot(5, FeedCatalogScope.Active, Now.AddMinutes(-30));
        var repository = new FakeFeedCatalogRepository(existing);
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(LoginResponse("USER"));
            throw new HttpRequestException("offline-secret-must-not-surface");
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        AppException first = await Assert.ThrowsAsync<AppException>(() => service.SyncAsync(CancellationToken.None));
        DateTimeOffset? firstRetry = service.Current.NextAttemptAt;
        AppException second = await Assert.ThrowsAsync<AppException>(() => service.SyncAsync(CancellationToken.None));

        Assert.Equal(AppErrorCode.NetworkUnavailable, first.Error.Code);
        Assert.Equal(AppErrorCode.NetworkUnavailable, second.Error.Code);
        Assert.Equal(Now.AddSeconds(5), firstRetry);
        Assert.Equal(Now.AddSeconds(10), service.Current.NextAttemptAt);
        Assert.Equal(2, service.Current.ConsecutiveFailures);
        Assert.True(service.Current.IsStale);
        Assert.Equal(0, repository.ReplaceCount);
        Assert.Equal(existing, repository.Snapshot);
        Assert.DoesNotContain("offline-secret", service.Current.Error?.TechnicalDetails ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutIsMappedAndDoesNotClearCatalog()
    {
        FeedCatalogSnapshot existing = CreateSnapshot(4, FeedCatalogScope.Active, Now.AddHours(-1));
        var repository = new FakeFeedCatalogRepository(existing);
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(LoginResponse("USER"));
            throw new TaskCanceledException("simulated client timeout");
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        AppException error = await Assert.ThrowsAsync<AppException>(() => service.SyncAsync(CancellationToken.None));

        Assert.Equal(AppErrorCode.Timeout, error.Error.Code);
        Assert.Equal(existing, repository.Snapshot);
        Assert.True(service.Current.IsStale);
    }

    [Fact]
    public async Task OlderSuccessfulSnapshotIsRejectedWithoutReplacingLocalVersion()
    {
        FeedCatalogSnapshot existing = CreateSnapshot(8, FeedCatalogScope.Active, Now.AddMinutes(-10));
        var repository = new FakeFeedCatalogRepository(existing);
        var handler = new StubHandler((request, cancellationToken) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/v1/auth/login"
                ? LoginResponse("USER")
                : CatalogResponse(7, "ACTIVE")));
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        AppException error = await Assert.ThrowsAsync<AppException>(() => service.SyncAsync(CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
        Assert.Equal(8, repository.State.Version);
        Assert.Equal(0, repository.ReplaceCount);
    }

    [Fact]
    public async Task NotModifiedCannotSatisfyARequiredFullSnapshot()
    {
        var repository = new FakeFeedCatalogRepository();
        var handler = new StubHandler((request, cancellationToken) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/v1/auth/login"
                ? LoginResponse("USER")
                : new HttpResponseMessage(HttpStatusCode.NotModified)));
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        AppException error = await Assert.ThrowsAsync<AppException>(() => service.SyncAsync(CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
        Assert.Equal(0, repository.MarkSynchronizedCount);
        Assert.Null(repository.State.LastSyncedAt);
    }

    [Fact]
    public async Task ServerVersionAheadConflictPreservesLocalSnapshot()
    {
        FeedCatalogSnapshot existing = CreateSnapshot(8, FeedCatalogScope.Active, Now.AddMinutes(-10));
        var repository = new FakeFeedCatalogRepository(existing);
        var handler = new StubHandler((request, cancellationToken) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/v1/auth/login"
                ? LoginResponse("USER")
                : ErrorResponse(HttpStatusCode.Conflict, "CATALOG_VERSION_AHEAD")));
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        await Assert.ThrowsAsync<AppException>(() => service.SyncAsync(CancellationToken.None));

        Assert.Equal(existing, repository.Snapshot);
        Assert.Equal(0, repository.ReplaceCount);
        Assert.True(service.Current.IsStale);
    }

    [Fact]
    public async Task OversizedCatalogResponseIsRejectedBeforeReplacingCache()
    {
        var repository = new FakeFeedCatalogRepository();
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(LoginResponse("USER"));
            HttpResponseMessage response = CatalogResponse(1, "ACTIVE");
            response.Content.Headers.ContentLength = 10L * 1024 * 1024 + 1;
            return Task.FromResult(response);
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);

        AppException error = await Assert.ThrowsAsync<AppException>(() => service.SyncAsync(CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
        Assert.Equal(0, repository.ReplaceCount);
        Assert.True(service.Current.IsStale);
    }

    [Fact]
    public async Task CallerCancellationStopsRequestWithoutRecordingFailure()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login") return LoginResponse("USER");
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var repository = new FakeFeedCatalogRepository();
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = CreateService(account, repository);
        using var cancellation = new CancellationTokenSource();

        Task sync = service.SyncAsync(cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
        Assert.False(service.Current.IsSynchronizing);
        Assert.Equal(0, service.Current.ConsecutiveFailures);
        Assert.Equal(0, repository.ReplaceCount);
    }

    [Fact]
    public async Task LoginAfterInitializationTriggersImmediateSynchronization()
    {
        var repository = new FakeFeedCatalogRepository();
        var handler = new StubHandler((request, cancellationToken) => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/v1/auth/login"
                ? LoginResponse("USER")
                : CatalogResponse(1, "ACTIVE")));
        using WorkerAccountSessionService account = CreateAccount(handler);
        using var service = CreateService(account, repository);
        await service.InitializeAsync(CancellationToken.None);

        await account.LoginAsync("reader", "password", CancellationToken.None);

        await WaitUntilAsync(() => repository.State.Version == 1);
        Assert.Equal(1, repository.ReplaceCount);
    }

    [Fact]
    public async Task SuccessfulInitializationSchedulesPeriodicSynchronization()
    {
        int catalogCalls = 0;
        var repository = new FakeFeedCatalogRepository();
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
                return Task.FromResult(LoginResponse("USER"));
            catalogCalls++;
            return Task.FromResult(catalogCalls == 1
                ? CatalogResponse(1, "ACTIVE")
                : new HttpResponseMessage(HttpStatusCode.NotModified));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync("reader", "password", CancellationToken.None);
        using var service = new FeedCatalogSyncService(
            account,
            repository,
            new FeedCatalogSyncOptions(
                TimeSpan.FromMilliseconds(25),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromHours(24)),
            TimeProvider.System);

        await service.InitializeAsync(CancellationToken.None);

        await WaitUntilAsync(() => repository.MarkSynchronizedCount >= 1);
        Assert.True(catalogCalls >= 2);
    }

    private static FeedCatalogSyncService CreateService(
        WorkerAccountSessionService account,
        IFeedCatalogRepository repository) => new(
            account,
            repository,
            new FeedCatalogSyncOptions(
                TimeSpan.FromMinutes(15),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromHours(24)),
            new FixedTimeProvider(Now));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static WorkerAccountSessionService CreateAccount(HttpMessageHandler handler) => new(
        new StubHttpClientFactory(handler),
        new FakeSecretStore(),
        new WorkerAccountOptions(new Uri("https://worker.test")));

    private static HttpResponseMessage LoginResponse(
        string role,
        string accessToken = "access-token",
        string refreshToken = "refresh-token") => JsonResponse(HttpStatusCode.OK, new
        {
            user = new { id = "10000000-0000-4000-8000-000000000001", username = "reader", role },
            quota = new
            {
                date = "2026-07-22",
                ai = new { limit = 10, used = 0, reserved = 0, remaining = 10 },
                speechSeconds = new { limit = 60, used = 0, reserved = 0, remaining = 60 }
            },
            accessToken,
            refreshToken,
            expiresInSeconds = 900
        });

    private static HttpResponseMessage TokenResponse(string accessToken, string refreshToken) =>
        JsonResponse(HttpStatusCode.OK, new { accessToken, refreshToken, expiresInSeconds = 900 });

    private static HttpResponseMessage CatalogResponse(long version, string scope, bool includeDisabled = false) =>
        JsonResponse(HttpStatusCode.OK, new
        {
            catalogVersion = version,
            scope,
            generatedAt = "2026-07-22T08:00:00Z",
            categories = new[]
            {
                new
                {
                    id = "20000000-0000-4000-8000-000000000001",
                    name = "Technology",
                    sortOrder = 10,
                    isEnabled = true,
                    version,
                    createdAt = "2026-07-20T08:00:00Z",
                    updatedAt = "2026-07-22T08:00:00Z"
                }
            },
            feeds = includeDisabled
                ? new[]
                {
                    FeedDto(version, true, "30000000-0000-4000-8000-000000000001"),
                    FeedDto(version, false, "30000000-0000-4000-8000-000000000002")
                }
                : new[] { FeedDto(version, true, "30000000-0000-4000-8000-000000000001") }
        });

    private static object FeedDto(long version, bool enabled, string id) => new
    {
        id,
        originalUrl = $"https://feeds.example/{id}.xml",
        normalizedUrl = $"https://feeds.example/{id}.xml",
        displayName = enabled ? "Daily Feed" : "Disabled Feed",
        siteUrl = "https://feeds.example/",
        categoryId = "20000000-0000-4000-8000-000000000001",
        viewKind = "ARTICLE",
        fullTextPolicy = enabled ? "BACKGROUND" : "NONE",
        refreshIntervalMinutes = 60,
        sortOrder = enabled ? 10 : 20,
        isEnabled = enabled,
        version,
        createdAt = "2026-07-20T08:00:00Z",
        updatedAt = "2026-07-22T08:00:00Z"
    };

    private static HttpResponseMessage LegacyPictureCatalogResponse() =>
        JsonResponse(HttpStatusCode.OK, new
        {
            catalogVersion = 1,
            scope = "ACTIVE",
            generatedAt = "2026-07-22T08:00:00Z",
            categories = new[]
            {
                new
                {
                    id = "20000000-0000-4000-8000-000000000001",
                    name = "Technology",
                    sortOrder = 10,
                    isEnabled = true,
                    version = 1,
                    createdAt = "2026-07-20T08:00:00Z",
                    updatedAt = "2026-07-22T08:00:00Z"
                }
            },
            feeds = new[]
            {
                new
                {
                    id = "30000000-0000-4000-8000-000000000001",
                    originalUrl = "https://feeds.example/picture.xml",
                    normalizedUrl = "https://feeds.example/picture.xml",
                    displayName = "Legacy Picture Feed",
                    siteUrl = "https://feeds.example/",
                    categoryId = "20000000-0000-4000-8000-000000000001",
                    viewKind = "PICTURE",
                    fullTextPolicy = "NONE",
                    refreshIntervalMinutes = 60,
                    sortOrder = 10,
                    isEnabled = true,
                    version = 1,
                    createdAt = "2026-07-20T08:00:00Z",
                    updatedAt = "2026-07-22T08:00:00Z"
                }
            }
        });

    private static HttpResponseMessage PolicyCatalogResponse(string autoSummary) =>
        JsonResponse(HttpStatusCode.OK, new
        {
            catalogVersion = 5,
            scope = "ACTIVE",
            generatedAt = "2026-07-22T08:00:00Z",
            aiPolicyDefaults = new
            {
                manualSummary = "ENABLED",
                autoSummary = "DISABLED",
                autoTranslation = "DISABLED",
                translationTargetLanguage = "zh-Hans",
                dailyEntryLimit = 20,
                maxConcurrency = 1
            },
            categories = new[]
            {
                new
                {
                    id = "20000000-0000-4000-8000-000000000001",
                    name = "Technology",
                    sortOrder = 10,
                    isEnabled = true,
                    aiPolicy = new
                    {
                        manualSummary = "DISABLED",
                        autoSummary,
                        autoTranslation = "INHERIT",
                        translationTargetLanguage = (string?)null,
                        dailyEntryLimit = (int?)12,
                        maxConcurrency = (int?)null
                    },
                    version = 5,
                    createdAt = "2026-07-20T08:00:00Z",
                    updatedAt = "2026-07-22T08:00:00Z"
                }
            },
            feeds = new[]
            {
                new
                {
                    id = "30000000-0000-4000-8000-000000000001",
                    originalUrl = "https://feeds.example/daily.xml",
                    normalizedUrl = "https://feeds.example/daily.xml",
                    displayName = "Daily Feed",
                    siteUrl = "https://feeds.example/",
                    categoryId = "20000000-0000-4000-8000-000000000001",
                    viewKind = "ARTICLE",
                    fullTextPolicy = "BACKGROUND",
                    refreshIntervalMinutes = 60,
                    sortOrder = 10,
                    isEnabled = true,
                    aiPolicy = new
                    {
                        manualSummary = "INHERIT",
                        autoSummary = "INHERIT",
                        autoTranslation = "ENABLED",
                        translationTargetLanguage = "ko",
                        dailyEntryLimit = (int?)null,
                        maxConcurrency = (int?)2
                    },
                    version = 5,
                    createdAt = "2026-07-20T08:00:00Z",
                    updatedAt = "2026-07-22T08:00:00Z"
                }
            }
        });

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status, string code) =>
        JsonResponse(status, new { error = new { code, requestId = "catalog-test" } });

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private static FeedCatalogSnapshot CreateSnapshot(
        long version,
        FeedCatalogScope scope,
        DateTimeOffset lastSynchronizedAt) => new(
            new(version, scope, Now.AddHours(-1), lastSynchronizedAt),
            [new(
                "20000000-0000-4000-8000-000000000001",
                "Technology",
                "technology",
                10,
                true,
                version,
                Now.AddDays(-2),
                Now.AddHours(-1))],
            [new(
                "30000000-0000-4000-8000-000000000001",
                "https://feeds.example/daily.xml",
                "https://feeds.example/daily.xml",
                "Daily Feed",
                "https://feeds.example/",
                "20000000-0000-4000-8000-000000000001",
                FeedViewKind.Article,
                60,
                10,
                true,
                version,
                Now.AddDays(-2),
                Now.AddHours(-1),
                FeedFullTextPolicy.Background)]);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

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

    private sealed class FakeFeedCatalogRepository : IFeedCatalogRepository
    {
        public FakeFeedCatalogRepository(FeedCatalogSnapshot? snapshot = null)
        {
            Snapshot = snapshot;
            State = snapshot?.State ?? new(0, FeedCatalogScope.Active, null, null);
        }

        public FeedCatalogSnapshot? Snapshot { get; private set; }
        public FeedCatalogState State { get; private set; }
        public int ReplaceCount { get; private set; }
        public int MarkSynchronizedCount { get; private set; }

        public Task ReplaceAsync(FeedCatalogSnapshot snapshot, CancellationToken cancellationToken)
        {
            ReplaceCount++;
            Snapshot = snapshot;
            State = snapshot.State;
            return Task.CompletedTask;
        }

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) => Task.FromResult(Snapshot);

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken)
        {
            Assert.Equal(State.Version, expectedVersion);
            MarkSynchronizedCount++;
            State = State with { LastSyncedAt = synchronizedAt };
            if (Snapshot is not null) Snapshot = Snapshot with { State = State };
            return Task.CompletedTask;
        }

        public Task<FeedCatalogState> GetStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(State);
    }
}
