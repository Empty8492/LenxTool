using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public interface IAppNotificationInbox
{
    event Action<AppNotification>? NotificationReceived;

    void Publish(AppNotification notification);
}

public sealed class AppNotificationInbox : IAppNotificationInbox
{
    public event Action<AppNotification>? NotificationReceived;

    public void Publish(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Delegate[] handlers =
            NotificationReceived?.GetInvocationList() ?? [];
        foreach (Action<AppNotification> handler
                 in handlers.Cast<Action<AppNotification>>())
        {
            try
            {
                handler(notification);
            }
            catch
            {
                // A UI subscriber cannot invalidate a durable notification.
            }
        }
    }
}
