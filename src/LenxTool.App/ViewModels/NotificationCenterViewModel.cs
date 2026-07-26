using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed class NotificationCenterViewModel : ObservableObject
{
    private const int RecentLimit = 50;
    private readonly IAppNotificationRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _synchronizationContext;
    private bool _isOpen;
    private int _unreadCount;

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
    public RelayCommand ToggleCommand { get; }
    public AsyncRelayCommand<AppNotification> MarkReadCommand { get; }
    public AsyncRelayCommand MarkAllReadCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
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

        Items.Clear();
        foreach (AppNotification notification in recent)
        {
            Items.Add(notification);
        }
        UnreadCount = unreadCount;
        OnPropertyChanged(nameof(Items));
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
        int existingIndex = FindIndex(notification.Id);
        bool wasUnread = existingIndex >= 0 &&
            !Items[existingIndex].IsRead;
        if (existingIndex >= 0)
        {
            Items.RemoveAt(existingIndex);
        }

        int insertionIndex = 0;
        while (insertionIndex < Items.Count &&
               Compare(Items[insertionIndex], notification) <= 0)
        {
            insertionIndex++;
        }
        Items.Insert(insertionIndex, notification);
        while (Items.Count > RecentLimit)
        {
            Items.RemoveAt(Items.Count - 1);
        }

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

        int index = FindIndex(notification.Id);
        if (index >= 0 && !Items[index].IsRead)
        {
            Items[index] = Items[index] with { ReadAt = readAt };
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
        for (int index = 0; index < Items.Count; index++)
        {
            if (!Items[index].IsRead)
            {
                Items[index] = Items[index] with { ReadAt = readAt };
            }
        }
        UnreadCount = 0;
        MarkReadCommand.NotifyCanExecuteChanged();
    }

    private int FindIndex(string id)
    {
        for (int index = 0; index < Items.Count; index++)
        {
            if (string.Equals(
                    Items[index].Id,
                    id,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

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
