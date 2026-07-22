using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedCatalogBatchService
{
    Task<FeedCatalogBatchResult> ApplyAsync(
        IReadOnlyList<FeedCatalogBatchOperation> operations,
        long expectedCatalogVersion,
        CancellationToken cancellationToken);
}
