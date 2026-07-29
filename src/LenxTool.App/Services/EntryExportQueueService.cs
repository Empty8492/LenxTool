using System.Collections.Concurrent;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public sealed class EntryExportQueueService :
    IEntryExportQueueService,
    IEntryExportQueueProcessor,
    IDisposable
{
    private readonly IEntryExportTaskRepository _repository;
    private readonly IFeedEntryRepository _entries;
    private readonly IEntryExportCoordinator _coordinator;
    private readonly TimeProvider _timeProvider;
    private readonly EntryExportQueueOptions _options;
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource>
        _runningCancellations = new(StringComparer.Ordinal);
    private bool _disposed;

    public EntryExportQueueService(
        IEntryExportTaskRepository repository,
        IFeedEntryRepository entries,
        IEntryExportCoordinator coordinator,
        TimeProvider timeProvider,
        EntryExportQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateOptions(options);
        _repository = repository;
        _entries = entries;
        _coordinator = coordinator;
        _timeProvider = timeProvider;
        _options = options;
    }

    public Task<EntryExportEnqueueResult> EnqueueAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        EntryExportCapability? capability =
            FindCapability(request.ExporterId);
        if (capability is null)
        {
            throw new InvalidOperationException(
                "The requested export adapter is not registered.");
        }
        if (!capability.IsIdempotent)
        {
            // 持久化租约只能提供至少一次执行；非幂等适配器在写后崩溃时
            // 无法安全重放，因此必须在进入队列前失败关闭。
            throw new InvalidOperationException(
                "Non-idempotent export adapters cannot use the durable queue.");
        }
        return _repository.EnqueueAsync(
            request,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public async Task<EntryExportCancellationResult> CancelAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EntryExportCancellationResult result =
            await _repository.RequestCancellationAsync(
                idempotencyKey,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        if (result == EntryExportCancellationResult.CancellationRequested
            && _runningCancellations.TryGetValue(
                idempotencyKey,
                out CancellationTokenSource? running))
        {
            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 执行器刚完成清理；数据库中的取消标记仍会阻止它覆盖终态。
            }
        }
        return result;
    }

    public Task<EntryExportTask?> GetAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _repository.GetAsync(idempotencyKey, cancellationToken);
    }

    public Task<IReadOnlyList<EntryExportTask>> GetRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _repository.GetRecentAsync(maximumCount, cancellationToken);
    }

    public async Task<int> ProcessBackgroundBatchAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _processingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EntryExportTaskLease? task = await _repository.ClaimDueAsync(
                _timeProvider.GetUtcNow(),
                _options.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                return 0;
            }

            await ProcessClaimedAsync(task, cancellationToken)
                .ConfigureAwait(false);
            return 1;
        }
        finally
        {
            _processingGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (CancellationTokenSource cancellation
                 in _runningCancellations.Values)
        {
            cancellation.Cancel();
        }
        _processingGate.Dispose();
    }

    private async Task ProcessClaimedAsync(
        EntryExportTaskLease task,
        CancellationToken stoppingToken)
    {
        using var taskCancellation = new CancellationTokenSource();
        using var leaseOwnershipLost = new CancellationTokenSource();
        using var heartbeatStop = new CancellationTokenSource();
        if (!_runningCancellations.TryAdd(
                task.IdempotencyKey,
                taskCancellation))
        {
            await TryReleaseAsync(task).ConfigureAwait(false);
            throw new InvalidOperationException(
                "The export task is already running in this process.");
        }

        Task heartbeat = MaintainLeaseAsync(
            task,
            taskCancellation,
            leaseOwnershipLost,
            heartbeatStop.Token);
        try
        {
            if (await _repository.IsCancellationRequestedAsync(
                    task,
                    stoppingToken).ConfigureAwait(false))
            {
                taskCancellation.Cancel();
            }
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    taskCancellation.Token,
                    leaseOwnershipLost.Token);
            try
            {
                EntryExportCapability? capability =
                    FindCapability(task.ExporterId);
                if (capability is null)
                {
                    await TryFailAsync(
                        task,
                        EntryExportTaskErrorCode.ExporterNotFound,
                        linked.Token).ConfigureAwait(false);
                    return;
                }
                if (!capability.IsIdempotent)
                {
                    await TryFailAsync(
                        task,
                        EntryExportTaskErrorCode.InvalidRequest,
                        linked.Token).ConfigureAwait(false);
                    return;
                }

                FeedEntry? entry = await _entries.GetByIdAsync(
                    task.EntryId,
                    linked.Token).ConfigureAwait(false);
                if (entry is null)
                {
                    await TryFailAsync(
                        task,
                        EntryExportTaskErrorCode.EntryMissing,
                        linked.Token).ConfigureAwait(false);
                    return;
                }
                if (!string.Equals(
                        entry.ContentHash,
                        task.ContentHash,
                        StringComparison.Ordinal))
                {
                    await TryFailAsync(
                        task,
                        EntryExportTaskErrorCode.EntryChanged,
                        linked.Token).ConfigureAwait(false);
                    return;
                }

                EntryExportRequest request = EntryExportRequest.Create(
                    task.ExporterId,
                    task.TargetId,
                    entry,
                    task.ViewKind,
                    task.ContentBytes);
                if (!string.Equals(
                        request.IdempotencyKey,
                        task.IdempotencyKey,
                        StringComparison.Ordinal))
                {
                    await TryFailAsync(
                        task,
                        EntryExportTaskErrorCode.InvalidRequest,
                        linked.Token).ConfigureAwait(false);
                    return;
                }

                EntryExportResult result = await _coordinator.ExportAsync(
                    request,
                    linked.Token).ConfigureAwait(false);
                if (leaseOwnershipLost.IsCancellationRequested)
                {
                    return;
                }
                if (result.Succeeded)
                {
                    await TryCompleteAsync(
                        task).ConfigureAwait(false);
                }
                else
                {
                    if (await TryHonorCancellationAsync(task)
                            .ConfigureAwait(false))
                    {
                        return;
                    }
                    EntryExportError error = result.Error
                        ?? new(
                            EntryExportErrorCode.Unknown,
                            IsRetryable: true);
                    await RetryOrFailAsync(
                        task,
                        MapErrorCode(error.Code),
                        error.IsRetryable,
                        error.RetryAfter,
                        linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
                when (taskCancellation.IsCancellationRequested)
            {
                await TryCancelAsync(task).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                await TryReleaseAsync(task).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
                when (leaseOwnershipLost.IsCancellationRequested)
            {
                // 另一个进程已取得租约时，旧执行器只能停止，不能再写任务状态。
            }
            catch (Exception)
            {
                await RetryOrFailAsync(
                    task,
                    EntryExportTaskErrorCode.Unknown,
                    retryable: true,
                    retryAfter: null,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat.ConfigureAwait(false);
            _runningCancellations.TryRemove(
                task.IdempotencyKey,
                out _);
        }
    }

    private async Task MaintainLeaseAsync(
        EntryExportTaskLease task,
        CancellationTokenSource taskCancellation,
        CancellationTokenSource leaseOwnershipLost,
        CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromTicks(
            Math.Max(1, _options.LeaseDuration.Ticks / 3));
        try
        {
            while (true)
            {
                await Task.Delay(interval, cancellationToken)
                    .ConfigureAwait(false);
                DateTimeOffset renewedAt = _timeProvider.GetUtcNow();
                bool renewed = await _repository.RenewLeaseAsync(
                    task,
                    renewedAt,
                    renewedAt.Add(_options.LeaseDuration),
                    cancellationToken).ConfigureAwait(false);
                if (!renewed)
                {
                    leaseOwnershipLost.Cancel();
                    return;
                }
                if (await _repository.IsCancellationRequestedAsync(
                        task,
                        cancellationToken).ConfigureAwait(false))
                {
                    // 取消可能由另一个进程写入；心跳负责把持久化信号桥接到
                    // 当前适配器的合作取消令牌，避免长任务持续续租而无法收敛。
                    taskCancellation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // 正常完成、取消或退出都会停止心跳。
        }
        catch
        {
            // 无法确认租约仍归当前进程时失败关闭，避免超过到期时间继续外投。
            leaseOwnershipLost.Cancel();
        }
    }

    private async Task RetryOrFailAsync(
        EntryExportTaskLease task,
        EntryExportTaskErrorCode errorCode,
        bool retryable,
        TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        if (await TryHonorCancellationAsync(task).ConfigureAwait(false))
        {
            return;
        }
        if (!retryable || task.AttemptCount >= _options.MaximumAttempts)
        {
            await TryFailAsync(task, errorCode, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        DateTimeOffset failedAt = _timeProvider.GetUtcNow();
        TimeSpan delay = retryAfter is null
            ? GetRetryDelay(task.AttemptCount)
            : ClampExplicitRetryDelay(retryAfter.Value);
        try
        {
            await _repository.ScheduleRetryAsync(
                task,
                errorCode,
                failedAt.Add(delay),
                failedAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await TryHonorCancellationAsync(task).ConfigureAwait(false);
        }
    }

    private async Task TryCompleteAsync(
        EntryExportTaskLease task)
    {
        try
        {
            await _repository.CompleteAsync(
                task,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await TryHonorCancellationAsync(task).ConfigureAwait(false);
        }
    }

    private async Task TryFailAsync(
        EntryExportTaskLease task,
        EntryExportTaskErrorCode errorCode,
        CancellationToken cancellationToken)
    {
        if (await TryHonorCancellationAsync(task).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await _repository.FailAsync(
                task,
                errorCode,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await TryHonorCancellationAsync(task).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryHonorCancellationAsync(
        EntryExportTaskLease task)
    {
        try
        {
            if (!await _repository.IsCancellationRequestedAsync(
                    task,
                    CancellationToken.None).ConfigureAwait(false))
            {
                return false;
            }
            await TryCancelAsync(task).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task TryCancelAsync(EntryExportTaskLease task)
    {
        try
        {
            await _repository.CancelClaimedAsync(
                task,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // 过期租约可能已被另一个进程收敛；旧执行器不得覆盖新状态。
        }
    }

    private async Task TryReleaseAsync(EntryExportTaskLease task)
    {
        try
        {
            await _repository.ReleaseAsync(
                task,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 退出阶段只做尽力释放；失败时持久化租约到期后仍可恢复。
        }
    }

    private TimeSpan GetRetryDelay(int attemptCount)
    {
        int exponent = Math.Min(20, Math.Max(0, attemptCount - 1));
        double ticks = Math.Min(
            _options.MaximumRetryDelay.Ticks,
            _options.BaseRetryDelay.Ticks * Math.Pow(2, exponent));
        return TimeSpan.FromTicks((long)ticks);
    }

    private TimeSpan ClampExplicitRetryDelay(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        return value > _options.MaximumRetryDelay
            ? _options.MaximumRetryDelay
            : value;
    }

    private static EntryExportTaskErrorCode MapErrorCode(
        EntryExportErrorCode value) =>
        value switch
        {
            EntryExportErrorCode.InvalidRequest =>
                EntryExportTaskErrorCode.InvalidRequest,
            EntryExportErrorCode.ExporterNotFound =>
                EntryExportTaskErrorCode.ExporterNotFound,
            EntryExportErrorCode.UnsupportedContent =>
                EntryExportTaskErrorCode.UnsupportedContent,
            EntryExportErrorCode.CredentialsRequired =>
                EntryExportTaskErrorCode.CredentialsRequired,
            EntryExportErrorCode.ContentTooLarge =>
                EntryExportTaskErrorCode.ContentTooLarge,
            EntryExportErrorCode.RateLimited =>
                EntryExportTaskErrorCode.RateLimited,
            EntryExportErrorCode.DestinationUnavailable =>
                EntryExportTaskErrorCode.DestinationUnavailable,
            EntryExportErrorCode.AccessDenied =>
                EntryExportTaskErrorCode.AccessDenied,
            EntryExportErrorCode.Conflict =>
                EntryExportTaskErrorCode.Conflict,
            EntryExportErrorCode.ProviderRejected =>
                EntryExportTaskErrorCode.ProviderRejected,
            EntryExportErrorCode.Unknown =>
                EntryExportTaskErrorCode.Unknown,
            _ => EntryExportTaskErrorCode.Unknown
        };

    private EntryExportCapability? FindCapability(string exporterId) =>
        _coordinator.Capabilities.SingleOrDefault(item => string.Equals(
            item.ExporterId,
            exporterId,
            StringComparison.Ordinal));

    private static void ValidateOptions(EntryExportQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumAttempts is < 1 or > 20
            || options.LeaseDuration <= TimeSpan.Zero
            || options.LeaseDuration > TimeSpan.FromHours(1)
            || options.PollInterval <= TimeSpan.Zero
            || options.BaseRetryDelay <= TimeSpan.Zero
            || options.MaximumRetryDelay < options.BaseRetryDelay
            || options.MaximumRetryDelay > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
