namespace LenxTool.Core.Models;

public sealed record FeedCategoryInput(
    string Name,
    int SortOrder,
    bool IsEnabled,
    FeedAiPolicy? AiPolicy = null);

public sealed record FeedCatalogItemInput(
    string OriginalUrl,
    string DisplayName,
    string? SiteUrl,
    string? CategoryId,
    FeedViewKind ViewKind,
    int RefreshIntervalMinutes,
    int SortOrder,
    bool IsEnabled,
    FeedFullTextPolicy FullTextPolicy = FeedFullTextPolicy.None,
    FeedAiPolicy? AiPolicy = null,
    bool IsViewKindExplicit = false);
