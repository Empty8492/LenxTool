using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAutomationMediaActionService
{
    Task<FeedAutomationMediaActionResult> ExecuteAsync(
        FeedAutomationActionLease action,
        CancellationToken cancellationToken);
}
