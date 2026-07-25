using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAutomationLocalActionService
{
    Task<FeedAutomationLocalActionResult> ExecuteAsync(
        FeedAutomationActionLease action,
        CancellationToken cancellationToken);
}
