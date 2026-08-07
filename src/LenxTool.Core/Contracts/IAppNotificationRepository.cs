using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IAppNotificationRepository
{
    Task<AppNotificationRegistration> RegisterAsync(
        AppNotification notification,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AppNotification>> GetRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken);

    Task<AppNotification?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken);

    Task<int> GetUnreadCountAsync(
        CancellationToken cancellationToken);

    Task<bool> MarkReadAsync(
        string id,
        DateTimeOffset readAt,
        CancellationToken cancellationToken);

    Task<int> MarkAllReadAsync(
        DateTimeOffset readAt,
        CancellationToken cancellationToken);
}
