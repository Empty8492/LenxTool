using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedCatalogAdminService
{
    Task<long> CreateCategoryAsync(
        FeedCategoryInput input,
        long expectedCatalogVersion,
        CancellationToken cancellationToken);

    Task<long> UpdateCategoryAsync(
        string categoryId,
        FeedCategoryInput input,
        long expectedCatalogVersion,
        CancellationToken cancellationToken);

    Task<long> DeleteCategoryAsync(
        string categoryId,
        long expectedCatalogVersion,
        CancellationToken cancellationToken);

    Task<long> CreateFeedAsync(
        FeedCatalogItemInput input,
        long expectedCatalogVersion,
        CancellationToken cancellationToken);

    Task<long> UpdateFeedAsync(
        string feedId,
        FeedCatalogItemInput input,
        long expectedCatalogVersion,
        CancellationToken cancellationToken);

    Task<long> DeleteFeedAsync(
        string feedId,
        long expectedCatalogVersion,
        CancellationToken cancellationToken);
}
