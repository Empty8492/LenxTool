namespace LenxTool.Core.Models;

public enum FeedCatalogBatchOperationType
{
    CreateCategory,
    PatchCategory,
    DeleteCategory,
    CreateFeed,
    PatchFeed,
    DeleteFeed
}

public sealed record FeedCatalogBatchOperation(
    string OperationId,
    FeedCatalogBatchOperationType Type,
    string? CategoryId = null,
    string? FeedId = null,
    FeedCategoryInput? CategoryInput = null,
    FeedCatalogItemInput? FeedInput = null,
    string? CategoryOperationId = null);

public sealed record FeedCatalogBatchOperationResult(
    string OperationId,
    string ResourceType,
    string ResourceId);

public sealed record FeedCatalogBatchResult(
    long CatalogVersion,
    IReadOnlyList<FeedCatalogBatchOperationResult> Results);
