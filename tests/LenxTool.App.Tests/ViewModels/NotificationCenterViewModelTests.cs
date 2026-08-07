using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class NotificationCenterViewModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 21, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task InitializeRestoresRecentItemsAndUnreadCount()
    {
        AppNotification unread = Notification('a', "未读");
        AppNotification read = Notification('b', "已读") with
        {
            CreatedAt = Now.AddMinutes(-1),
            ReadAt = Now
        };
        var repository = new StubRepository(unread, read);
        var viewModel = new NotificationCenterViewModel(
            repository,
            new AppNotificationInbox(),
            new FixedTimeProvider(Now));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal([unread.Id, read.Id], viewModel.Items.Select(item => item.Id));
        Assert.Equal(1, viewModel.UnreadCount);
        Assert.True(viewModel.HasUnread);
        Assert.Equal("1", viewModel.BadgeText);
    }

    [Fact]
    public async Task InboxNotificationAppearsOnceAndUpdatesUnreadBadge()
    {
        var repository = new StubRepository();
        var inbox = new AppNotificationInbox();
        var viewModel = new NotificationCenterViewModel(
            repository,
            inbox,
            new FixedTimeProvider(Now));
        await viewModel.InitializeAsync(CancellationToken.None);
        AppNotification notification = Notification('c', "即时通知");

        inbox.Publish(notification);
        inbox.Publish(notification);

        Assert.Equal(notification, Assert.Single(viewModel.Items));
        Assert.Equal(1, viewModel.UnreadCount);
        Assert.Equal("1", viewModel.BadgeText);
        Assert.True(viewModel.HasUnread);
    }

    [Fact]
    public async Task MarkReadAndMarkAllUpdateRepositoryAndLocalState()
    {
        AppNotification first = Notification('d', "第一条");
        AppNotification second = Notification('e', "第二条") with
        {
            CreatedAt = Now.AddMinutes(-1)
        };
        var repository = new StubRepository(first, second);
        var viewModel = new NotificationCenterViewModel(
            repository,
            new AppNotificationInbox(),
            new FixedTimeProvider(Now));
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.MarkReadCommand.ExecuteAsync(first);

        Assert.Equal(Now, viewModel.Items[0].ReadAt);
        Assert.Equal(1, viewModel.UnreadCount);
        Assert.Equal(first.Id, Assert.Single(repository.MarkedIds));

        await viewModel.MarkAllReadCommand.ExecuteAsync();

        Assert.All(viewModel.Items, item => Assert.True(item.IsRead));
        Assert.Equal(0, viewModel.UnreadCount);
        Assert.False(viewModel.HasUnread);
        Assert.Equal(string.Empty, viewModel.BadgeText);
        Assert.Equal(1, repository.MarkAllCalls);
    }

    [Fact]
    public async Task OpenUsesTheSharedSafeRouterAndUpdatesLocalReadState()
    {
        AppNotification notification = Notification('3', "打开目标") with
        {
            TargetKind = AppNotificationTargetKind.FeedEntry,
            TargetId = "entry-3"
        };
        var repository = new StubRepository(notification);
        var navigation = new StubNotificationNavigationService(
            notification with { ReadAt = Now });
        var viewModel = new NotificationCenterViewModel(
            repository,
            new AppNotificationInbox(),
            new FixedTimeProvider(Now),
            navigation);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.IsOpen = true;

        await viewModel.OpenCommand.ExecuteAsync(notification);

        Assert.Equal(notification.Id, Assert.Single(navigation.OpenedIds));
        Assert.True(viewModel.Items[0].IsRead);
        Assert.Equal(0, viewModel.UnreadCount);
        Assert.False(viewModel.IsOpen);
    }

    [Fact]
    public async Task ExternalSharedRouterOpenSynchronizesCurrentUnreadProjection()
    {
        AppNotification notification = Notification('4', "系统通知打开") with
        {
            TargetKind = AppNotificationTargetKind.FeedEntry,
            TargetId = "entry-4"
        };
        var repository = new StubRepository(notification);
        var navigation = new AppNotificationNavigationService(
            repository,
            new RecordingAppNavigationService(),
            new FixedTimeProvider(Now));
        var viewModel = new NotificationCenterViewModel(
            repository,
            new AppNotificationInbox(),
            new FixedTimeProvider(Now),
            navigation);
        await viewModel.InitializeAsync(CancellationToken.None);

        await navigation.OpenAsync(
            notification.Id,
            CancellationToken.None);

        Assert.True(viewModel.Items[0].IsRead);
        Assert.Equal(0, viewModel.UnreadCount);
        Assert.False(viewModel.HasUnread);
    }

    [Fact]
    public async Task ExternalOpenDecrementsUnreadWhenTargetIsOutsideRecentWindow()
    {
        AppNotification[] notifications = Enumerable.Range(0, 51)
            .Select(index => Notification('a', $"通知 {index}") with
            {
                Id = index.ToString(
                    "x64",
                    System.Globalization.CultureInfo.InvariantCulture),
                EntryId = $"entry-{index}",
                CreatedAt = Now.AddMinutes(-index),
                TargetKind = AppNotificationTargetKind.FeedEntry,
                TargetId = $"entry-{index}"
            })
            .ToArray();
        AppNotification outsideRecent = notifications[^1];
        var repository = new StubRepository(notifications);
        var navigation = new AppNotificationNavigationService(
            repository,
            new RecordingAppNavigationService(),
            new FixedTimeProvider(Now));
        var viewModel = new NotificationCenterViewModel(
            repository,
            new AppNotificationInbox(),
            new FixedTimeProvider(Now),
            navigation);
        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.Equal(51, viewModel.UnreadCount);
        Assert.DoesNotContain(
            viewModel.Items,
            item => item.Id == outsideRecent.Id);

        await navigation.OpenAsync(
            outsideRecent.Id,
            CancellationToken.None);

        Assert.Equal(50, viewModel.UnreadCount);
        Assert.DoesNotContain(
            viewModel.Items,
            item => item.Id == outsideRecent.Id);
    }

    [Fact]
    public async Task ToggleControlsPanelWithoutDatabaseAccess()
    {
        var repository = new StubRepository();
        var viewModel = new NotificationCenterViewModel(
            repository,
            new AppNotificationInbox(),
            new FixedTimeProvider(Now));

        viewModel.ToggleCommand.Execute(null);

        Assert.True(viewModel.IsOpen);
        Assert.Equal(0, repository.RecentCalls);
    }

    [Fact]
    public async Task KindFilterChangesVisibleItemsWithoutChangingPrivateUnreadState()
    {
        AppNotification content = Notification('f', "内容命中");
        AppNotification health = Notification('1', "抓取异常") with
        {
            Kind = AppNotificationKind.SystemHealth,
            CreatedAt = Now.AddMinutes(-1)
        };
        AppNotification task = Notification('2', "摘要完成") with
        {
            Kind = AppNotificationKind.TaskCompleted,
            CreatedAt = Now.AddMinutes(-2)
        };
        var repository = new StubRepository(content, health, task);
        var viewModel = new NotificationCenterViewModel(
            repository,
            new AppNotificationInbox(),
            new FixedTimeProvider(Now));
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedKindFilter =
            AppNotificationKindFilter.SystemHealth;

        Assert.Equal(health, Assert.Single(viewModel.Items));
        Assert.Equal(3, viewModel.UnreadCount);
        Assert.Equal(0, repository.MarkAllCalls);

        viewModel.SelectedKindFilter =
            AppNotificationKindFilter.All;
        Assert.Equal(3, viewModel.Items.Count);
    }

    private static AppNotification Notification(
        char key,
        string title) => new(
        new string(key, 64),
        $"entry-{key}",
        "30000000-0000-4000-8000-000000000701",
        "40000000-0000-4000-8000-000000000701",
        3,
        title,
        "AI 资讯",
        Now,
        null);

    private sealed class StubRepository(params AppNotification[] items)
        : IAppNotificationRepository
    {
        private readonly Dictionary<string, AppNotification> _items =
            items.ToDictionary(item => item.Id);

        public int RecentCalls { get; private set; }
        public List<string> MarkedIds { get; } = [];
        public int MarkAllCalls { get; private set; }

        public Task<AppNotificationRegistration> RegisterAsync(
            AppNotification notification,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken)
        {
            RecentCalls++;
            return Task.FromResult<IReadOnlyList<AppNotification>>(
                _items.Values
                    .OrderByDescending(item => item.CreatedAt)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Take(maximumCount)
                    .ToArray());
        }

        public Task<AppNotification?> GetByIdAsync(
            string id,
            CancellationToken cancellationToken) =>
            Task.FromResult(_items.GetValueOrDefault(id));

        public Task<int> GetUnreadCountAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(_items.Values.Count(item => !item.IsRead));

        public Task<bool> MarkReadAsync(
            string id,
            DateTimeOffset readAt,
            CancellationToken cancellationToken)
        {
            if (!_items.TryGetValue(id, out AppNotification? item) ||
                item.IsRead)
            {
                return Task.FromResult(false);
            }

            _items[id] = item with { ReadAt = readAt };
            MarkedIds.Add(id);
            return Task.FromResult(true);
        }

        public Task<int> MarkAllReadAsync(
            DateTimeOffset readAt,
            CancellationToken cancellationToken)
        {
            MarkAllCalls++;
            int updated = 0;
            foreach ((string id, AppNotification item) in _items.ToArray())
            {
                if (item.IsRead)
                {
                    continue;
                }
                _items[id] = item with { ReadAt = readAt };
                updated++;
            }
            return Task.FromResult(updated);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingAppNavigationService
        : IAppNavigationService
    {
        public Task NavigateAsync(
            AppNavigationRequest request,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubNotificationNavigationService(
        AppNotification result) : IAppNotificationNavigationService
    {
        public List<string> OpenedIds { get; } = [];

        public event EventHandler<AppNotificationOpenedEventArgs>?
            NotificationOpened;

        public Task<AppNotification?> OpenAsync(
            string notificationId,
            CancellationToken cancellationToken)
        {
            OpenedIds.Add(notificationId);
            NotificationOpened?.Invoke(
                this,
                new AppNotificationOpenedEventArgs(result));
            return Task.FromResult<AppNotification?>(result);
        }
    }
}
