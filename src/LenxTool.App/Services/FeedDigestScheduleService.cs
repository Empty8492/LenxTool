using System.IO;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Core.Scheduling;

namespace LenxTool.App.Services;

/// <summary>
/// 配置更新先禁用旧代际，再把日历字段和版本化范围负载放进同一个仓储事务。
/// 多进程写入只能完整地由一个代际获胜；失败最多留下禁用计划，不会形成
/// “计划 A + 范围 B”的混合状态。
/// </summary>
public sealed class FeedDigestScheduleService(
    ILocalScheduledTaskRepository scheduledTasks,
    IFeedCatalogRepository catalogRepository,
    TimeProvider timeProvider) : IFeedDigestScheduleService
{
    public async Task<FeedDigestScheduleState> GetAsync(
        FeedDigestPeriod period,
        CancellationToken cancellationToken)
    {
        ValidatePeriod(period);
        string id = FeedDigestScheduleIds.For(period);
        LocalScheduledTask? task = await scheduledTasks.GetAsync(
            id,
            cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return new(
                period,
                new TimeOnly(8, 0),
                period is FeedDigestPeriod.Weekly
                    ? DayOfWeek.Monday
                    : null,
                TimeZoneInfo.Local.Id,
                false,
                null,
                FeedDigestScope.AllActive);
        }

        ValidateStoredTask(period, task);
        FeedDigestScope scope = FeedDigestScopePayload.Deserialize(
            task.Payload);
        return new(
            period,
            task.Schedule.LocalTime,
            task.Schedule.WeeklyDay,
            task.Schedule.TimeZoneId,
            task.IsEnabled,
            task.NextRunAtUtc,
            scope);
    }

    public async Task<FeedDigestScheduleState> SaveAsync(
        FeedDigestScheduleConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidatePeriod(configuration.Period);
        FeedDigestScope scope = FeedDigestScope.Normalize(configuration.Scope);
        LocalScheduleDefinition schedule = CreateSchedule(configuration);
        DateTimeOffset nowUtc = timeProvider.GetUtcNow().ToUniversalTime();

        // 在改变任何本地状态前完成时区和字段形状校验。只有启用计划才要求
        // 范围仍在 ACTIVE 目录；已下架的旧范围不能反过来阻止用户关闭任务。
        _ = LocalScheduleCalculator.GetNextOccurrenceUtc(schedule, nowUtc);
        if (configuration.IsEnabled)
        {
            await ValidateActiveScopeAsync(scope, cancellationToken)
                .ConfigureAwait(false);
        }

        string id = FeedDigestScheduleIds.For(configuration.Period);
        LocalScheduledTask? current = await scheduledTasks.GetAsync(
            id,
            cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            ValidateStoredTask(configuration.Period, current);
        }
        if (current?.IsEnabled == true)
        {
            DateTimeOffset disableAt = NextChangeTimestamp(
                nowUtc,
                current.UpdatedAtUtc);
            current = await scheduledTasks.SetEnabledAsync(
                    id,
                    false,
                    disableAt,
                    cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "摘要计划在禁用旧代际时消失。");
        }

        DateTimeOffset saveAt = NextChangeTimestamp(
            timeProvider.GetUtcNow().ToUniversalTime(),
            current?.UpdatedAtUtc);
        LocalScheduledTask saved = await scheduledTasks.SaveAsync(
            id,
            schedule,
            LocalScheduleMissedRunPolicy.RunOnce,
            configuration.IsEnabled,
            saveAt,
            cancellationToken,
            FeedDigestScopePayload.Serialize(scope)).ConfigureAwait(false);
        return new(
            configuration.Period,
            saved.Schedule.LocalTime,
            saved.Schedule.WeeklyDay,
            saved.Schedule.TimeZoneId,
            saved.IsEnabled,
            saved.NextRunAtUtc,
            scope);
    }

    private async Task ValidateActiveScopeAsync(
        FeedDigestScope scope,
        CancellationToken cancellationToken)
    {
        if (scope.FeedId is null && scope.CategoryId is null)
        {
            return;
        }
        FeedCatalogSnapshot snapshot = await catalogRepository.GetCatalogAsync(
                FeedCatalogScope.Active,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "尚未同步可用于摘要的 ACTIVE 订阅目录。");
        FeedCategory? category = scope.CategoryId is null
            ? null
            : snapshot.Categories.FirstOrDefault(item =>
                string.Equals(
                    item.Id,
                    scope.CategoryId,
                    StringComparison.Ordinal)
                && item.IsEnabled);
        if (scope.CategoryId is not null && category is null)
        {
            throw new ArgumentException(
                "摘要分类不在当前 ACTIVE 目录中。",
                nameof(scope));
        }
        FeedCatalogItem? feed = scope.FeedId is null
            ? null
            : snapshot.Feeds.FirstOrDefault(item =>
                string.Equals(
                    item.Id,
                    scope.FeedId,
                    StringComparison.Ordinal)
                && item.IsEnabled);
        if (scope.FeedId is not null && feed is null)
        {
            throw new ArgumentException(
                "摘要 Feed 不在当前 ACTIVE 目录中。",
                nameof(scope));
        }
        if (feed is not null
            && scope.CategoryId is not null
            && !string.Equals(
                feed.CategoryId,
                scope.CategoryId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "摘要 Feed 不属于所选分类。",
                nameof(scope));
        }
    }

    private static LocalScheduleDefinition CreateSchedule(
        FeedDigestScheduleConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.TimeZoneId)
            || !string.Equals(
                configuration.TimeZoneId,
                configuration.TimeZoneId.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "摘要时区无效。",
                nameof(configuration));
        }
        return configuration.Period switch
        {
            FeedDigestPeriod.Daily when configuration.WeeklyDay is null =>
                new(
                    LocalScheduleFrequency.Daily,
                    configuration.TimeZoneId,
                    configuration.LocalTime),
            FeedDigestPeriod.Weekly
                when configuration.WeeklyDay is { } weeklyDay
                     && Enum.IsDefined(weeklyDay) =>
                new(
                    LocalScheduleFrequency.Weekly,
                    configuration.TimeZoneId,
                    configuration.LocalTime,
                    WeeklyDay: weeklyDay),
            _ => throw new ArgumentException(
                "摘要周期字段不匹配。",
                nameof(configuration))
        };
    }

    private static void ValidateStoredTask(
        FeedDigestPeriod period,
        LocalScheduledTask task)
    {
        LocalScheduleFrequency expected = period switch
        {
            FeedDigestPeriod.Daily => LocalScheduleFrequency.Daily,
            FeedDigestPeriod.Weekly => LocalScheduleFrequency.Weekly,
            _ => throw new ArgumentOutOfRangeException(nameof(period))
        };
        if (!string.Equals(
                task.Id,
                FeedDigestScheduleIds.For(period),
                StringComparison.Ordinal)
            || task.Schedule.Frequency != expected
            || task.MissedRunPolicy != LocalScheduleMissedRunPolicy.RunOnce)
        {
            throw new InvalidDataException(
                "持久摘要计划与已发布处理器契约不一致。");
        }
    }

    private static DateTimeOffset NextChangeTimestamp(
        DateTimeOffset requestedUtc,
        DateTimeOffset? previousUtc)
    {
        DateTimeOffset normalized = requestedUtc.ToUniversalTime();
        return previousUtc is null || normalized > previousUtc.Value
            ? normalized
            : previousUtc.Value.AddTicks(1);
    }

    private static void ValidatePeriod(FeedDigestPeriod period)
    {
        if (!Enum.IsDefined(period))
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }
    }
}
