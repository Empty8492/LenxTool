using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class AppNotificationNavigationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 8, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(
        AppNotificationTargetKind.FeedEntry,
        "news",
        "feed_entry")]
    [InlineData(
        AppNotificationTargetKind.AiReport,
        "ai-reports",
        "ai_report")]
    public async Task OpenResolvesTrustedTargetAndMarksNotificationRead(
        AppNotificationTargetKind targetKind,
        string expectedRoute,
        string expectedEntityType)
    {
        AppNotification notification = Notification(targetKind, EntityId);
        var repository = new StubRepository(notification);
        var navigation = new RecordingNavigationService();
        var service = new AppNotificationNavigationService(
            repository,
            navigation,
            new FixedTimeProvider(Now));

        AppNotification? opened = await service.OpenAsync(
            NotificationId,
            CancellationToken.None);

        Assert.NotNull(opened);
        Assert.Equal(Now, opened.ReadAt);
        AppNavigationRequest request = Assert.Single(navigation.Requests);
        Assert.Equal(expectedRoute, request.RouteId);
        Assert.Equal(expectedEntityType, request.EntityType);
        Assert.Equal(EntityId, request.EntityId);
        Assert.Equal(NotificationId, Assert.Single(repository.MarkedIds));
    }

    [Theory]
    [InlineData("not-hex")]
    [InlineData("https://example.com")]
    public async Task MalformedIdFailsClosedWithoutDatabaseOrNavigation(
        string id)
    {
        var repository = new StubRepository();
        var navigation = new RecordingNavigationService();
        var service = new AppNotificationNavigationService(
            repository,
            navigation,
            new FixedTimeProvider(Now));

        AppNotification? opened = await service.OpenAsync(
            id,
            CancellationToken.None);

        Assert.Null(opened);
        Assert.Equal(0, repository.GetByIdCalls);
        Assert.Empty(navigation.Requests);
        Assert.Empty(repository.MarkedIds);
    }

    [Fact]
    public async Task MissingOrInvalidPersistedNotificationFailsClosed()
    {
        AppNotification invalid = Notification(
            AppNotificationTargetKind.FeedEntry,
            "https://example.com");
        var repository = new StubRepository(invalid);
        var navigation = new RecordingNavigationService();
        var service = new AppNotificationNavigationService(
            repository,
            navigation,
            new FixedTimeProvider(Now));

        Assert.Null(await service.OpenAsync(
            new string('f', 64),
            CancellationToken.None));
        Assert.Null(await service.OpenAsync(
            NotificationId,
            CancellationToken.None));
        Assert.Empty(navigation.Requests);
        Assert.Empty(repository.MarkedIds);
    }

    private const string NotificationId =
        "59e8f62691b35bc028c5e32939b793a8a469498d0758fe4b0974f01c03d3031a";
    private const string EntityId =
        "7f0a7aa7b7f2ee754c1f6337becc09d87885d985dfcba6e71bb69bee9c535b46";

    private static AppNotification Notification(
        AppNotificationTargetKind targetKind,
        string? targetId) =>
        new(
            NotificationId,
            EntityId,
            "feed-1",
            Guid.Empty.ToString("D"),
            1,
            "标题",
            "来源",
            Now.AddMinutes(-1),
            ReadAt: null,
            AppNotificationKind.TaskCompleted,
            targetKind,
            targetId);

    private sealed class StubRepository(params AppNotification[] items)
        : IAppNotificationRepository
    {
        private readonly Dictionary<string, AppNotification> _items =
            items.ToDictionary(item => item.Id, StringComparer.Ordinal);

        public int GetByIdCalls { get; private set; }
        public List<string> MarkedIds { get; } = [];

        public Task<AppNotification?> GetByIdAsync(
            string id,
            CancellationToken cancellationToken)
        {
            GetByIdCalls++;
            return Task.FromResult(_items.GetValueOrDefault(id));
        }

        public Task<bool> MarkReadAsync(
            string id,
            DateTimeOffset readAt,
            CancellationToken cancellationToken)
        {
            if (!_items.TryGetValue(id, out AppNotification? notification)
                || notification.IsRead)
            {
                return Task.FromResult(false);
            }
            _items[id] = notification with { ReadAt = readAt };
            MarkedIds.Add(id);
            return Task.FromResult(true);
        }

        public Task<AppNotificationRegistration> RegisterAsync(
            AppNotification notification,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> GetUnreadCountAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> MarkAllReadAsync(
            DateTimeOffset readAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingNavigationService : IAppNavigationService
    {
        public List<AppNavigationRequest> Requests { get; } = [];

        public Task NavigateAsync(
            AppNavigationRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
