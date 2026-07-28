using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IAppNotificationPublisher
{
    Task<AppNotificationRegistration> PublishAsync(
        AppNotificationDraft draft,
        CancellationToken cancellationToken);
}
