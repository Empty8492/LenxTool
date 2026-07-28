namespace LenxTool.Core.Models;

public enum FeedSmartViewScope
{
    Active,
    All
}

public sealed record FeedSmartViewFilter(
    string? FeedId,
    string? CategoryId,
    EntryViewKind? ViewKind,
    FeedEntryReadFilter ReadFilter,
    bool FavoritesOnly,
    string? SearchText,
    int? PublishedWithinDays);

public sealed record FeedSmartView(
    string Id,
    int Version,
    string Name,
    int SortOrder,
    bool IsEnabled,
    FeedSmartViewFilter Filter);

public sealed record FeedSmartViewInput(
    string Name,
    int SortOrder,
    bool IsEnabled,
    FeedSmartViewFilter Filter);

public sealed record FeedSmartViewSnapshot(
    long ViewSetVersion,
    FeedSmartViewScope Scope,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? LastSyncedAt,
    IReadOnlyList<FeedSmartView> Views);

public enum FeedSmartViewSyncOutcome
{
    Updated,
    Unchanged,
    SkippedNotAuthenticated
}

public sealed record FeedSmartViewSyncResult(
    FeedSmartViewSyncOutcome Outcome,
    long ViewSetVersion,
    DateTimeOffset? SynchronizedAt);

public sealed record FeedSmartViewMutationResult(
    long ViewSetVersion,
    FeedSmartView? View,
    string? DeletedViewId = null);
