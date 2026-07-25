using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAutomationRunRepository
{
    Task<FeedAutomationStageResult> StageAsync(
        FeedAutomationPlan plan,
        DateTimeOffset stagedAt,
        CancellationToken cancellationToken);

    Task<FeedAutomationRunSnapshot> GetAsync(
        string entryId,
        CancellationToken cancellationToken);
}
