using System.IO;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Core.Scheduling;

namespace LenxTool.App.Services;

/// <summary>
/// 把一个稳定的日/周计划窗口投影为本地 Feed 聚合摘要。租约令牌只交给专用执行仓储，
/// 用于在同一 SQLite 事务中验证代际并提交报告与窗口终态。只有能证明未产生模型结果的安全失败
/// 才释放重试；结果不明的请求宁可放弃该窗口，也不自动重放造成二次计费。
/// </summary>
public sealed class FeedDigestScheduledTaskHandler : ILocalScheduledTaskHandler
{
    private readonly FeedDigestPeriod _period;
    private readonly ILocalScheduledTaskRepository _scheduledTasks;
    private readonly IFeedEntryRepository _entries;
    private readonly INewsRepository _reports;
    private readonly IAiReportService _aiReports;
    private readonly IFeedDigestExecutionStore _executionStore;
    private readonly FeedDigestOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IAppNotificationPublisher? _notifications;

    public FeedDigestScheduledTaskHandler(
        FeedDigestPeriod period,
        ILocalScheduledTaskRepository scheduledTasks,
        IFeedEntryRepository entries,
        INewsRepository reports,
        IAiReportService aiReports,
        IFeedDigestExecutionStore executionStore,
        FeedDigestOptions options,
        TimeProvider timeProvider,
        IAppNotificationPublisher? notifications = null)
    {
        if (!Enum.IsDefined(period))
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }
        ArgumentNullException.ThrowIfNull(scheduledTasks);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(aiReports);
        ArgumentNullException.ThrowIfNull(executionStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _period = period;
        _scheduledTasks = scheduledTasks;
        _entries = entries;
        _reports = reports;
        _aiReports = aiReports;
        _executionStore = executionStore;
        _options = options;
        _timeProvider = timeProvider;
        _notifications = notifications;
    }

    public string ScheduleId => FeedDigestScheduleIds.For(_period);

    public bool IsIdempotent => true;

    public async Task ExecuteAsync(
        LocalScheduleExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!string.Equals(
                execution.ScheduleId,
                ScheduleId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "摘要执行上下文与处理器 ID 不一致。",
                nameof(execution));
        }

        LocalScheduledTask task = await _scheduledTasks.GetAsync(
                ScheduleId,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "摘要计划在执行前已不存在。");
        FeedDigestWindow window = FeedDigestPlanner.GetWindow(
            _period,
            task.Schedule,
            execution.ScheduledForUtc);
        FeedDigestScope scope = FeedDigestScopePayload.Deserialize(
            task.Payload);
        FeedEntryPage page = await _entries.QueryAsync(
            new(
                scope.SearchText,
                scope.FeedId,
                scope.CategoryId,
                window.StartUtc,
                window.EndUtc,
                FeedEntryReadFilter.All,
                Offset: 0,
                Limit: _options.MaximumCandidateEntries,
                ActiveOnly: true),
            cancellationToken).ConfigureAwait(false);
        string title = CreateTitle(task.Schedule, window.EndUtc);
        FeedDigestPlan? plan = FeedDigestPlanner.CreatePlan(
            _period,
            ScheduleId,
            scope,
            window,
            page.Items,
            _options,
            title);
        if (plan is null)
        {
            // 空窗口是成功终态；它不产生占位报告，也不会消耗模型额度。
            return;
        }

        AiReport? cached = await _reports.GetReportByIdAsync(
            plan.ReportId,
            cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            // The report commit may have won immediately before a prior
            // best-effort notification failed. Publishing is idempotent and
            // repairs that narrow crash/failure window without model replay.
            await TryPublishCompletionAsync(cached).ConfigureAwait(false);
            return;
        }

