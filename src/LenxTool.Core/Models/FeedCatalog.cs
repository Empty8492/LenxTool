namespace LenxTool.Core.Models;

public enum FeedCatalogScope
{
    Active,
    All
}

public enum FeedViewKind
{
    Article,
    Picture,
    Audio,
    Video,
    Notification
}

public sealed record FeedCatalogState(
    long Version,
    FeedCatalogScope Scope,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? LastSyncedAt);

public sealed record FeedCatalogSnapshot(
    FeedCatalogState State,
    IReadOnlyList<FeedCategory> Categories,
    IReadOnlyList<FeedCatalogItem> Feeds);

public sealed record FeedCategory(
    string Id,
    string Name,
    string NormalizedName,
    int SortOrder,
    bool IsEnabled,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FeedCatalogItem(
    string Id,
    string OriginalUrl,
    string NormalizedUrl,
    string DisplayName,
    string? SiteUrl,
    string? CategoryId,
    FeedViewKind ViewKind,
    int RefreshIntervalMinutes,
    int SortOrder,
    bool IsEnabled,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FeedFetchState(
    string FeedId,
    string? ETag,
    string? LastModified,
    DateTimeOffset? NextFetchAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    int ConsecutiveFailures,
    string? ErrorCode,
    DateTimeOffset UpdatedAt);
