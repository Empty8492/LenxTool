using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedCatalogRepository
{
    Task ReplaceAsync(
        FeedCatalogSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<FeedCatalogSnapshot?> GetCatalogAsync(
        FeedCatalogScope scope,
        CancellationToken cancellationToken);

    Task MarkSynchronizedAsync(
        long expectedVersion,
        DateTimeOffset synchronizedAt,
        CancellationToken cancellationToken);

    Task<FeedCatalogState> GetStateAsync(CancellationToken cancellationToken);
}