        LocalScheduleRunLease lease = execution.RequireLease();
        FeedDigestExecutionBeginResult begin =
            await _executionStore.BeginAsync(
                lease,
                plan.ReportId,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        if (begin == FeedDigestExecutionBeginResult
                .SuppressedUncertainPriorAttempt)
        {
            // 上一次进程可能已经把请求发给模型；没有服务端幂等键时宁可
            // 放弃本窗口，也不能通过自动重放制造第二次计费。
            return;
        }
        if (begin == FeedDigestExecutionBeginResult.AlreadyCompleted)
        {
            throw new InvalidDataException(
                "摘要调用已完成但本地报告缺失。");
        }

        AiReport generated;
        bool committed;
        try
        {
            generated = await _aiReports.GenerateFeedDigestAsync(
                plan,
                cancellationToken).ConfigureAwait(false);
            ValidateGeneratedReport(plan, generated);
            cancellationToken.ThrowIfCancellationRequested();
            committed = await _executionStore.CompleteAsync(
                lease,
                generated,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (AppException exception)
            when (IsSafeToRetry(exception.Error))
        {
            // 明确的 4xx/429 响应证明本次没有产生可保存的模型结果；只有
            // 这类失败才清除 STARTED，让通用调度器按 Retry-After 重试。
            await _executionStore.ClearForSafeRetryAsync(
                lease,
                plan.ReportId,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await _executionStore.AbandonUncertainAsync(
                lease,
                plan.ReportId,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (committed)
        {
            await TryPublishCompletionAsync(generated).ConfigureAwait(false);
        }
    }

    private async Task TryPublishCompletionAsync(AiReport report)
    {
        if (_notifications is null)
        {
            return;
        }

        try
        {
            await _notifications.PublishAsync(
                new(
                    AppNotificationKind.TaskCompleted,
                    $"feed-digest:{report.Id}",
                    report.Id,
                    ScheduleId,
                    report.Title,
                    "本地定时摘要",
                    TargetKind: AppNotificationTargetKind.AiReport,
                    TargetId: report.Id),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 系统或应用内通知失败不能反向破坏已原子提交的摘要结果。
        }
    }

    private string CreateTitle(
        LocalScheduleDefinition schedule,
        DateTimeOffset windowEndUtc)
    {
        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(
            schedule.TimeZoneId);
        DateOnly localDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(windowEndUtc, timeZone).DateTime);
        return _period switch
        {
            FeedDigestPeriod.Daily =>
                $"每日订阅摘要 · {localDate:yyyy-MM-dd}",
            FeedDigestPeriod.Weekly =>
                $"每周订阅摘要 · 截至 {localDate:yyyy-MM-dd}",
            _ => throw new InvalidOperationException("摘要周期状态无效。")
        };
    }

    private void ValidateGeneratedReport(
        FeedDigestPlan plan,
        AiReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string expectedType = _period switch
        {
            FeedDigestPeriod.Daily => "daily_feed_digest",
            FeedDigestPeriod.Weekly => "weekly_feed_digest",
            _ => throw new InvalidOperationException("摘要周期状态无效。")
        };
        if (!string.Equals(report.Id, plan.ReportId, StringComparison.Ordinal)
            || !string.Equals(
                report.EntityType,
                "feed_digest",
                StringComparison.Ordinal)
            || !string.Equals(
                report.EntityId,
                ScheduleId,
                StringComparison.Ordinal)
            || !string.Equals(
                report.ReportType,
                expectedType,
                StringComparison.Ordinal)
            || !string.Equals(
                report.Model,
                _options.Model,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(report.Content)
            || report.Content.Length > _options.MaximumReportCharacters)
        {
            throw new InvalidDataException(
                "AI 摘要结果不符合本地报告契约。");
        }
    }

    private static bool IsSafeToRetry(AppError error) =>
        error.Code is AppErrorCode.InvalidRequest
            or AppErrorCode.Conflict
            or AppErrorCode.CredentialsInvalid
            or AppErrorCode.AccessDenied
            or AppErrorCode.ProviderRateLimited;
}
