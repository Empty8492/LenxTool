using LenxTool.Core.Models;
using LenxTool.Core.Scheduling;

namespace LenxTool.Core.Tests.Scheduling;

public sealed class LocalScheduleCalculatorTests
{
    [Fact]
    public void OnceReturnsOnlyTheStrictlyFutureOccurrence()
    {
        LocalScheduleDefinition schedule = Once(
            "UTC",
            new DateOnly(2026, 8, 5),
            new TimeOnly(9, 30));

        Assert.Equal(
            Utc(2026, 8, 5, 9, 30),
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                schedule,
                Utc(2026, 8, 5, 9, 29)));
        Assert.Null(LocalScheduleCalculator.GetNextOccurrenceUtc(
            schedule,
            Utc(2026, 8, 5, 9, 30)));
    }

    [Fact]
    public void DailyUsesTheCurrentLocalDateUntilItsOccurrencePasses()
    {
        var schedule = new LocalScheduleDefinition(
            LocalScheduleFrequency.Daily,
            "UTC",
            new TimeOnly(8, 0));

        Assert.Equal(
            Utc(2026, 8, 5, 8, 0),
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                schedule,
                Utc(2026, 8, 5, 7, 59)));
        Assert.Equal(
            Utc(2026, 8, 6, 8, 0),
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                schedule,
                Utc(2026, 8, 5, 8, 0)));
    }

    [Fact]
    public void WeeklyAdvancesToTheConfiguredLocalWeekday()
    {
        var schedule = new LocalScheduleDefinition(
            LocalScheduleFrequency.Weekly,
            "UTC",
            new TimeOnly(7, 15),
            WeeklyDay: DayOfWeek.Monday);

        Assert.Equal(
            Utc(2026, 8, 10, 7, 15),
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                schedule,
                Utc(2026, 8, 5, 12, 0)));
    }

    [Fact]
    public void MonthlyClampsToTheLastDayOfShortMonths()
    {
        var schedule = new LocalScheduleDefinition(
            LocalScheduleFrequency.Monthly,
            "UTC",
            new TimeOnly(18, 0),
            MonthlyDay: 31);

        Assert.Equal(
            Utc(2027, 2, 28, 18, 0),
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                schedule,
                Utc(2027, 1, 31, 18, 0)));
    }

    [Fact]
    public void SpringGapMovesToTheFirstValidLocalMinute()
    {
        string zoneId = EasternTimeZoneId();
        var schedule = new LocalScheduleDefinition(
            LocalScheduleFrequency.Daily,
            zoneId,
            new TimeOnly(2, 30));

        Assert.Equal(
            Utc(2026, 3, 8, 7, 0),
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                schedule,
                Utc(2026, 3, 8, 5, 0)));
    }

    [Fact]
    public void FallOverlapUsesTheEarlierInstantOnlyOnce()
    {
        string zoneId = EasternTimeZoneId();
        var schedule = new LocalScheduleDefinition(
            LocalScheduleFrequency.Daily,
            zoneId,
            new TimeOnly(1, 30));

        Assert.Equal(
            Utc(2026, 11, 1, 5, 30),
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                schedule,
                Utc(2026, 11, 1, 4, 0)));
        Assert.Equal(
            Utc(2026, 11, 2, 6, 30),
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                schedule,
                Utc(2026, 11, 1, 5, 45)));
    }

    [Fact]
    public void InvalidFrequencyFieldsAndTimeZoneAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                new(
                    LocalScheduleFrequency.Daily,
                    "UTC",
                    new TimeOnly(8, 0),
                    WeeklyDay: DayOfWeek.Monday),
                Utc(2026, 8, 5, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                new(
                    LocalScheduleFrequency.Monthly,
                    "UTC",
                    new TimeOnly(8, 0),
                    MonthlyDay: 32),
                Utc(2026, 8, 5, 0, 0)));
        Assert.Throws<ArgumentException>(() =>
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                new(
                    LocalScheduleFrequency.Monthly,
                    "UTC",
                    new TimeOnly(8, 0)),
                Utc(2026, 8, 5, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                new(
                    LocalScheduleFrequency.Weekly,
                    "UTC",
                    new TimeOnly(8, 0),
                    WeeklyDay: (DayOfWeek)7),
                Utc(2026, 8, 5, 0, 0)));
        Assert.Throws<TimeZoneNotFoundException>(() =>
            LocalScheduleCalculator.GetNextOccurrenceUtc(
                new(
                    LocalScheduleFrequency.Daily,
                    "LenxTool/Unknown-Time-Zone",
                    new TimeOnly(8, 0)),
                Utc(2026, 8, 5, 0, 0)));
    }

    private static LocalScheduleDefinition Once(
        string timeZoneId,
        DateOnly date,
        TimeOnly time) =>
        new(
            LocalScheduleFrequency.Once,
            timeZoneId,
            time,
            OnceDate: date);

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static string EasternTimeZoneId() =>
        OperatingSystem.IsWindows()
            ? "Eastern Standard Time"
            : "America/New_York";
}
