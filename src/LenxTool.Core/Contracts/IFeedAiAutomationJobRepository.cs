using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAiAutomationJobRepository
{
    Task<int> EnqueueAsync(
        string feedId,
        IReadOnlyList<FeedEntry> entries,
        ResolvedFeedAiPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FeedAiAutomationJob>> ClaimDueAsync(
        DateTimeOffset now,
        int maximumCount,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> TryReserveDailyEntryAsync(
        DateOnly usageDate,
        string feedId,
        string entryId,
        int dailyEntryLimit,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        FeedAiAutomationJob job,
        FeedAiAutomationJobOutcome outcome,
        string? errorCode,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task ScheduleRetryAsync(
        FeedAiAutomationJob job,
        string errorCode,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        FeedAiAutomationJob job,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken);
}
