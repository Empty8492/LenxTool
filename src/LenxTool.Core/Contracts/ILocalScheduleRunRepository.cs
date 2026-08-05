using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface ILocalScheduleRunRepository
{
    /// <summary>
    /// 先接管已释放或租约已过期的窗口，再原子领取最早到期计划。
    /// scheduled_for 严格早于 missedBeforeUtc 才算漏跑：RunOnce 只补当前
    /// 持久游标代表的一次，Skip 只推进到 nowUtc 之后，不写伪历史。
    /// </summary>
    Task<LocalScheduleRunLease?> ClaimDueAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset missedBeforeUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> RenewLeaseAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    Task CancelAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset releasedAtUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LocalScheduleRun>> GetRecentAsync(
        string scheduleId,
        int maximumCount,
        CancellationToken cancellationToken);
}
