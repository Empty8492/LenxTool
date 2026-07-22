namespace LenxTool.Core.Models;

public sealed record FeedRefreshTarget(
    FeedCatalogItem Feed,
    FeedFetchState? State);

public enum FeedRefreshOutcome
{
    Updated,
    NotModified,
    SkippedNotDue,
    SkippedUnavailable,
    Failed
}

public sealed record FeedRefreshResult(
    string FeedId,
    FeedRefreshOutcome Outcome,
    int ParsedEntryCount,
    DateTimeOffset? NextFetchAt,
    string? ErrorCode);

public sealed record FeedRefreshBatchResult(
    int Attempted,
    int Updated,
    int NotModified,
    int Failed);
