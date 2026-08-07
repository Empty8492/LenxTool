using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedDigestScheduledTaskHandlerTests
{
    private const string LeaseToken =
        "10000000000040008000000000000021";

    [Fact]
    public async Task EmptyWindowCompletesWithoutCallingModel()
    {
        LocalScheduledTask task = DailyTask();
        var entries = new StubEntryRepository([]);
        var reports = new StubNewsRepository();
        var ai = new StubAiReportService();
        var handler = CreateHandler(task, entries, reports, ai);

        await handler.ExecuteAsync(
            Execution(1),
            CancellationToken.None);

        Assert.NotNull(entries.LastQuery);
        Assert.Equal(Utc(2026, 8, 5, 8, 0), entries.LastQuery!.PublishedFrom);
        Assert.Equal(Utc(2026, 8, 6, 8, 0), entries.LastQuery.PublishedBefore);
        Assert.True(entries.LastQuery.ActiveOnly);
        Assert.Equal(FeedDigestOptions.Default.MaximumCandidateEntries, entries.LastQuery.Limit);
        Assert.Equal(0, ai.DigestCalls);
        Assert.Null(reports.SavedReport);
    }

    [Fact]
    public async Task ExistingDeterministicReportSkipsModelAndRecoversNotification()
    {
        LocalScheduledTask task = DailyTask();
        var entries = new StubEntryRepository([Entry("entry-1", "摘要")]);
        var reports = new StubNewsRepository
        {
            ReportFactory = id => Report(id, FeedDigestScheduleIds.Daily)
        };
        var ai = new StubAiReportService();
        var notifications = new RecordingNotificationPublisher();
        var handler = CreateHandler(
            task,
            entries,
            reports,
            ai,
            new StubExecutionStore(),
            notifications);

        await handler.ExecuteAsync(
            Execution(2),
            CancellationToken.None);

        Assert.StartsWith("feed-digest-", reports.LastRequestedReportId, StringComparison.Ordinal);
        Assert.Equal(0, ai.DigestCalls);
        Assert.Null(reports.SavedReport);
        Assert.Single(notifications.Drafts);
    }

    [Fact]
    public async Task NewWindowPersistsGeneratedReportForHistoryAndSearch()
    {
        LocalScheduledTask task = DailyTask();
        var entries = new StubEntryRepository(
        [
            Entry("entry-1", "第一条摘要"),
            Entry("entry-2", "第二条摘要", "https://example.test/second")
        ]);
        var reports = new StubNewsRepository();
        var executions = new StubExecutionStore();
        var ai = new StubAiReportService
        {
            DigestFactory = plan => Report(plan.ReportId, plan.ScheduleId)
        };
        var handler = CreateHandler(task, entries, reports, ai, executions);

        await handler.ExecuteAsync(
            Execution(1),
            CancellationToken.None);

        FeedDigestPlan plan = Assert.IsType<FeedDigestPlan>(ai.LastPlan);
        AiReport saved = Assert.IsType<AiReport>(executions.CompletedReport);
        Assert.Equal(2, plan.EntryCount);
        Assert.Equal(plan.ReportId, saved.Id);
        Assert.Equal("feed_digest", saved.EntityType);
        Assert.Equal(FeedDigestScheduleIds.Daily, saved.EntityId);
        Assert.Equal("daily_feed_digest", saved.ReportType);
        Assert.Equal(1, ai.DigestCalls);
        Assert.Null(reports.SavedReport);
    }

    [Fact]
    public async Task CommittedDigestPublishesAiReportTargetWithoutBody()
    {
        LocalScheduledTask task = DailyTask();
        var entries = new StubEntryRepository([Entry("entry-1", "摘要")]);
        var executions = new StubExecutionStore();
        var notifications = new RecordingNotificationPublisher();
        var ai = new StubAiReportService
        {
            DigestFactory = plan => Report(plan.ReportId, plan.ScheduleId)
        };
        var handler = CreateHandler(
            task,
            entries,
            new StubNewsRepository(),
            ai,
            executions,
            notifications);

        await handler.ExecuteAsync(Execution(1), CancellationToken.None);

        AppNotificationDraft draft = Assert.Single(notifications.Drafts);
        AiReport report = Assert.IsType<AiReport>(executions.CompletedReport);
        Assert.Equal(AppNotificationKind.TaskCompleted, draft.Kind);
        Assert.Equal(AppNotificationTargetKind.AiReport, draft.TargetKind);
        Assert.Equal(report.Id, draft.TargetId);
        Assert.Equal(report.Title, draft.Title);
        Assert.DoesNotContain(report.Content, draft.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleGenerationOrNotificationFailureDoesNotCorruptDigestResult()
    {
        LocalScheduledTask task = DailyTask();
        var entries = new StubEntryRepository([Entry("entry-1", "摘要")]);
        var staleExecutions = new StubExecutionStore
        {
            CompleteResult = false
        };
        var staleNotifications = new RecordingNotificationPublisher();
        var ai = new StubAiReportService();
        var staleHandler = CreateHandler(
            task,
            entries,
            new StubNewsRepository(),
            ai,
            staleExecutions,
            staleNotifications);

        await staleHandler.ExecuteAsync(Execution(1), CancellationToken.None);

        Assert.Empty(staleNotifications.Drafts);

        var committedExecutions = new StubExecutionStore();
        var failingNotifications = new RecordingNotificationPublisher
        {
            Failure = new IOException("notification unavailable")
        };
        var committedHandler = CreateHandler(
            task,
            entries,
            new StubNewsRepository(),
            ai,
            committedExecutions,
            failingNotifications);

        await committedHandler.ExecuteAsync(
            Execution(2),
            CancellationToken.None);

        Assert.NotNull(committedExecutions.CompletedReport);
        Assert.Single(failingNotifications.Drafts);
    }

    [Fact]
    public async Task RetryableProviderFailureIsNotConvertedToSuccess()
    {
        LocalScheduledTask task = DailyTask();
        var entries = new StubEntryRepository([Entry("entry-1", "摘要")]);
        var reports = new StubNewsRepository();
        var executions = new StubExecutionStore();
        AppError retryable = new(
            AppErrorCode.ProviderRateLimited,
            "请求过于频繁",
            "DeepSeek 暂时限流。",
            "稍后重试。",
            Provider: "DeepSeek",
            RetryAfter: TimeSpan.FromMinutes(2),
            IsRetryable: true);
        var ai = new StubAiReportService
        {
            DigestError = new AppException(retryable)
        };
        var handler = CreateHandler(task, entries, reports, ai, executions);

        AppException exception = await Assert.ThrowsAsync<AppException>(() =>
            handler.ExecuteAsync(
                Execution(1),
                CancellationToken.None));

        Assert.Same(retryable, exception.Error);
        Assert.Null(reports.SavedReport);
        Assert.True(executions.WasClearedForRetry);
        Assert.False(executions.WasAbandoned);
    }

    [Fact]
    public async Task NetworkResultUnknownIsAbandonedInsteadOfRetried()
    {
        LocalScheduledTask task = DailyTask();
        var entries = new StubEntryRepository([Entry("entry-1", "摘要")]);
        var executions = new StubExecutionStore();
        var ai = new StubAiReportService
        {
            DigestError = new AppException(new(
                AppErrorCode.NetworkUnavailable,
                "网络中断",
                "无法确认模型是否已处理请求。",
                "保留本地状态。",
                Provider: "DeepSeek",
                IsRetryable: true))
        };
        var handler = CreateHandler(
            task,
            entries,
            new StubNewsRepository(),
            ai,
            executions);

        await Assert.ThrowsAsync<AppException>(() => handler.ExecuteAsync(
            Execution(1),
            CancellationToken.None));

        Assert.Equal(1, ai.DigestCalls);
        Assert.True(executions.WasAbandoned);
        Assert.False(executions.WasClearedForRetry);
    }

    [Fact]
    public async Task UncertainPriorAttemptSuppressesSecondModelCall()
    {
        LocalScheduledTask task = DailyTask();
        var entries = new StubEntryRepository([Entry("entry-1", "摘要")]);
        var executions = new StubExecutionStore
        {
            BeginResult = FeedDigestExecutionBeginResult
                .SuppressedUncertainPriorAttempt
        };
        var ai = new StubAiReportService();
        var handler = CreateHandler(
            task,
            entries,
            new StubNewsRepository(),
            ai,
            executions);

        await handler.ExecuteAsync(
            Execution(2),
            CancellationToken.None);

        Assert.Equal(0, ai.DigestCalls);
        Assert.Null(executions.CompletedReport);
    }

    [Fact]
    public async Task CancellationAfterModelResponsePreventsDurableReportWrite()
    {
        using var cancellation = new CancellationTokenSource();
        LocalScheduledTask task = DailyTask();
        var entries = new StubEntryRepository([Entry("entry-1", "摘要")]);
        var reports = new StubNewsRepository();
        var executions = new StubExecutionStore();
        var ai = new StubAiReportService
        {
            OnDigest = cancellation.Cancel
        };
        var handler = CreateHandler(task, entries, reports, ai, executions);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync(
                Execution(1),
                cancellation.Token));

        Assert.Equal(1, ai.DigestCalls);
        Assert.Null(reports.SavedReport);
        Assert.True(executions.WasAbandoned);
    }

    private static FeedDigestScheduledTaskHandler CreateHandler(
        LocalScheduledTask task,
        IFeedEntryRepository entries,
        INewsRepository reports,
        IAiReportService ai,
        IFeedDigestExecutionStore? executionStore = null,
        IAppNotificationPublisher? notifications = null) =>
        new(
            FeedDigestPeriod.Daily,
            new StubScheduledTaskRepository(task),
            entries,
            reports,
            ai,
            executionStore ?? new StubExecutionStore(),
            FeedDigestOptions.Default,
            new FrozenTimeProvider(Utc(2026, 8, 6, 8, 1)),
            notifications);

    private static LocalScheduledTask DailyTask() =>
        new(
            FeedDigestScheduleIds.Daily,
            new(
                LocalScheduleFrequency.Daily,
                "UTC",
                new TimeOnly(8, 0)),
            LocalScheduleMissedRunPolicy.RunOnce,
            true,
            Utc(2026, 8, 6, 8, 0),
            Utc(2026, 8, 1, 0, 0),
            Utc(2026, 8, 1, 0, 0),
            FeedDigestScopePayload.Serialize(
                FeedDigestScope.AllActive));

    private static LocalScheduleExecution Execution(int attemptCount) =>
        new(
            FeedDigestScheduleIds.Daily,
            Utc(2026, 8, 6, 8, 0),
            attemptCount,
            LeaseToken);

    private static FeedEntry Entry(
        string id,
        string summary,
        string? url = "https://example.test/shared") =>
        new(
            id,
            "10000000-0000-0000-0000-000000000001",
            id,
            url,
            $"标题 {id}",
            null,
            Utc(2026, 8, 5, 10, 0),
            null,
            summary,
            string.Empty,
            [],
            [],
            new string(id[^1], 64),
            Utc(2026, 8, 5, 10, 0));

    private static AiReport Report(string id, string scheduleId) =>
        new(
            id,
            "feed_digest",
            scheduleId,
            "daily_feed_digest",
            "每日订阅摘要 · 2026-08-06",
            "生成的本地摘要",
            FeedDigestOptions.Default.Model,
            1,
            123,
            Utc(2026, 8, 6, 8, 1));

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StubExecutionStore : IFeedDigestExecutionStore
    {
        public FeedDigestExecutionBeginResult BeginResult { get; init; } =
            FeedDigestExecutionBeginResult.Started;
        public AiReport? CompletedReport { get; private set; }
        public bool WasClearedForRetry { get; private set; }
        public bool WasAbandoned { get; private set; }
        public bool CompleteResult { get; init; } = true;

        public Task<FeedDigestExecutionBeginResult> BeginAsync(
            LocalScheduleRunLease lease,
            string reportId,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(BeginResult);

        public Task ClearForSafeRetryAsync(
            LocalScheduleRunLease lease,
            string reportId,
            DateTimeOffset clearedAtUtc,
            CancellationToken cancellationToken)
        {
            WasClearedForRetry = true;
            return Task.CompletedTask;
        }

        public Task<bool> CompleteAsync(
            LocalScheduleRunLease lease,
            AiReport report,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken)
        {
            CompletedReport = report;
            return Task.FromResult(CompleteResult);
        }

        public Task AbandonUncertainAsync(
            LocalScheduleRunLease lease,
            string reportId,
            DateTimeOffset abandonedAtUtc,
            CancellationToken cancellationToken)
        {
            WasAbandoned = true;
            return Task.CompletedTask;
        }
    }
    private sealed class StubScheduledTaskRepository(LocalScheduledTask task)
        : ILocalScheduledTaskRepository
    {
        public Task<LocalScheduledTask> SaveAsync(
            string id,
            LocalScheduleDefinition schedule,
            LocalScheduleMissedRunPolicy missedRunPolicy,
            bool isEnabled,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken,
            string? payload = null) => throw new NotSupportedException();

        public Task<LocalScheduledTask?> GetAsync(
            string id,
            CancellationToken cancellationToken) =>
            Task.FromResult<LocalScheduledTask?>(
                string.Equals(id, task.Id, StringComparison.Ordinal)
                    ? task
                    : null);

        public Task<IReadOnlyList<LocalScheduledTask>> GetAllAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalScheduledTask>>([task]);

        public Task<LocalScheduledTask?> SetEnabledAsync(
            string id,
            bool isEnabled,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubEntryRepository(IReadOnlyList<FeedEntry> entries)
        : IFeedEntryRepository
    {
        public FeedEntryQuery? LastQuery { get; private set; }

        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(new FeedEntryPage(entries, 0, false));
        }

        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubAiReportService : IAiReportService
    {
        public int DigestCalls { get; private set; }
        public FeedDigestPlan? LastPlan { get; private set; }
        public Func<FeedDigestPlan, AiReport>? DigestFactory { get; init; }
        public AppException? DigestError { get; init; }
        public Action? OnDigest { get; init; }

        public Task<AiReport> GenerateFeedDigestAsync(
            FeedDigestPlan plan,
            CancellationToken cancellationToken)
        {
            DigestCalls++;
            LastPlan = plan;
            OnDigest?.Invoke();
            if (DigestError is not null) throw DigestError;
            return Task.FromResult(
                DigestFactory?.Invoke(plan)
                ?? Report(plan.ReportId, plan.ScheduleId));
        }

        public Task<AiReport> GenerateArticleInsightAsync(
            NewsArticle article,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AiReport> GenerateDailyTrendReportAsync(
            IReadOnlyList<TrendItem> trends,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubNewsRepository : INewsRepository
    {
        public Func<string, AiReport?>? ReportFactory { get; init; }
        public string? LastRequestedReportId { get; private set; }
        public AiReport? SavedReport { get; private set; }

        public Task<AiReport?> GetReportByIdAsync(
            string reportId,
            CancellationToken cancellationToken)
        {
            LastRequestedReportId = reportId;
            return Task.FromResult(ReportFactory?.Invoke(reportId));
        }

        public Task UpsertReportAsync(
            AiReport report,
            CancellationToken cancellationToken)
        {
            SavedReport = report;
            return Task.CompletedTask;
        }

        public Task UpsertAsync(
            IReadOnlyCollection<NewsArticle> articles,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<NewsArticle>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ContentSearchResult>> SearchContentAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ContentSearchPage> SearchContentAsync(
            ContentSearchQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<AiReport>> GetLatestReportsAsync(
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<NewsArticle>> GetLatestAsync(
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpsertTrendsAsync(
            IReadOnlyCollection<TrendItem> trends,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TrendItem>> GetLatestTrendsAsync(
            int limit,
            string? platform,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingNotificationPublisher
        : IAppNotificationPublisher
    {
        public List<AppNotificationDraft> Drafts { get; } = [];
        public Exception? Failure { get; init; }

        public Task<AppNotificationRegistration> PublishAsync(
            AppNotificationDraft draft,
            CancellationToken cancellationToken)
        {
            Drafts.Add(draft);
            if (Failure is not null)
            {
                throw Failure;
            }
            return Task.FromResult(new AppNotificationRegistration(
                new(
                    new string('d', 64),
                    draft.EntryId,
                    draft.FeedId,
                    Guid.Empty.ToString("D"),
                    1,
                    draft.Title,
                    draft.SourceLabel,
                    Utc(2026, 8, 6, 8, 1),
                    ReadAt: null,
                    draft.Kind,
                    draft.TargetKind,
                    draft.TargetId),
                Created: true));
        }
    }
}
