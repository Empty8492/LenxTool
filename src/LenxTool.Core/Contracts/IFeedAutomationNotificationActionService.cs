using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAutomationNotificationActionService
{
    Task<FeedAutomationNotificationActionResult> ExecuteAsync(
        FeedAutomationActionLease action,
        CancellationToken cancellationToken);
}
