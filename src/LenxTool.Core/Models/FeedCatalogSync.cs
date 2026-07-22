using LenxTool.Core.Errors;

namespace LenxTool.Core.Models;

public enum FeedCatalogSyncOutcome
{
    Updated,
    Unchanged,
    SkippedNotAuthenticated
}

public sealed record FeedCatalogSyncResult(
    FeedCatalogSyncOutcome Outcome,
    long Version,
    DateTimeOffset? SynchronizedAt);

public sealed record FeedCatalogSyncStatus(
    bool IsSynchronizing,
    long Version,
    FeedCatalogScope Scope,
    DateTimeOffset? LastSynchronizedAt,
    bool IsStale,
    int ConsecutiveFailures,
    DateTimeOffset? NextAttemptAt,
    AppError? Error);

public sealed class FeedCatalogSyncStatusChangedEventArgs(FeedCatalogSyncStatus status) : EventArgs
{
    public FeedCatalogSyncStatus Status { get; } = status;
}
