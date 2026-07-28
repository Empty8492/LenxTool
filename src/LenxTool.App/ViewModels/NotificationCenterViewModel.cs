using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public enum AppNotificationKindFilter
{
    All,
    ContentMatch,
    SystemHealth,
    TaskCompleted
}

public sealed record AppNotificationKindFilterOption(
    AppNotificationKindFilter Value,
    string Label);

public sealed class NotificationCenterViewModel : ObservableObject
{
    private const int RecentLimit = 50;
    private readonly IAppNotificationRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly List<AppNotification> _allItems = [];
    private bool _isOpen;
    private int _unreadCount;
    private AppNotificationKindFilter _selectedKindFilter;

    public NotificationCenterViewModel(
        IAppNotificationRepository repository,
        IAppNotificationInbox inbox,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _repository = repository;
        _timeProvider = timeProvider;
        _synchronizationContext = SynchronizationContext.Current;
        ToggleCommand = new(() => IsOpen = !IsOpen);
        MarkReadCommand = new(MarkReadAsync, item => item is { IsRead: false });
        MarkAllReadCommand = new(
            MarkAllReadAsync,
            () => UnreadCount > 0);
        inbox.NotificationReceived += OnNotificationReceived;
    }

    public ObservableCollection<AppNotification> Items { get; } = [];
    public IReadOnlyList<AppNotificationKindFilterOption> KindFilters { get; } =
    [
        new(AppNotificationKindFilter.All, "全部"),
        new(AppNotificationKindFilter.ContentMatch, "内容命中"),
        new(AppNotificationKindFilter.SystemHealth, "系统健康"),
        new(AppNotificationKindFilter.TaskCompleted, "任务完成")
    ];
    public RelayCommand ToggleCommand { get; }
    public AsyncRelayCommand<AppNotification> MarkReadCommand { get; }
    public AsyncRelayCommand MarkAllReadCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public AppNotificationKindFilter SelectedKindFilter
    {
        get => _selectedKindFilter;
        set
        {
            if (SetProperty(ref _selectedKindFilter, value))
            {
                RefreshVisibleItems();
            }
        }
    }

    public int UnreadCount
    {
        get => _unreadCount;
        private set
        {
            if (!SetProperty(ref _unreadCount, value))
            {
                return;
            }
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(BadgeText));
            MarkAllReadCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasUnread => UnreadCount > 0;

    public string BadgeText => UnreadCount switch
    {
        <= 0 => string.Empty,
        <= 99 => UnreadCount.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
        _ => "99+"
    };

    public async Task InitializeAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AppNotification> recent =
            await _repository.GetRecentAsync(
                RecentLimit,
                cancellationToken);
        int unreadCount = await _repository.GetUnreadCountAsync(
            cancellationToken);

        _allItems.Clear();
        foreach (AppNotification notification in recent)
        {
            _allItems.Add(notification);
        }
        RefreshVisibleItems();
        UnreadCount = unreadCount;
        MarkReadCommand.NotifyCanExecuteChanged();
    }

    private void OnNotificationReceived(
        AppNotification notification)
    {
        if (_synchronizationContext is not null &&
            !ReferenceEquals(
                SynchronizationContext.Current,
                _synchronizationContext))
        {
            _synchronizationContext.Post(
                _ => AcceptNotification(notification),
                null);
            return;
        }

        AcceptNotification(notification);
    }

    private void AcceptNotification(
        AppNotification notification)
    {
        int existingIndex = FindAllIndex(notification.Id);
        bool wasUnread = existingIndex >= 0 &&
            !_allItems[existingIndex].IsRead;
        if (existingIndex >= 0)
        {
            _allItems.RemoveAt(existingIndex);
        }

        int insertionIndex = 0;
        while (insertionIndex < _allItems.Count &&
               Compare(_allItems[insertionIndex], notification) <= 0)
        {
            insertionIndex++;
        }
        _allItems.Insert(insertionIndex, notification);
        while (_allItems.Count > RecentLimit)
        {
            _allItems.RemoveAt(_allItems.Count - 1);
        }
        RefreshVisibleItems();

        bool isUnread = !notification.IsRead;
        if (!wasUnread && isUnread)
        {
            UnreadCount++;
        }
        else if (wasUnread && !isUnread)
        {
            UnreadCount = Math.Max(0, UnreadCount - 1);
        }
        MarkReadCommand.NotifyCanExecuteChanged();
    }

    private async Task MarkReadAsync(
        AppNotification? notification,
        CancellationToken cancellationToken)
    {
        if (notification is null || notification.IsRead)
        {
            return;
        }

        DateTimeOffset readAt = _timeProvider.GetUtcNow();
        bool updated = await _repository.MarkReadAsync(
            notification.Id,
            readAt,
            cancellationToken);
        if (!updated)
        {
            return;
        }

        int index = FindAllIndex(notification.Id);
        if (index >= 0 && !_allItems[index].IsRead)
        {
            _allItems[index] =
                _allItems[index] with { ReadAt = readAt };
            RefreshVisibleItems();
            UnreadCount = Math.Max(0, UnreadCount - 1);
        }
        MarkReadCommand.NotifyCanExecuteChanged();
    }

    private async Task MarkAllReadAsync(
        CancellationToken cancellationToken)
    {
        if (UnreadCount <= 0)
        {
            return;
        }

        DateTimeOffset readAt = _timeProvider.GetUtcNow();
        await _repository.MarkAllReadAsync(
            readAt,
            cancellationToken);
        for (int index = 0; index < _allItems.Count; index++)
        {
            if (!_allItems[index].IsRead)
            {
                _allItems[index] =
                    _allItems[index] with { ReadAt = readAt };
            }
        }
        RefreshVisibleItems();
        UnreadCount = 0;
        MarkReadCommand.NotifyCanExecuteChanged();
    }

    private int FindAllIndex(string id)
    {
        for (int index = 0; index < _allItems.Count; index++)
        {
            if (string.Equals(
                    _allItems[index].Id,
                    id,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private void RefreshVisibleItems()
    {
        Items.Clear();
        foreach (AppNotification notification in _allItems)
        {
            if (MatchesFilter(notification.Kind))
            {
                Items.Add(notification);
            }
        }
        OnPropertyChanged(nameof(Items));
        MarkReadCommand.NotifyCanExecuteChanged();
    }

    private bool MatchesFilter(AppNotificationKind kind) =>
        SelectedKindFilter switch
        {
            AppNotificationKindFilter.All => true,
            AppNotificationKindFilter.ContentMatch =>
                kind == AppNotificationKind.ContentMatch,
            AppNotificationKindFilter.SystemHealth =>
                kind == AppNotificationKind.SystemHealth,
            AppNotificationKindFilter.TaskCompleted =>
                kind == AppNotificationKind.TaskCompleted,
            _ => false
        };

    private static int Compare(
        AppNotification left,
        AppNotification right)
    {
        int timestamp = right.CreatedAt.CompareTo(left.CreatedAt);
        return timestamp != 0
            ? timestamp
            : string.CompareOrdinal(left.Id, right.Id);
    }
}
