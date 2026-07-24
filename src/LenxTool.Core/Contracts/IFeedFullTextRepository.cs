using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedFullTextRepository
{
    Task<IReadOnlyList<FeedFullTextWorkItem>> ClaimBackgroundAsync(
        DateTimeOffset now,
        int maximumCount,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<FeedFullTextWorkItem?> ClaimOnOpenAsync(
        string entryId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<FeedFullTextContent?> GetContentAsync(
        string entryId,
        CancellationToken cancellationToken);

    Task SaveContentAsync(
        FeedFullTextWorkItem workItem,
        ArticleContentResult article,
        DateTimeOffset extractedAt,
        CancellationToken cancellationToken);

    Task ScheduleRetryAsync(
        FeedFullTextWorkItem workItem,
        string errorCode,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken);

    Task BlockAsync(
        FeedFullTextWorkItem workItem,
        string errorCode,
        DateTimeOffset blockedAt,
        DateTimeOffset hostRetryAt,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        FeedFullTextWorkItem workItem,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken);
}
