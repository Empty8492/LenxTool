using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class LocalAppNotificationPublisherTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PublishNormalizesLabelsAndDeduplicatesBeforeInboxEvent()
    {
        var repository = new StubRepository();
        var inbox = new AppNotificationInbox();
        var received = new List<AppNotification>();
        inbox.NotificationReceived += received.Add;
        var publisher = new LocalAppNotificationPublisher(
            repository,
            inbox,
            new FixedTimeProvider(Now));
        var draft = new AppNotificationDraft(
            AppNotificationKind.SystemHealth,
            "feed-health:one",
            "feed-health:one",
            "20000000-0000-4000-8000-000000000001",
            "  Daily\r\n抓取异常  ",
            " Daily ");

        AppNotificationRegistration first =
            await publisher.PublishAsync(draft, CancellationToken.None);
        AppNotificationRegistration duplicate =
            await publisher.PublishAsync(draft, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Notification.Id, duplicate.Notification.Id);
        Assert.Equal(AppNotificationKind.SystemHealth, first.Notification.Kind);
        Assert.Equal(AppNotificationTargetKind.None, first.Notification.TargetKind);
        Assert.Null(first.Notification.TargetId);
        Assert.Equal("Daily 抓取异常", first.Notification.Title);
        Assert.Equal("Daily", first.Notification.SourceLabel);
        Assert.Equal(Now, first.Notification.CreatedAt);
        Assert.Equal(first.Notification, Assert.Single(received));
    }

    private sealed class StubRepository : IAppNotificationRepository
    {
        private AppNotification? _notification;

        public Task<AppNotificationRegistration> RegisterAsync(
            AppNotification notification,
            CancellationToken cancellationToken)
        {
            bool created = _notification is null;
            _notification ??= notification;
            return Task.FromResult(
                new AppNotificationRegistration(
                    _notification,
                    created));
        }

        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AppNotification?> GetByIdAsync(
            string id,
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
