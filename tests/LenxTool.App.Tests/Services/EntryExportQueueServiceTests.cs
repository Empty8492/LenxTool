using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.App.Tests.Services;

public sealed class EntryExportQueueServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly EntryExportQueueOptions Options = new(
        MaximumAttempts: 3,
        LeaseDuration: TimeSpan.FromMinutes(5),
        PollInterval: TimeSpan.FromSeconds(1),
        BaseRetryDelay: TimeSpan.FromSeconds(10),
        MaximumRetryDelay: TimeSpan.FromDays(7));

    [Fact]
    public async Task RateLimitRetryUsesExactRetryAfterAndKeepsClosedErrorCode()
    {
        FeedEntry entry = Entry();
        EntryExportTaskLease lease = Lease(entry);
        var repository = new StubRepository(lease);
        var coordinator = new StubCoordinator
        {
            Handler = (request, _) => Task.FromResult(
                EntryExportResult.Failure(
                    request.IdempotencyKey,
                    new(
                        EntryExportErrorCode.RateLimited,
                        IsRetryable: true,
                        RetryAfter: TimeSpan.FromSeconds(47))))
        };
        EntryExportQueueService service = CreateService(
            repository,
            coordinator,
            entry);

        Assert.Equal(
            1,
            await service.ProcessBackgroundBatchAsync(
                CancellationToken.None));

        ScheduledRetry retry = Assert.Single(repository.Retries);
        Assert.Equal(EntryExportTaskErrorCode.RateLimited, retry.ErrorCode);
        Assert.Equal(Now.AddSeconds(47), retry.NextAttemptAt);
        Assert.Empty(repository.Failures);
    }

    [Fact]
    public async Task EnqueueRejectsNonIdempotentExporterBeforePersistingTask()
    {
        FeedEntry entry = Entry();
        var repository = new StubRepository();
        var coordinator = new StubCoordinator
        {
            Capabilities =
            [
                new(
                    "markdown",
                    "Markdown",
                    [EntryViewKind.Article],
                    RequiresCredentials: false,
                    MaximumContentBytes: null,
                    IsIdempotent: false)
            ],
            Handler = (_, _) => throw new InvalidOperationException(
                "Exporter must not run during enqueue.")
        };
        EntryExportQueueService service = CreateService(
            repository,
            coordinator,
            entry);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnqueueAsync(
                Request(entry),
                CancellationToken.None));
        Assert.Equal(0, repository.EnqueueCallCount);
    }

    [Fact]
    public async Task RecoveredTaskRefusesExporterThatBecameNonIdempotent()
    {
        FeedEntry entry = Entry();
        var repository = new StubRepository(Lease(entry));
        var coordinator = new StubCoordinator
        {
            Capabilities =
            [
                new(
                    "markdown",
                    "Markdown",
                    [EntryViewKind.Article],
                    RequiresCredentials: false,
                    MaximumContentBytes: null,
                    IsIdempotent: false)
            ],
            Handler = (_, _) => throw new InvalidOperationException(
                "A non-idempotent adapter must not run from recovery.")
        };
        EntryExportQueueService service = CreateService(
            repository,
            coordinator,
            entry);

        await service.ProcessBackgroundBatchAsync(CancellationToken.None);

        Assert.Equal(0, coordinator.CallCount);
        Assert.Equal(
            EntryExportTaskErrorCode.InvalidRequest,
            Assert.Single(repository.Failures).ErrorCode);
    }

    [Fact]
    public async Task MissingOrChangedEntryFailsWithoutCallingExporter()
    {
        FeedEntry changed = Entry(contentHash: new string('b', 64));
        EntryExportTaskLease lease = Lease(Entry());
        var repository = new StubRepository(lease);
        var coordinator = new StubCoordinator
        {
            Handler = (_, _) => throw new InvalidOperationException(
                "Exporter must not run for a stale entry version.")
        };
        EntryExportQueueService service = CreateService(
            repository,
            coordinator,
            changed);

        await service.ProcessBackgroundBatchAsync(CancellationToken.None);

        FailedTask failure = Assert.Single(repository.Failures);
        Assert.Equal(EntryExportTaskErrorCode.EntryChanged, failure.ErrorCode);
        Assert.Equal(0, coordinator.CallCount);
    }

    [Fact]
    public async Task UserCancellationStopsRunningExporterAndCommitsCancelledState()
    {
        FeedEntry entry = Entry();
        EntryExportTaskLease lease = Lease(entry);
        var repository = new StubRepository(lease);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new StubCoordinator
        {
            Handler = async (_, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }
        };
        EntryExportQueueService service = CreateService(
            repository,
            coordinator,
            entry);

        Task<int> processing = service.ProcessBackgroundBatchAsync(
            CancellationToken.None);
        await started.Task;
        Assert.Equal(
            EntryExportCancellationResult.CancellationRequested,
            await service.CancelAsync(
                lease.IdempotencyKey,
                CancellationToken.None));

        Assert.Equal(1, await processing);
        Assert.Single(repository.Cancelled);
        Assert.Empty(repository.Released);
    }

    [Fact]
    public async Task SuccessfulSideEffectWinsOverLateCancellationRequest()
    {
        FeedEntry entry = Entry();
        EntryExportTaskLease lease = Lease(entry);
        var repository = new StubRepository(lease);
        var sideEffectCommitted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var returnSuccess = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new StubCoordinator
        {
            Handler = async (request, _) =>
            {
                sideEffectCommitted.SetResult();
                await returnSuccess.Task;
                return EntryExportResult.Success(
                    request.IdempotencyKey,
                    "entry.md",
                    remoteUrl: null);
            }
        };
        EntryExportQueueService service = CreateService(
            repository,
            coordinator,
            entry);

        Task<int> processing = service.ProcessBackgroundBatchAsync(
            CancellationToken.None);
        await sideEffectCommitted.Task;
        await service.CancelAsync(
            lease.IdempotencyKey,
            CancellationToken.None);
        returnSuccess.SetResult();

        Assert.Equal(1, await processing);
        Assert.Single(repository.Completed);
        Assert.Empty(repository.Cancelled);
    }

    [Fact]
    public async Task LongRunningExporterRenewsLeaseUntilCompletion()
    {
        FeedEntry entry = Entry();
        var repository = new StubRepository(Lease(entry));
        var coordinator = new StubCoordinator
        {
            Handler = async (request, cancellationToken) =>
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(240),
                    cancellationToken);
                return EntryExportResult.Success(
                    request.IdempotencyKey,
                    "entry.md",
                    remoteUrl: null);
            }
        };
        EntryExportQueueOptions shortLease = Options with
        {
            LeaseDuration = TimeSpan.FromMilliseconds(90)
        };
        using var service = new EntryExportQueueService(
            repository,
            new StubFeedEntryRepository(
                new Dictionary<string, FeedEntry>(StringComparer.Ordinal)
                {
                    [entry.Id] = entry
                }),
            coordinator,
            TimeProvider.System,
            shortLease);

        Assert.Equal(
            1,
            await service.ProcessBackgroundBatchAsync(
                CancellationToken.None));
        Assert.True(repository.RenewCount >= 2);
        Assert.Single(repository.Completed);
    }

    [Fact]
    public async Task SecondProcessCannotReclaimTaskWhileFirstRenewsLease()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "Lenx Tools export lease heartbeat tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var database = new SqliteDatabase(
                new AppPaths(testRoot),
                NullLogger<SqliteDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            FeedEntry entry = Entry();
            var entries = new FeedEntryRepository(database);
            await entries.UpsertAsync(
                entry.FeedId,
                [entry],
                CancellationToken.None);
            var tasks = new EntryExportTaskRepository(database);
            await tasks.EnqueueAsync(
                Request(entry),
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            var started = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            var coordinator = new StubCoordinator
            {
                Handler = async (request, _) =>
                {
                    Interlocked.Increment(ref calls);
                    started.TrySetResult();
                    await release.Task;
                    return EntryExportResult.Success(
                        request.IdempotencyKey,
                        "entry.md",
                        remoteUrl: null);
                }
            };
            EntryExportQueueOptions shortLease = Options with
            {
                LeaseDuration = TimeSpan.FromMilliseconds(300)
            };
            using var firstService = new EntryExportQueueService(
                tasks,
                entries,
                coordinator,
                TimeProvider.System,
                shortLease);
            using var secondService = new EntryExportQueueService(
                tasks,
                entries,
                coordinator,
                TimeProvider.System,
                shortLease);

            Task<int> first = firstService.ProcessBackgroundBatchAsync(
                CancellationToken.None);
            await started.Task;
            await Task.Delay(450);
            Task<int> second = secondService.ProcessBackgroundBatchAsync(
                CancellationToken.None);
            Task finished = await Task.WhenAny(
                second,
                Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(second, finished);
            Assert.Equal(0, await second);
            Assert.Equal(1, calls);
            release.SetResult();
            Assert.Equal(1, await first);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PersistentCancellationFromSecondProcessStopsRunningExporter()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "Lenx Tools cross process export cancellation tests",
            Guid.NewGuid().ToString("N"));
        Task<int>? processing = null;
        EntryExportQueueService? firstService = null;
        string? idempotencyKey = null;
        SqliteDatabase? database = null;
        try
        {
            database = new SqliteDatabase(
                new AppPaths(testRoot),
                NullLogger<SqliteDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            FeedEntry entry = Entry();
            var entries = new FeedEntryRepository(database);
            await entries.UpsertAsync(
                entry.FeedId,
                [entry],
                CancellationToken.None);
            var tasks = new EntryExportTaskRepository(database);
            EntryExportEnqueueResult enqueued = await tasks.EnqueueAsync(
                Request(entry),
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            idempotencyKey = enqueued.Task.IdempotencyKey;
            var started = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var coordinator = new StubCoordinator
            {
                Handler = async (_, cancellationToken) =>
                {
                    started.TrySetResult();
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                    throw new InvalidOperationException("Unreachable.");
                }
            };
            EntryExportQueueOptions shortLease = Options with
            {
                LeaseDuration = TimeSpan.FromMilliseconds(300)
            };
            firstService = new EntryExportQueueService(
                tasks,
                entries,
                coordinator,
                TimeProvider.System,
                shortLease);
            using var secondService = new EntryExportQueueService(
                tasks,
                entries,
                coordinator,
                TimeProvider.System,
                shortLease);

            processing = firstService.ProcessBackgroundBatchAsync(
                CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(
                EntryExportCancellationResult.CancellationRequested,
                await secondService.CancelAsync(
                    enqueued.Task.IdempotencyKey,
                    CancellationToken.None));

            // 第二个进程只有持久化取消标志，第一进程必须由心跳观察它并把取消令牌
            // 传给正在运行的适配器，不能因持续续租而永久停留在 RUNNING。
            Assert.Equal(
                1,
                await processing.WaitAsync(TimeSpan.FromSeconds(2)));
            EntryExportTask cancelled = Assert.IsType<EntryExportTask>(
                await tasks.GetAsync(
                    enqueued.Task.IdempotencyKey,
                    CancellationToken.None));
            Assert.Equal(EntryExportTaskStatus.Cancelled, cancelled.Status);
            Assert.Null(cancelled.LastErrorCode);
        }
        finally
        {
            if (processing is { IsCompleted: false }
                && firstService is not null
                && idempotencyKey is not null)
            {
                await firstService.CancelAsync(
                    idempotencyKey,
                    CancellationToken.None);
                try
                {
                    await processing.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (OperationCanceledException)
                {
                    // 失败路径只负责结束仍在运行的测试任务。
                }
                catch (TimeoutException)
                {
                    // 即使产品逻辑失败，也不让清理路径无限等待。
                }
            }
            firstService?.Dispose();
            database?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ApplicationShutdownReleasesLeaseForRestartRecovery()
    {
        FeedEntry entry = Entry();
        EntryExportTaskLease lease = Lease(entry);
        var repository = new StubRepository(lease);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new StubCoordinator
        {
            Handler = async (_, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }
        };
        EntryExportQueueService service = CreateService(
            repository,
            coordinator,
            entry);
        using var stopping = new CancellationTokenSource();

        Task<int> processing = service.ProcessBackgroundBatchAsync(
            stopping.Token);
        await started.Task;
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processing);
        Assert.Single(repository.Released);
        Assert.Empty(repository.Cancelled);
    }

    [Fact]
    public async Task ConcurrentQueuePassesNeverRunMoreThanOneExporter()
    {
        FeedEntry entry = Entry();
        EntryExportTaskLease firstLease = Lease(entry);
        FeedEntry secondEntry = Entry(
            id: "entry-export-43",
            contentHash: new string('c', 64));
        EntryExportTaskLease secondLease = Lease(secondEntry);
        var repository = new StubRepository(firstLease, secondLease);
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximumActive = 0;
        var coordinator = new StubCoordinator
        {
            Handler = async (request, _) =>
            {
                int current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                if (request.Entry.Id == firstLease.EntryId)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
                Interlocked.Decrement(ref active);
                return EntryExportResult.Success(
                    request.IdempotencyKey,
                    $"{request.Entry.Id}.md",
                    remoteUrl: null);
            }
        };
        var entries = new Dictionary<string, FeedEntry>(
            StringComparer.Ordinal)
        {
            [entry.Id] = entry,
            [secondEntry.Id] = secondEntry
        };
        EntryExportQueueService service = CreateService(
            repository,
            coordinator,
            entries);

        Task<int> first = service.ProcessBackgroundBatchAsync(
            CancellationToken.None);
        await firstStarted.Task;
        Task<int> second = service.ProcessBackgroundBatchAsync(
            CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(1, repository.ClaimCount);
        releaseFirst.SetResult();

        int[] results = await Task.WhenAll(first, second);
        Assert.Equal([1, 1], results);
        Assert.Equal(1, maximumActive);
        Assert.Equal(2, repository.Completed.Count);
    }

    private static EntryExportQueueService CreateService(
        StubRepository repository,
        StubCoordinator coordinator,
        FeedEntry entry) =>
        CreateService(
            repository,
            coordinator,
            new Dictionary<string, FeedEntry>(StringComparer.Ordinal)
            {
                [entry.Id] = entry
            });

    private static EntryExportQueueService CreateService(
        StubRepository repository,
        StubCoordinator coordinator,
        IReadOnlyDictionary<string, FeedEntry> entries) =>
        new(
            repository,
            new StubFeedEntryRepository(entries),
            coordinator,
            new FixedTimeProvider(Now),
            Options);

    private static EntryExportTaskLease Lease(FeedEntry entry)
    {
        EntryExportRequest request = Request(entry);
        return new(
            request.IdempotencyKey,
            request.ExporterId,
            request.TargetId,
            entry.Id,
            entry.ContentHash,
            request.ViewKind,
            request.ContentBytes,
            AttemptCount: 1,
            Guid.NewGuid().ToString());
    }

    private static EntryExportRequest Request(FeedEntry entry) =>
        EntryExportRequest.Create(
            "markdown",
            "knowledge-base",
            entry,
            EntryViewKind.Article,
            128);

    private static FeedEntry Entry(
        string id = "entry-export-42",
        string? contentHash = null) =>
        new(
            id,
            "30000000-0000-4000-8000-000000000001",
            $"external-{id}",
            $"https://example.com/articles/{id}",
            "导出队列测试",
            "作者",
            Now.AddDays(-1),
            null,
            "摘要",
            "<p>正文</p>",
            ["RSS"],
            [],
            contentHash ?? new string('a', 64),
            Now);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubCoordinator : IEntryExportCoordinator
    {
        public IReadOnlyList<EntryExportCapability> Capabilities { get; init; } =
        [
            new(
                "markdown",
                "Markdown",
                Enum.GetValues<EntryViewKind>(),
                RequiresCredentials: false,
                MaximumContentBytes: null,
                IsIdempotent: true)
        ];

        public required Func<
            EntryExportRequest,
            CancellationToken,
            Task<EntryExportResult>> Handler
        { get; init; }

        public int CallCount { get; private set; }

        public Task<EntryExportResult> ExportAsync(
            EntryExportRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Handler(request, cancellationToken);
        }
    }

    private sealed class StubFeedEntryRepository(
        IReadOnlyDictionary<string, FeedEntry> entries)
        : IFeedEntryRepository
    {
        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken) =>
            Task.FromResult(entries.GetValueOrDefault(entryId));

        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> newEntries,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubRepository(
        params EntryExportTaskLease[] leases)
        : IEntryExportTaskRepository
    {
        private readonly Queue<EntryExportTaskLease> _leases = new(leases);
        private volatile bool _cancellationRequested;

        public int ClaimCount { get; private set; }

        public int EnqueueCallCount { get; private set; }

        public int RenewCount { get; private set; }

        public List<ScheduledRetry> Retries { get; } = [];

        public List<FailedTask> Failures { get; } = [];

        public List<EntryExportTaskLease> Cancelled { get; } = [];

        public List<EntryExportTaskLease> Released { get; } = [];

        public List<EntryExportTaskLease> Completed { get; } = [];

        public Task<EntryExportEnqueueResult> EnqueueAsync(
            EntryExportRequest request,
            DateTimeOffset enqueuedAt,
            CancellationToken cancellationToken)
        {
            EnqueueCallCount++;
            throw new NotSupportedException();
        }

        public Task<EntryExportTaskLease?> ClaimDueAsync(
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            ClaimCount++;
            return Task.FromResult(
                _leases.Count == 0 ? null : _leases.Dequeue());
        }

        public Task<bool> IsCancellationRequestedAsync(
            EntryExportTaskLease task,
            CancellationToken cancellationToken) =>
            Task.FromResult(_cancellationRequested);

        public Task<bool> RenewLeaseAsync(
            EntryExportTaskLease task,
            DateTimeOffset renewedAt,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken)
        {
            RenewCount++;
            return Task.FromResult(true);
        }

        public Task CompleteAsync(
            EntryExportTaskLease task,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            Completed.Add(task);
            return Task.CompletedTask;
        }

        public Task FailAsync(
            EntryExportTaskLease task,
            EntryExportTaskErrorCode errorCode,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken)
        {
            Failures.Add(new(task, errorCode, failedAt));
            return Task.CompletedTask;
        }

        public Task ScheduleRetryAsync(
            EntryExportTaskLease task,
            EntryExportTaskErrorCode errorCode,
            DateTimeOffset nextAttemptAt,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken)
        {
            Retries.Add(new(task, errorCode, nextAttemptAt, failedAt));
            return Task.CompletedTask;
        }

        public Task CancelClaimedAsync(
            EntryExportTaskLease task,
            DateTimeOffset cancelledAt,
            CancellationToken cancellationToken)
        {
            Cancelled.Add(task);
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(
            EntryExportTaskLease task,
            DateTimeOffset releasedAt,
            CancellationToken cancellationToken)
        {
            Released.Add(task);
            return Task.CompletedTask;
        }

        public Task<EntryExportCancellationResult> RequestCancellationAsync(
            string idempotencyKey,
            DateTimeOffset requestedAt,
            CancellationToken cancellationToken)
        {
            _cancellationRequested = true;
            return Task.FromResult(
                EntryExportCancellationResult.CancellationRequested);
        }

        public Task<EntryExportTask?> GetAsync(
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<EntryExportTask?>(null);

        public Task<IReadOnlyList<EntryExportTask>> GetRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntryExportTask>>([]);
    }

    private sealed record ScheduledRetry(
        EntryExportTaskLease Task,
        EntryExportTaskErrorCode ErrorCode,
        DateTimeOffset NextAttemptAt,
        DateTimeOffset FailedAt);

    private sealed record FailedTask(
        EntryExportTaskLease Task,
        EntryExportTaskErrorCode ErrorCode,
        DateTimeOffset FailedAt);
}
