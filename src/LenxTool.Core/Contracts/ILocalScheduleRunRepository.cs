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

    /// <summary>
    /// 只在拥有已注册幂等处理器的计划中领取窗口，避免未知计划阻塞
    /// 其他可执行计划，或被错误地当作已成功处理。
    /// </summary>
    Task<LocalScheduleRunLease?> ClaimDueAsync(
        IReadOnlyCollection<string> eligibleScheduleIds,
        DateTimeOffset nowUtc,
        DateTimeOffset missedBeforeUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    /// <summary>
    /// 计划在窗口领取之后发生任何持久变更时，旧窗口必须合作取消；
    /// 即使计划先禁用后快速重新启用，也不能恢复旧 owner。
    /// </summary>
    Task<bool> IsCancellationRequestedAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset observedAtUtc,
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
        CancellationToken cancellationToken,
        DateTimeOffset? retryNotBeforeUtc = null);

    Task<IReadOnlyList<LocalScheduleRun>> GetRecentAsync(
        string scheduleId,
        int maximumCount,
        CancellationToken cancellationToken);
}
