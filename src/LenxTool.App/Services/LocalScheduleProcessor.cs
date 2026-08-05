using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

/// <summary>
/// 单并发驱动已注册的本地计划处理器，并把持久租约、重启恢复和计划代际取消
/// 隔离在业务处理器之外。具体任务只能看到窗口身份，不能直接提交仓储状态。
/// </summary>
public sealed class LocalScheduleProcessor :
    ILocalScheduleProcessor,
    IDisposable
{
    private readonly ILocalScheduleRunRepository _repository;
    private readonly Dictionary<string, ILocalScheduledTaskHandler> _handlers;
    private readonly string[] _eligibleScheduleIds;
    private readonly TimeProvider _timeProvider;
    private readonly LocalScheduleProcessorOptions _options;
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private bool _disposed;

    public LocalScheduleProcessor(
        ILocalScheduleRunRepository repository,
        IEnumerable<ILocalScheduledTaskHandler> handlers,
        TimeProvider timeProvider,
        LocalScheduleProcessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateOptions(options);

        var validatedHandlers =
            new Dictionary<string, ILocalScheduledTaskHandler>(
                StringComparer.Ordinal);
        foreach (ILocalScheduledTaskHandler handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            string scheduleId = ValidateScheduleId(handler.ScheduleId);
            if (!handler.IsIdempotent)
            {
                throw new InvalidOperationException(
                    "非幂等本地任务不能注册到持久计划处理器。");
            }
            if (!validatedHandlers.TryAdd(scheduleId, handler))
            {
                throw new InvalidOperationException(
                    "同一个本地计划 ID 只能注册一个处理器。");
            }
        }

        _repository = repository;
        _handlers = validatedHandlers;
        _eligibleScheduleIds = validatedHandlers.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
        _timeProvider = timeProvider;
        _options = options;
    }

    public async Task<int> ProcessBackgroundBatchAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_eligibleScheduleIds.Length == 0)
        {
            return 0;
        }

        await _processingGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
            LocalScheduleRunLease? lease =
                await _repository.ClaimDueAsync(
                    _eligibleScheduleIds,
                    nowUtc,
                    nowUtc.Subtract(_options.MissedRunGracePeriod),
                    _options.LeaseDuration,
                    cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return 0;
            }

            if (!_handlers.TryGetValue(
                    lease.ScheduleId,
                    out ILocalScheduledTaskHandler? handler))
            {
                await TryReleaseAsync(lease).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "领取到的本地计划没有对应处理器。");
            }

            await ProcessClaimedAsync(
                lease,
                handler,
                cancellationToken).ConfigureAwait(false);
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
        _processingGate.Dispose();
    }

    private async Task ProcessClaimedAsync(
        LocalScheduleRunLease lease,
        ILocalScheduledTaskHandler handler,
        CancellationToken stoppingToken)
    {
        using var taskCancellation = new CancellationTokenSource();
        using var leaseOwnershipLost = new CancellationTokenSource();
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = MaintainLeaseAsync(
            lease,
            taskCancellation,
            leaseOwnershipLost,
            heartbeatStop.Token);
        try
        {
            try
            {
                if (await IsCancellationRequestedAsync(
                        lease,
                        stoppingToken).ConfigureAwait(false))
                {
                    taskCancellation.Cancel();
                }

                using CancellationTokenSource linked =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        stoppingToken,
                        taskCancellation.Token,
                        leaseOwnershipLost.Token);
                linked.Token.ThrowIfCancellationRequested();
                await handler.ExecuteAsync(
                    new LocalScheduleExecution(
                        lease.ScheduleId,
                        lease.ScheduledForUtc,
                        lease.AttemptCount),
                    linked.Token).ConfigureAwait(false);
                if (leaseOwnershipLost.IsCancellationRequested)
                {
                    return;
                }
                if (await TryHonorCancellationAsync(lease)
                        .ConfigureAwait(false))
                {
                    return;
                }
                await TryCompleteAsync(lease).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (taskCancellation.IsCancellationRequested)
            {
                await TryCancelAsync(lease).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                await TryReleaseAsync(lease).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
                when (leaseOwnershipLost.IsCancellationRequested)
            {
                // 租约已失效时旧处理器只能退出，不能覆盖新 owner 的状态。
            }
            catch
            {
                if (!await TryHonorCancellationAsync(lease)
                        .ConfigureAwait(false))
                {
                    await TryReleaseAsync(lease).ConfigureAwait(false);
                }
                throw;
            }
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat.ConfigureAwait(false);
        }
    }

    private async Task MaintainLeaseAsync(
        LocalScheduleRunLease lease,
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
                await Task.Delay(
                        interval,
                        _timeProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
                DateTimeOffset renewedAtUtc = _timeProvider.GetUtcNow();
                bool renewed = await _repository.RenewLeaseAsync(
                    lease,
                    renewedAtUtc,
                    renewedAtUtc.Add(_options.LeaseDuration),
                    cancellationToken).ConfigureAwait(false);
                if (!renewed)
                {
                    leaseOwnershipLost.Cancel();
                    return;
                }
                if (await IsCancellationRequestedAsync(
                        lease,
                        cancellationToken).ConfigureAwait(false))
                {
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
            // 无法确认租约仍归当前进程时失败关闭，避免旧 owner 继续副作用。
            leaseOwnershipLost.Cancel();
        }
    }

    private Task<bool> IsCancellationRequestedAsync(
        LocalScheduleRunLease lease,
        CancellationToken cancellationToken) =>
        _repository.IsCancellationRequestedAsync(
            lease,
            _timeProvider.GetUtcNow(),
            cancellationToken);

    private async Task<bool> TryHonorCancellationAsync(
        LocalScheduleRunLease lease)
    {
        try
        {
            if (!await IsCancellationRequestedAsync(
                    lease,
                    CancellationToken.None).ConfigureAwait(false))
            {
                return false;
            }
            await TryCancelAsync(lease).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task TryCompleteAsync(LocalScheduleRunLease lease)
    {
        try
        {
            await _repository.CompleteAsync(
                lease,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await TryHonorCancellationAsync(lease).ConfigureAwait(false);
        }
    }

    private async Task TryCancelAsync(LocalScheduleRunLease lease)
    {
        try
        {
            await _repository.CancelAsync(
                lease,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // 已过期窗口会由下一 owner 按持久计划代际收敛，旧 owner 不覆盖。
        }
    }

    private async Task TryReleaseAsync(LocalScheduleRunLease lease)
    {
        try
        {
            await _repository.ReleaseAsync(
                lease,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 退出与异常路径尽力释放；失败时租约到期后仍能持久恢复。
        }
    }

    private static string ValidateScheduleId(string scheduleId)
    {
        if (!Guid.TryParseExact(scheduleId, "D", out Guid parsed)
            || !string.Equals(
                parsed.ToString("D"),
                scheduleId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "本地计划处理器 ID 必须是规范的小写 GUID。",
                nameof(scheduleId));
        }
        return scheduleId;
    }

    private static void ValidateOptions(LocalScheduleProcessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.LeaseDuration <= TimeSpan.Zero
            || options.LeaseDuration > TimeSpan.FromHours(1)
            || options.MissedRunGracePeriod < TimeSpan.Zero
            || options.MissedRunGracePeriod > TimeSpan.FromDays(30)
            || options.PollInterval <= TimeSpan.Zero
            || options.PollInterval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
