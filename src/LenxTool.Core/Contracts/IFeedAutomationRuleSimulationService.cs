using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAutomationRuleSimulationService
{
    Task<FeedAutomationSimulationResult> SimulateAsync(
        FeedAutomationRuleDefinition definition,
        int maximumEntries,
        CancellationToken cancellationToken);
}
