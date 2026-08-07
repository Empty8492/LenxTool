using System.IO;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public interface IAppNotificationNavigationService
{
    event EventHandler<AppNotificationOpenedEventArgs>? NotificationOpened;

    Task<AppNotification?> OpenAsync(
        string notificationId,
        CancellationToken cancellationToken);
}

public sealed class AppNotificationOpenedEventArgs(
    AppNotification notification,
    bool becameRead = false) : EventArgs
{
    public AppNotification Notification { get; } = notification ??
        throw new ArgumentNullException(nameof(notification));

    public bool BecameRead { get; } = becameRead;
}

public sealed class AppNotificationNavigationService(
    IAppNotificationRepository repository,
    IAppNavigationService navigation,
    TimeProvider timeProvider) : IAppNotificationNavigationService
{
    public event EventHandler<AppNotificationOpenedEventArgs>?
        NotificationOpened;

    public async Task<AppNotification?> OpenAsync(
        string notificationId,
        CancellationToken cancellationToken)
    {
        if (!WindowsNotificationActivation.IsValidNotificationId(
                notificationId))
        {
            return null;
        }

        AppNotification? notification = await repository.GetByIdAsync(
            notificationId,
            cancellationToken).ConfigureAwait(false);
        if (notification is null)
        {
            return null;
        }

        AppNavigationRequest request;
        try
        {
            request = WindowsNotificationDeepLink.For(notification);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        await navigation.NavigateAsync(request, cancellationToken)
            .ConfigureAwait(false);
        AppNotification? opened;
        bool becameRead = false;
        if (notification.IsRead)
        {
            opened = notification;
        }
        else
        {
            DateTimeOffset readAt = timeProvider.GetUtcNow();
            bool updated = await repository.MarkReadAsync(
                notification.Id,
                readAt,
                cancellationToken).ConfigureAwait(false);
            becameRead = updated;
            opened = updated
                ? notification with { ReadAt = readAt }
                : await repository.GetByIdAsync(
                    notification.Id,
                    cancellationToken).ConfigureAwait(false);
        }

        if (opened is not null)
        {
            RaiseNotificationOpened(opened, becameRead);
        }
        return opened;
    }

    private void RaiseNotificationOpened(
        AppNotification notification,
        bool becameRead)
    {
        var eventArgs = new AppNotificationOpenedEventArgs(
            notification,
            becameRead);
        Delegate[] subscribers = NotificationOpened?.GetInvocationList() ?? [];
        foreach (Delegate subscriber in subscribers)
        {
            try
            {
                ((EventHandler<AppNotificationOpenedEventArgs>)subscriber)(
                    this,
                    eventArgs);
            }
            catch
            {
                // A stale UI projection must not undo trusted navigation or
                // the durable read transition.
            }
        }
    }
}
