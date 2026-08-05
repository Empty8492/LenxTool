namespace LenxTool.Core.Models;

/// <summary>
/// 本地计划窗口的持久化生命周期。Pending 只表示已落盘但当前没有持有者，
/// Running 必须由租约令牌保护，终态不会再次领取。
/// </summary>
public enum LocalScheduleRunStatus
{
    Pending,
    Running,
    Completed,
    Cancelled
}

/// <summary>
/// 可供历史和诊断使用的最小窗口快照，不暴露租约令牌或计划执行内容。
/// </summary>
public sealed record LocalScheduleRun(
    string ScheduleId,
    DateTimeOffset ScheduledForUtc,
    LocalScheduleRunStatus Status,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>
/// 调度处理器领取一个逻辑窗口后得到的所有权证明。
/// 相同计划和计划时刻组成窗口身份，令牌用于拒绝崩溃前旧处理器的迟到提交。
/// </summary>
public sealed record LocalScheduleRunLease(
    string ScheduleId,
    DateTimeOffset ScheduledForUtc,
    int AttemptCount,
    string LeaseToken);
