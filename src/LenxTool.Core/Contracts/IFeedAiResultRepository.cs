using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAiResultRepository
{
    Task UpsertAsync(
        FeedAiResult result,
        CancellationToken cancellationToken);

    Task<FeedAiResult?> GetCurrentAsync(
        FeedAiCacheKey key,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FeedAiResult>> GetHistoryAsync(
        string entryId,
        FeedAiTaskType taskType,
        string targetLanguage,
        int limit,
        CancellationToken cancellationToken);
}
