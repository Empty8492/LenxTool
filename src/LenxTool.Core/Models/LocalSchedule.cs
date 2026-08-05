namespace LenxTool.Core.Models;

/// <summary>
/// 本地计划只描述日历重复方式；实际执行时间始终由计划保存的时区换算为 UTC。
/// </summary>
public enum LocalScheduleFrequency
{
    Once,
    Daily,
    Weekly,
    Monthly
}

/// <summary>
/// 可持久化的本地计划定义。不同频率只允许使用各自对应的可选字段，
/// 避免同一记录同时表达多种互相冲突的日历语义。
/// </summary>
public sealed record LocalScheduleDefinition(
    LocalScheduleFrequency Frequency,
    string TimeZoneId,
    TimeOnly LocalTime,
    DateOnly? OnceDate = null,
    DayOfWeek? WeeklyDay = null,
    int? MonthlyDay = null);

/// <summary>
/// 应用在计划时间之外启动时的恢复策略。RunOnce 最多补一次，Skip 只推进游标；
/// 具体领取语义由后续持久执行片实现。
/// </summary>
public enum LocalScheduleMissedRunPolicy
{
    RunOnce,
    Skip
}

/// <summary>
/// 本地计划的持久化快照。禁用计划不保留待执行时间，重新启用时从变更时刻重算。
/// </summary>
public sealed record LocalScheduledTask(
    string Id,
    LocalScheduleDefinition Schedule,
    LocalScheduleMissedRunPolicy MissedRunPolicy,
    bool IsEnabled,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
