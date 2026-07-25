using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAutomationActionQueueRepository
{
    Task<IReadOnlyList<FeedAutomationActionLease>> ClaimDueAsync(
        DateTimeOffset now,
        IReadOnlyCollection<FeedAutomationActionType> actionTypes,
        int maximumCount,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        FeedAutomationActionLease action,
        FeedAutomationActionRunOutcome outcome,
        string? errorCode,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task ScheduleRetryAsync(
        FeedAutomationActionLease action,
        string errorCode,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        FeedAutomationActionLease action,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken);
}
