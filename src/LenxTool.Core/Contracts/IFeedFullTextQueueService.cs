using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedFullTextQueueService
{
    Task<FeedFullTextContent?> FetchOnOpenAsync(
        string entryId,
        CancellationToken cancellationToken);

    Task<int> ProcessBackgroundBatchAsync(CancellationToken cancellationToken);
}
