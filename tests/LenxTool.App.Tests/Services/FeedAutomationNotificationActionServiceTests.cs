using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationNotificationActionServiceTests
{
    private const string FeedId =
        "30000000-0000-4000-8000-000000000701";
    private const string CategoryId =
        "20000000-0000-4000-8000-000000000701";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 20, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsyncCreatesBoundedLocalNotificationAndPublishesIt()
    {
        var repository = new StubNotificationRepository();
        var inbox = new AppNotificationInbox();
        var published = new List<AppNotification>();
        inbox.NotificationReceived += _ => throw new InvalidOperationException(
            "A broken UI subscriber must not fail durable notification creation.");
        inbox.NotificationReceived += published.Add;
        var service = CreateService(repository, inbox);

        FeedAutomationNotificationActionResult result =
            await service.ExecuteAsync(Lease(), CancellationToken.None);

        Assert.Equal(FeedAutomationNotificationActionResult.Completed, result);
        AppNotification notification = Assert.Single(repository.Registered);
        Assert.Equal(Lease().IdempotencyKey, notification.Id);
        Assert.Equal("重要 AI 新闻", notification.Title);
        Assert.Equal("AI 资讯", notification.SourceLabel);
        Assert.Equal(Lease().RuleId, notification.RuleId);
        Assert.Equal(Lease().RuleVersion, notification.RuleVersion);
        Assert.Equal(Now, notification.CreatedAt);
        Assert.Null(notification.ReadAt);
        Assert.Equal(notification, Assert.Single(published));
    }

    [Fact]
    public async Task ExecuteAsyncReturnsTerminalResultsWithoutCreatingNotification()
    {
        var repository = new StubNotificationRepository();
        var inbox = new AppNotificationInbox();
        FeedAutomationNotificationActionService missing = CreateService(
            repository,
            inbox,
            entryExists: false);
        FeedAutomationNotificationActionService disabledFeed = CreateService(
            repository,
            inbox,
            feedEnabled: false);
        FeedAutomationNotificationActionService disabledCategory = CreateService(
            repository,
            inbox,
            categoryEnabled: false);

        Assert.Equal(
            FeedAutomationNotificationActionResult.EntryMissing,
            await missing.ExecuteAsync(Lease(), CancellationToken.None));
        Assert.Equal(
            FeedAutomationNotificationActionResult.FeedUnavailable,
            await disabledFeed.ExecuteAsync(Lease(), CancellationToken.None));
        Assert.Equal(
            FeedAutomationNotificationActionResult.FeedUnavailable,
            await disabledCategory.ExecuteAsync(Lease(), CancellationToken.None));
        Assert.Empty(repository.Registered);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsUnsupportedActionBeforeEntryLookup()
    {
        var entries = new StubEntryRepository(Entry());
        var service = new FeedAutomationNotificationActionService(
            new StubCatalogRepository(Catalog()),
            entries,
            new StubNotificationRepository(),
            new AppNotificationInbox(),
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync(
            Lease() with
            {
                Type = FeedAutomationActionType.SendToMedia
            },
            CancellationToken.None));

        Assert.Equal(0, entries.GetByIdCalls);
    }

    [Fact]
    public async Task ExecuteAsyncTreatsMissingCatalogAsRetryable()
    {
        var catalog = new StubCatalogRepository(Catalog())
        {
            ReturnNullCatalog = true
        };
        var repository = new StubNotificationRepository();
        var service = new FeedAutomationNotificationActionService(
            catalog,
            new StubEntryRepository(Entry()),
            repository,
            new AppNotificationInbox(),
            new FixedTimeProvider(Now));

        AppException error = await Assert.ThrowsAsync<AppException>(() =>
            service.ExecuteAsync(Lease(), CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
        Assert.True(error.Error.IsRetryable);
        Assert.Empty(repository.Registered);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotRepublishExistingNotification()
    {
        var repository = new StubNotificationRepository
        {
            Created = false
        };
        var inbox = new AppNotificationInbox();
        var published = new List<AppNotification>();
        inbox.NotificationReceived += published.Add;
        var service = CreateService(repository, inbox);

        Assert.Equal(
            FeedAutomationNotificationActionResult.Completed,
            await service.ExecuteAsync(Lease(), CancellationToken.None));

        Assert.Single(repository.Registered);
        Assert.Empty(published);
    }

    private static FeedAutomationNotificationActionService CreateService(
        StubNotificationRepository repository,
        AppNotificationInbox inbox,
        bool entryExists = true,
        bool feedEnabled = true,
        bool categoryEnabled = true) =>
        new(
            new StubCatalogRepository(Catalog(
                feedEnabled,
                categoryEnabled)),
            new StubEntryRepository(entryExists ? Entry() : null),
            repository,
            inbox,
            new FixedTimeProvider(Now));

    private static FeedCatalogSnapshot Catalog(
        bool feedEnabled = true,
        bool categoryEnabled = true) =>
        new(
            new(1, FeedCatalogScope.Active, Now, Now),
            [
                new(
                    CategoryId,
                    "Tech",
                    "tech",
                    0,
                    categoryEnabled,
                    1,
                    Now,
                    Now)
            ],
            [
                new(
                    FeedId,
                    "https://news.example/feed.xml",
                    "https://news.example/feed.xml",
                    "AI 资讯",
                    null,
                    CategoryId,
                    FeedViewKind.Article,
                    60,
                    0,
                    feedEnabled,
                    1,
                    Now,
                    Now)
            ],
            FeedAiPolicy.SafeDefaults);

    private static FeedEntry Entry() => new(
        "entry-notification",
        FeedId,
        "external-notification",
        "https://news.example/articles/notification",
        "  重要\tAI\r\n新闻  ",
        "作者",
        Now,
        Now,
        "正文不进入通知",
        "正文不进入通知",
        [],
        [],
        new string('a', 64),
        Now);

    private static FeedAutomationActionLease Lease() => new(
        new string('a', 64),
        "entry-notification",
        "40000000-0000-4000-8000-000000000701",
        3,
        100,
        0,
        FeedAutomationActionType.Notify,
        10,
        null,
        1,
        new string('b', 32));

    private sealed class StubNotificationRepository
        : IAppNotificationRepository
    {
        public bool Created { get; init; } = true;
        public List<AppNotification> Registered { get; } = [];

        public Task<AppNotificationRegistration> RegisterAsync(
            AppNotification notification,
            CancellationToken cancellationToken)
        {
            Registered.Add(notification);
            return Task.FromResult(new AppNotificationRegistration(
                notification,
                Created));
        }

        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> GetUnreadCountAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> MarkReadAsync(
            string id,
            DateTimeOffset readAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> MarkAllReadAsync(
            DateTimeOffset readAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubEntryRepository(FeedEntry? entry)
        : IFeedEntryRepository
    {
        public int GetByIdCalls { get; private set; }

        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken)
        {
            GetByIdCalls++;
            return Task.FromResult(
                entry?.Id == entryId
                    ? entry
                    : null);
        }

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCatalogRepository(
        FeedCatalogSnapshot snapshot) : IFeedCatalogRepository
    {
        public bool ReturnNullCatalog { get; init; }

        public Task<FeedCatalogState> GetStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(snapshot.State);

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedCatalogSnapshot?>(
                ReturnNullCatalog ? null : snapshot);

        public Task ReplaceAsync(
            FeedCatalogSnapshot value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
