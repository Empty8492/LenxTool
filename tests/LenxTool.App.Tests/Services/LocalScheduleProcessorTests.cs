using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.App.Tests.Services;

public sealed class LocalScheduleProcessorTests : IDisposable
{
    private const string ScheduleId =
        "10000000-0000-4000-8000-000000000020";
    private const string UnknownScheduleId =
        "10000000-0000-4000-8000-000000000099";
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 5, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DueAt = CreatedAt.AddHours(1);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools local schedule processor tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RegisteredHandlerExecutesDueWindowOnce()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, ScheduleId);
        var executions = new List<LocalScheduleExecution>();
        var handler = new StubHandler(
            ScheduleId,
            (execution, _) =>
            {
                executions.Add(execution);
                return Task.CompletedTask;
            });
        using var processor = CreateProcessor(
            database,
            new FrozenTimeProvider(DueAt),
            [handler]);

        Assert.Equal(
            1,
            await processor.ProcessBackgroundBatchAsync(
                CancellationToken.None));
        Assert.Equal(
            0,
            await processor.ProcessBackgroundBatchAsync(
                CancellationToken.None));

        LocalScheduleExecution execution = Assert.Single(executions);
        Assert.Equal(ScheduleId, execution.ScheduleId);
        Assert.Equal(DueAt, execution.ScheduledForUtc);
        Assert.Equal(1, execution.AttemptCount);
        LocalScheduleRun run = Assert.Single(
            await new LocalScheduleRunRepository(database).GetRecentAsync(
                ScheduleId,
                10,
                CancellationToken.None));
        Assert.Equal(LocalScheduleRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task ScheduleWithoutRegisteredHandlerIsNotClaimed()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, UnknownScheduleId);
        var handler = new StubHandler(
            ScheduleId,
            static (_, _) => Task.CompletedTask);
        using var processor = CreateProcessor(
            database,
            new FrozenTimeProvider(DueAt),
            [handler]);

        Assert.Equal(
            0,
            await processor.ProcessBackgroundBatchAsync(
                CancellationToken.None));
        Assert.Empty(
            await new LocalScheduleRunRepository(database).GetRecentAsync(
                UnknownScheduleId,
                10,
                CancellationToken.None));
    }

    [Fact]
    public async Task ScheduleMutationCooperativelyCancelsRunningHandler()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, ScheduleId);
        var time = new MutableTimeProvider(DueAt);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(
            ScheduleId,
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            });
        using var processor = CreateProcessor(
            database,
            time,
            [handler],
            leaseDuration: TimeSpan.FromMilliseconds(300));

        Task<int> processing = processor.ProcessBackgroundBatchAsync(
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.SetUtcNow(DueAt.AddMilliseconds(50));
        await new LocalScheduledTaskRepository(database).SetEnabledAsync(
            ScheduleId,
            isEnabled: false,
            time.GetUtcNow(),
            CancellationToken.None);

        Assert.Equal(
            1,
            await processing.WaitAsync(TimeSpan.FromSeconds(2)));
        LocalScheduleRun run = Assert.Single(
            await new LocalScheduleRunRepository(database).GetRecentAsync(
                ScheduleId,
                10,
                CancellationToken.None));
        Assert.Equal(LocalScheduleRunStatus.Cancelled, run.Status);
    }

    [Fact]
    public async Task HandlerFailureReleasesWindowForIdempotentRetry()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, ScheduleId);
        var attempts = new List<int>();
        var handler = new StubHandler(
            ScheduleId,
            (execution, _) =>
            {
                attempts.Add(execution.AttemptCount);
                if (execution.AttemptCount == 1)
                {
                    throw new InvalidOperationException("simulated failure");
                }
                return Task.CompletedTask;
            });
        using var processor = CreateProcessor(
            database,
            new FrozenTimeProvider(DueAt),
            [handler]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessBackgroundBatchAsync(CancellationToken.None));
        Assert.Equal(
            LocalScheduleRunStatus.Pending,
            Assert.Single(
                await new LocalScheduleRunRepository(database).GetRecentAsync(
                    ScheduleId,
                    10,
                    CancellationToken.None)).Status);

        Assert.Equal(
            1,
            await processor.ProcessBackgroundBatchAsync(
                CancellationToken.None));
        Assert.Equal([1, 2], attempts);
        Assert.Equal(
            LocalScheduleRunStatus.Completed,
            Assert.Single(
                await new LocalScheduleRunRepository(database).GetRecentAsync(
                    ScheduleId,
                    10,
                CancellationToken.None)).Status);
    }

    [Fact]
    public async Task RetryAfterDefersFailedWindowAndDoesNotStarveOtherPlan()
    {
        const string secondScheduleId =
            "10000000-0000-4000-8000-000000000021";
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, ScheduleId);
        await SaveDailyAsync(database, secondScheduleId);
        var time = new MutableTimeProvider(DueAt);
        int firstAttempts = 0;
        var first = new StubHandler(
            ScheduleId,
            (_, _) =>
            {
                firstAttempts++;
                if (firstAttempts == 1)
                {
                    throw new AppException(new(
                        AppErrorCode.ProviderRateLimited,
                        "限流",
                        "稍后重试。",
                        "等待服务商窗口。",
                        RetryAfter: TimeSpan.FromMinutes(2),
                        IsRetryable: true));
                }
                return Task.CompletedTask;
            });
        int secondAttempts = 0;
        var second = new StubHandler(
            secondScheduleId,
            (_, _) =>
            {
                secondAttempts++;
                return Task.CompletedTask;
            });
        using var processor = CreateProcessor(
            database,
            time,
            [first, second]);

        await Assert.ThrowsAsync<AppException>(() =>
            processor.ProcessBackgroundBatchAsync(CancellationToken.None));
        Assert.Equal(
            DueAt.AddMinutes(2),
            Assert.Single(
                await new LocalScheduleRunRepository(database)
                    .GetRecentAsync(
                        ScheduleId,
                        10,
                        CancellationToken.None))
                .RetryNotBeforeUtc);
        Assert.Equal(
            1,
            await processor.ProcessBackgroundBatchAsync(
                CancellationToken.None));
        Assert.Equal(1, secondAttempts);
        Assert.Equal(
            0,
            await processor.ProcessBackgroundBatchAsync(
                CancellationToken.None));

        time.SetUtcNow(DueAt.AddMinutes(2));
        Assert.Equal(
            1,
            await processor.ProcessBackgroundBatchAsync(
                CancellationToken.None));
        Assert.Equal(2, firstAttempts);
    }

    [Fact]
    public async Task NonRetryableProviderFailureCancelsWindowAndDoesNotStarveOtherPlan()
    {
        const string secondScheduleId =
            "10000000-0000-4000-8000-000000000021";
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, ScheduleId);
        await SaveDailyAsync(database, secondScheduleId);
        int firstAttempts = 0;
        var first = new StubHandler(
            ScheduleId,
            (_, _) =>
            {
                firstAttempts++;
                throw new AppException(new(
                    AppErrorCode.CredentialsInvalid,
                    "凭据无效",
                    "请更新凭据。",
                    "服务商明确拒绝请求。"));
            });
        int secondAttempts = 0;
        var second = new StubHandler(
            secondScheduleId,
            (_, _) =>
            {
                secondAttempts++;
                return Task.CompletedTask;
            });
        using var processor = CreateProcessor(
            database,
            new FrozenTimeProvider(DueAt),
            [first, second]);

        await Assert.ThrowsAsync<AppException>(() =>
            processor.ProcessBackgroundBatchAsync(CancellationToken.None));
        Assert.Equal(
            LocalScheduleRunStatus.Cancelled,
            Assert.Single(
                await new LocalScheduleRunRepository(database).GetRecentAsync(
                    ScheduleId,
                    10,
                    CancellationToken.None)).Status);

        Assert.Equal(
            1,
            await processor.ProcessBackgroundBatchAsync(
                CancellationToken.None));
        Assert.Equal(1, firstAttempts);
        Assert.Equal(1, secondAttempts);
    }

    [Fact]
    public async Task StopDuringInitialCancellationProbeReleasesClaimedWindow()
    {
        var repository = new BlockingProbeRepository();
        var handler = new StubHandler(
            ScheduleId,
            static (_, _) => Task.CompletedTask);
        using var processor = new LocalScheduleProcessor(
            repository,
            [handler],
            new FrozenTimeProvider(DueAt),
            new LocalScheduleProcessorOptions(
                LeaseDuration: TimeSpan.FromMinutes(10),
                MissedRunGracePeriod: TimeSpan.FromMinutes(1),
                PollInterval: TimeSpan.FromSeconds(10)));
        using var stopping = new CancellationTokenSource();

        Task<int> processing = processor.ProcessBackgroundBatchAsync(
            stopping.Token);
        await repository.ProbeStarted.WaitAsync(TimeSpan.FromSeconds(2));
        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processing);
        Assert.True(repository.WasReleased);
    }

    private static LocalScheduleProcessor CreateProcessor(
        SqliteDatabase database,
        TimeProvider timeProvider,
        IReadOnlyList<ILocalScheduledTaskHandler> handlers,
        TimeSpan? leaseDuration = null) => new(
            new LocalScheduleRunRepository(database),
            handlers,
            timeProvider,
            new LocalScheduleProcessorOptions(
                leaseDuration ?? TimeSpan.FromMinutes(10),
                MissedRunGracePeriod: TimeSpan.FromMinutes(1),
                PollInterval: TimeSpan.FromSeconds(10)));

    private static Task<LocalScheduledTask> SaveDailyAsync(
        SqliteDatabase database,
        string scheduleId) =>
        new LocalScheduledTaskRepository(database).SaveAsync(
            scheduleId,
            new LocalScheduleDefinition(
                LocalScheduleFrequency.Daily,
                "UTC",
                new TimeOnly(8, 0)),
            LocalScheduleMissedRunPolicy.RunOnce,
            isEnabled: true,
            CreatedAt,
            CancellationToken.None);

    private SqliteDatabase CreateDatabase() => new(
        new AppPaths(_testRoot),
        NullLogger<SqliteDatabase>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private sealed class StubHandler(
        string scheduleId,
        Func<LocalScheduleExecution, CancellationToken, Task> executeAsync)
        : ILocalScheduledTaskHandler
    {
        public string ScheduleId { get; } = scheduleId;

        public bool IsIdempotent => true;

        public Task ExecuteAsync(
            LocalScheduleExecution execution,
            CancellationToken cancellationToken) =>
            executeAsync(execution, cancellationToken);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }

    private sealed class BlockingProbeRepository
        : ILocalScheduleRunRepository
    {
        private readonly LocalScheduleRunLease _lease = new(
            ScheduleId,
            DueAt,
            AttemptCount: 1,
            "10000000000040008000000000000020");

        public Task ProbeStarted => _probeStarted.Task;

        public bool WasReleased { get; private set; }

        private readonly TaskCompletionSource _probeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LocalScheduleRunLease?> ClaimDueAsync(
            DateTimeOffset nowUtc,
            DateTimeOffset missedBeforeUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            Task.FromResult<LocalScheduleRunLease?>(_lease);

        public Task<LocalScheduleRunLease?> ClaimDueAsync(
            IReadOnlyCollection<string> eligibleScheduleIds,
            DateTimeOffset nowUtc,
            DateTimeOffset missedBeforeUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            Task.FromResult<LocalScheduleRunLease?>(_lease);

        public async Task<bool> IsCancellationRequestedAsync(
            LocalScheduleRunLease lease,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken)
        {
            _probeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return false;
        }

        public Task<bool> RenewLeaseAsync(
            LocalScheduleRunLease lease,
            DateTimeOffset renewedAtUtc,
            DateTimeOffset leaseExpiresAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task CompleteAsync(
            LocalScheduleRunLease lease,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAsync(
            LocalScheduleRunLease lease,
            DateTimeOffset cancelledAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(
            LocalScheduleRunLease lease,
            DateTimeOffset releasedAtUtc,
            CancellationToken cancellationToken,
            DateTimeOffset? retryNotBeforeUtc = null)
        {
            WasReleased = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalScheduleRun>> GetRecentAsync(
            string scheduleId,
            int maximumCount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
