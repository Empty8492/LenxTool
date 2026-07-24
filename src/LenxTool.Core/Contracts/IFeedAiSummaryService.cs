using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAiSummaryService
{
    Task<FeedAiResult> SummarizeAsync(
        FeedAiSummaryInput input,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FeedAiSummaryBatchItem>> SummarizeBatchAsync(
        IReadOnlyList<FeedAiSummaryInput> inputs,
        CancellationToken cancellationToken);
}
