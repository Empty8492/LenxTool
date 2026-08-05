using LenxTool.Core.Models;

namespace LenxTool.Core.Scheduling;

/// <summary>
/// 把本地日历计划换算为严格晚于给定时刻的下一次 UTC 时间。
/// 春季缺口移动到第一个有效本地分钟，秋季重叠固定选择较早的 UTC 时刻，
/// 从而让每个本地日历窗口最多产生一次计划时间。
/// </summary>
public static class LocalScheduleCalculator
{
    private const int MaximumTimeZoneIdLength = 128;
    private const int MaximumInvalidLocalMinutes = 48 * 60;

    public static DateTimeOffset? GetNextOccurrenceUtc(
        LocalScheduleDefinition schedule,
        DateTimeOffset afterUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        Validate(schedule);

        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(
            schedule.TimeZoneId);
        DateTimeOffset normalizedAfter = afterUtc.ToUniversalTime();
        DateOnly localDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(normalizedAfter, timeZone).DateTime);

        return schedule.Frequency switch
        {
            LocalScheduleFrequency.Once => NextOnce(
                schedule,
                timeZone,
                normalizedAfter),
            LocalScheduleFrequency.Daily => NextDaily(
                schedule,
                timeZone,
                localDate,
                normalizedAfter),
            LocalScheduleFrequency.Weekly => NextWeekly(
                schedule,
                timeZone,
                localDate,
                normalizedAfter),
            LocalScheduleFrequency.Monthly => NextMonthly(
                schedule,
                timeZone,
                localDate,
                normalizedAfter),
            _ => throw new ArgumentOutOfRangeException(
                nameof(schedule),
                "计划频率无效。")
        };
    }

    private static DateTimeOffset? NextOnce(
        LocalScheduleDefinition schedule,
        TimeZoneInfo timeZone,
        DateTimeOffset afterUtc)
    {
        DateTimeOffset candidate = ResolveLocal(
            schedule.OnceDate!.Value,
            schedule.LocalTime,
            timeZone);
        return candidate > afterUtc ? candidate : null;
    }

    private static DateTimeOffset NextDaily(
        LocalScheduleDefinition schedule,
        TimeZoneInfo timeZone,
        DateOnly localDate,
        DateTimeOffset afterUtc)
    {
        DateTimeOffset candidate = ResolveLocal(
            localDate,
            schedule.LocalTime,
            timeZone);
        return candidate > afterUtc
            ? candidate
            : ResolveLocal(
                localDate.AddDays(1),
                schedule.LocalTime,
                timeZone);
    }

    private static DateTimeOffset NextWeekly(
        LocalScheduleDefinition schedule,
        TimeZoneInfo timeZone,
        DateOnly localDate,
        DateTimeOffset afterUtc)
    {
        int daysUntilTarget =
            ((int)schedule.WeeklyDay!.Value - (int)localDate.DayOfWeek + 7) % 7;
        DateOnly candidateDate = localDate.AddDays(daysUntilTarget);
        DateTimeOffset candidate = ResolveLocal(
            candidateDate,
            schedule.LocalTime,
            timeZone);
        return candidate > afterUtc
            ? candidate
            : ResolveLocal(
                candidateDate.AddDays(7),
                schedule.LocalTime,
                timeZone);
    }

    private static DateTimeOffset NextMonthly(
        LocalScheduleDefinition schedule,
        TimeZoneInfo timeZone,
        DateOnly localDate,
        DateTimeOffset afterUtc)
    {
        DateOnly candidateDate = MonthlyDate(
            localDate.Year,
            localDate.Month,
            schedule.MonthlyDay!.Value);
        DateTimeOffset candidate = ResolveLocal(
            candidateDate,
            schedule.LocalTime,
            timeZone);
        if (candidate > afterUtc)
        {
            return candidate;
        }

        DateOnly nextMonth = new DateOnly(localDate.Year, localDate.Month, 1)
            .AddMonths(1);
        return ResolveLocal(
            MonthlyDate(
                nextMonth.Year,
                nextMonth.Month,
                schedule.MonthlyDay.Value),
            schedule.LocalTime,
            timeZone);
    }

    private static DateOnly MonthlyDate(int year, int month, int requestedDay) =>
        new(
            year,
            month,
            Math.Min(requestedDay, DateTime.DaysInMonth(year, month)));

    private static DateTimeOffset ResolveLocal(
        DateOnly date,
        TimeOnly time,
        TimeZoneInfo timeZone)
    {
        DateTime local = date.ToDateTime(time, DateTimeKind.Unspecified);
        int shiftedMinutes = 0;
        while (timeZone.IsInvalidTime(local)
               && shiftedMinutes < MaximumInvalidLocalMinutes)
        {
            local = local.AddMinutes(1);
            shiftedMinutes++;
        }

        if (timeZone.IsInvalidTime(local))
        {
            throw new InvalidTimeZoneException(
                "无法在时区中找到计划时间之后的有效本地时刻。");
        }

        TimeSpan offset = timeZone.IsAmbiguousTime(local)
            // 较大的 UTC offset 对应重叠窗口中的较早 UTC 时刻。
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static void Validate(LocalScheduleDefinition schedule)
    {
        if (!Enum.IsDefined(schedule.Frequency))
        {
            throw new ArgumentOutOfRangeException(
                nameof(schedule),
                "计划频率无效。");
        }
        if (string.IsNullOrWhiteSpace(schedule.TimeZoneId)
            || schedule.TimeZoneId.Length > MaximumTimeZoneIdLength
            || !string.Equals(
                schedule.TimeZoneId,
                schedule.TimeZoneId.Trim(),
                StringComparison.Ordinal)
            || schedule.TimeZoneId.Any(char.IsControl))
        {
            throw new ArgumentException("时区标识无效。", nameof(schedule));
        }

        switch (schedule.Frequency)
        {
            case LocalScheduleFrequency.Once:
                RequireShape(
                    schedule.OnceDate is not null
                    && schedule.WeeklyDay is null
                    && schedule.MonthlyDay is null,
                    schedule);
                break;
            case LocalScheduleFrequency.Daily:
                RequireShape(
                    schedule.OnceDate is null
                    && schedule.WeeklyDay is null
                    && schedule.MonthlyDay is null,
                    schedule);
                break;
            case LocalScheduleFrequency.Weekly:
                RequireShape(
                    schedule.OnceDate is null
                    && schedule.WeeklyDay is not null
                    && schedule.MonthlyDay is null,
                    schedule);
                if (!Enum.IsDefined(schedule.WeeklyDay!.Value))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(schedule),
                        "每周执行日无效。");
                }
                break;
            case LocalScheduleFrequency.Monthly:
                RequireShape(
                    schedule.OnceDate is null
                    && schedule.WeeklyDay is null
                    && schedule.MonthlyDay is not null,
                    schedule);
                if (schedule.MonthlyDay is < 1 or > 31)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(schedule),
                        "每月执行日必须介于 1 到 31。");
                }
                break;
        }
    }

    private static void RequireShape(
        bool condition,
        LocalScheduleDefinition schedule)
    {
        if (!condition)
        {
            throw new ArgumentException(
                "计划字段与频率不匹配。",
                nameof(schedule));
        }
    }
}
