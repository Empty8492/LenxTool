using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedSmartViewServicesTests
{
    private const string ViewId =
        "30000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SyncUsesIncrementalActiveRouteAndReplacesValidatedSnapshot()
    {
        string? target = null;
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
            {
                return Task.FromResult(LoginResponse("USER"));
            }
            target = request.RequestUri?.PathAndQuery;
            return Task.FromResult(
                SnapshotResponse(3, "ACTIVE", enabled: true));
        });
        var repository = new FakeRepository(Snapshot(2));
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync(
            "reader",
            "password",
            CancellationToken.None);
        using var service = new FeedSmartViewSyncService(
            account,
            repository,
            new FixedTimeProvider(Now));

        FeedSmartViewSyncResult result =
            await service.SyncAsync(CancellationToken.None);

        Assert.Equal(FeedSmartViewSyncOutcome.Updated, result.Outcome);
        Assert.Equal(
            "/v1/smart-views?scope=ACTIVE&afterVersion=2",
            target);
        Assert.Equal(1, repository.ReplaceCalls);
        FeedSmartView view =
            Assert.Single(repository.Current.Views);
        Assert.Equal(EntryViewKind.Video, view.Filter.ViewKind);
        Assert.Equal(FeedEntryReadFilter.Unread, view.Filter.ReadFilter);
        Assert.Equal(Now, repository.Current.LastSyncedAt);
    }

    [Fact]
    public async Task InvalidOrNonIncreasingSnapshotPreservesOfflineCache()
    {
        FeedSmartViewSnapshot existing = Snapshot(4);
        var handler = new StubHandler((request, cancellationToken) =>
            Task.FromResult(
                request.RequestUri?.AbsolutePath == "/v1/auth/login"
                    ? LoginResponse("USER")
                    : SnapshotResponse(
                        4,
                        "ACTIVE",
                        enabled: true)));
        var repository = new FakeRepository(existing);
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync(
            "reader",
            "password",
            CancellationToken.None);
        using var service = new FeedSmartViewSyncService(
            account,
            repository,
            new FixedTimeProvider(Now));

        AppException exception = await Assert.ThrowsAsync<AppException>(
            () => service.SyncAsync(CancellationToken.None));

        Assert.Equal(
            AppErrorCode.ProviderUnavailable,
            exception.Error.Code);
        Assert.Same(existing, repository.Current);
        Assert.Equal(0, repository.ReplaceCalls);
    }

    [Fact]
    public async Task AdminCreateSendsFilterOnlyPayloadAndSmartViewVersion()
    {
        string? idempotencyKey = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
            {
                return LoginResponse("ADMIN");
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "/v1/admin/smart-views",
                request.RequestUri?.AbsolutePath);
            Assert.Equal(
                "\"smart-views-all-7\"",
                request.Headers.GetValues("If-Match").Single());
            idempotencyKey = request.Headers
                .GetValues("Idempotency-Key").Single();
            string json = await request.Content!
                .ReadAsStringAsync(cancellationToken);
            using JsonDocument body = JsonDocument.Parse(json);
            Assert.True(body.RootElement.TryGetProperty("filter", out _));
            Assert.False(body.RootElement.TryGetProperty("content", out _));
            Assert.False(body.RootElement.TryGetProperty("url", out _));
            return MutationResponse(8);
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync(
            "owner",
            "password",
            CancellationToken.None);
        var service = new FeedSmartViewAdminService(account);

        FeedSmartViewMutationResult result =
            await service.CreateAsync(
                Input(),
                7,
                CancellationToken.None);

        Assert.Equal(8, result.ViewSetVersion);
        Assert.Equal(ViewId, result.View?.Id);
        Assert.Matches(
            "^[A-Za-z0-9._:-]{16,128}$",
            idempotencyKey);
    }

    [Fact]
    public async Task AdminDeleteUsesEmptyBodyAndCanonicalRoute()
    {
        var handler = new StubHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/auth/login")
            {
                return Task.FromResult(LoginResponse("ADMIN"));
            }
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal(
                $"/v1/admin/smart-views/{ViewId}",
                request.RequestUri?.AbsolutePath);
            Assert.Null(request.Content);
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                new
                {
                    viewSetVersion = 9,
                    deletedViewId = ViewId
                }));
        });
        using WorkerAccountSessionService account = CreateAccount(handler);
        await account.LoginAsync(
            "owner",
            "password",
            CancellationToken.None);
        var service = new FeedSmartViewAdminService(account);

        FeedSmartViewMutationResult result =
            await service.DeleteAsync(
                ViewId,
                8,
                CancellationToken.None);

        Assert.Equal(9, result.ViewSetVersion);
        Assert.Null(result.View);
        Assert.Equal(ViewId, result.DeletedViewId);
    }

    private static FeedSmartViewInput Input() => new(
        "视频收藏",
        20,
        true,
        new(
            "20000000-0000-4000-8000-000000000001",
            "10000000-0000-4000-8000-000000000001",
            EntryViewKind.Video,
            FeedEntryReadFilter.Unread,
            true,
            "release",
            30));

    private static FeedSmartViewSnapshot Snapshot(long version) => new(
        version,
        FeedSmartViewScope.Active,
        Now.AddHours(-1),
        Now.AddHours(-1),
        [
            new(
                ViewId,
                1,
                "Existing",
                10,
                true,
                Input().Filter)
        ]);

    private static HttpResponseMessage SnapshotResponse(
        long version,
        string scope,
        bool enabled) => JsonResponse(
        HttpStatusCode.OK,
        new
        {
            viewSetVersion = version,
            scope,
            generatedAt = "2026-07-28T11:55:00Z",
            views = new[] { ViewPayload(enabled) }
        });

    private static HttpResponseMessage MutationResponse(long version) =>
        JsonResponse(
            HttpStatusCode.Created,
            new
            {
                viewSetVersion = version,
                view = ViewPayload(enabled: true)
            });

    private static object ViewPayload(bool enabled) => new
    {
        id = ViewId,
        version = 2,
        name = "视频收藏",
        sortOrder = 20,
        isEnabled = enabled,
        filter = new
        {
            feedId = "20000000-0000-4000-8000-000000000001",
            categoryId =
                "10000000-0000-4000-8000-000000000001",
            viewKind = "VIDEO",
            readFilter = "UNREAD",
            favoritesOnly = true,
            searchText = "release",
            publishedWithinDays = 30
        }
    };

    private static WorkerAccountSessionService CreateAccount(
        HttpMessageHandler handler) => new(
        new StubHttpClientFactory(handler),
        new FakeSecretStore(),
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
                    date = "2026-07-28",
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

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
            send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
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

    private sealed class FakeRepository(
        FeedSmartViewSnapshot? initial = null)
        : IFeedSmartViewRepository
    {
        public FeedSmartViewSnapshot Current { get; private set; } =
            initial ?? new(
                0,
                FeedSmartViewScope.Active,
                null,
                null,
                Array.Empty<FeedSmartView>());
        public int ReplaceCalls { get; private set; }

        public Task<FeedSmartViewSnapshot> GetAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task ReplaceAsync(
            FeedSmartViewSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            ReplaceCalls++;
            Current = snapshot;
            return Task.CompletedTask;
        }

        public Task<bool> MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken)
        {
            if (Current.ViewSetVersion != expectedVersion)
            {
                return Task.FromResult(false);
            }
            Current = Current with
            {
                LastSyncedAt = synchronizedAt
            };
            return Task.FromResult(true);
        }
    }
}
