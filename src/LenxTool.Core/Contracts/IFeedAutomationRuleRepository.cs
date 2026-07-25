using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAutomationRuleRepository
{
    Task<FeedAutomationRuleSnapshot> GetAsync(
        CancellationToken cancellationToken);

    Task ReplaceAsync(
        FeedAutomationRuleSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<bool> MarkSynchronizedAsync(
        long expectedRuleSetVersion,
        DateTimeOffset synchronizedAt,
        CancellationToken cancellationToken);
}
