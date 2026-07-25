using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAutomationAiActionService
{
    Task<FeedAutomationAiActionResult> ExecuteAsync(
        FeedAutomationActionLease action,
        CancellationToken cancellationToken);
}
