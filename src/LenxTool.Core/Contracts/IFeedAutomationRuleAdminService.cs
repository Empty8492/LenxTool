using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAutomationRuleAdminService
{
    Task<FeedAutomationRuleSnapshot> GetAllAsync(
        CancellationToken cancellationToken);

    Task<FeedAutomationRuleMutationResult> CreateAsync(
        FeedAutomationRuleDefinition definition,
        long expectedRuleSetVersion,
        CancellationToken cancellationToken);

    Task<FeedAutomationRuleMutationResult> UpdateAsync(
        string ruleId,
        FeedAutomationRuleDefinition definition,
        long expectedRuleSetVersion,
        CancellationToken cancellationToken);
}
