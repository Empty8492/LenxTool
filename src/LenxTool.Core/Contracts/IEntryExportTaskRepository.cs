using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IEntryExportTaskRepository
{
    Task<EntryExportEnqueueResult> EnqueueAsync(
        EntryExportRequest request,
        DateTimeOffset enqueuedAt,
        CancellationToken cancellationToken);

    Task<EntryExportTaskLease?> ClaimDueAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> RenewLeaseAsync(
        EntryExportTaskLease task,
        DateTimeOffset renewedAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken);

    Task<bool> IsCancellationRequestedAsync(
        EntryExportTaskLease task,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        EntryExportTaskLease task,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task FailAsync(
        EntryExportTaskLease task,
        EntryExportTaskErrorCode errorCode,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken);

    Task ScheduleRetryAsync(
        EntryExportTaskLease task,
        EntryExportTaskErrorCode errorCode,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken);

    Task CancelClaimedAsync(
        EntryExportTaskLease task,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        EntryExportTaskLease task,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken);

    Task<EntryExportCancellationResult> RequestCancellationAsync(
        string idempotencyKey,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken);

    Task<EntryExportTask?> GetAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntryExportTask>> GetRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken);
}
