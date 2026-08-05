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
