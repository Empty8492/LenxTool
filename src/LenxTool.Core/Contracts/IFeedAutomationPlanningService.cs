using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAutomationPlanningService
{
    Task<FeedAutomationPlanningResult> StageAsync(
        FeedCatalogItem feed,
        IReadOnlyList<FeedEntry> entries,
        CancellationToken cancellationToken);
}
