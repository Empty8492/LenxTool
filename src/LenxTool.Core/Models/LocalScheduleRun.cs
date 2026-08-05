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

/// <summary>
/// 交给具体计划处理器的最小执行上下文。租约令牌只属于调度内核，
/// 不向任务实现暴露，避免业务代码绕过所有权检查提交窗口状态。
/// </summary>
public sealed record LocalScheduleExecution(
    string ScheduleId,
    DateTimeOffset ScheduledForUtc,
    int AttemptCount);

/// <summary>
/// 本地计划处理器的恢复、租约和轮询边界。错过阈值表示计划时刻
/// 严格早于“当前时间减去宽限期”时才应用 RunOnce/Skip 策略。
/// </summary>
public sealed record LocalScheduleProcessorOptions(
    TimeSpan LeaseDuration,
    TimeSpan MissedRunGracePeriod,
    TimeSpan PollInterval)
{
    public static LocalScheduleProcessorOptions Default { get; } = new(
        LeaseDuration: TimeSpan.FromMinutes(10),
        MissedRunGracePeriod: TimeSpan.FromMinutes(5),
        PollInterval: TimeSpan.FromSeconds(10));
}
