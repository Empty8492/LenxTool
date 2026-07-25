using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAiAutomationQueueService
{
    Task EnqueueAsync(
        string feedId,
        IReadOnlyList<FeedEntry> entries,
        CancellationToken cancellationToken);

    Task<int> ProcessBackgroundBatchAsync(CancellationToken cancellationToken);
}
