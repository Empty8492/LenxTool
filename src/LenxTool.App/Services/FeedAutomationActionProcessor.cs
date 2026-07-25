using System.IO;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public sealed class FeedAutomationActionProcessor :
    IFeedAutomationActionProcessor
{
    private static readonly IReadOnlyCollection<FeedAutomationActionType>
        LocalActionTypes = Array.AsReadOnly<FeedAutomationActionType>(
        [
            FeedAutomationActionType.AddTag,
            FeedAutomationActionType.Hide,
            FeedAutomationActionType.MarkRead
        ]);

    private readonly IFeedAutomationActionQueueRepository _queue;
    private readonly IFeedAutomationLocalActionService _localActions;
    private readonly TimeProvider _timeProvider;
    private readonly FeedAutomationActionProcessorOptions _options;

    public FeedAutomationActionProcessor(
        IFeedAutomationActionQueueRepository queue,
        IFeedAutomationLocalActionService localActions,
        TimeProvider timeProvider,
        FeedAutomationActionProcessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(localActions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateOptions(options);
        _queue = queue;
        _localActions = localActions;
        _timeProvider = timeProvider;
        _options = options;
    }

    public async Task<int> ProcessBackgroundBatchAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FeedAutomationActionLease> claimed =
            await _queue.ClaimDueAsync(
                _timeProvider.GetUtcNow(),
                LocalActionTypes,
                _options.BatchSize,
                _options.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return 0;
        }

        using var gate = new SemaphoreSlim(
            _options.MaximumConcurrency,
            _options.MaximumConcurrency);
        await Task.WhenAll(claimed.Select(action =>
            ProcessWithGateAsync(
                action,
                gate,
                cancellationToken))).ConfigureAwait(false);
        return claimed.Count;
    }

    private async Task ProcessWithGateAsync(
        FeedAutomationActionLease action,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await ProcessClaimedAsync(action, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAfterCancellationAsync(action).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (entered)
            {
                gate.Release();
            }
        }
    }

    private async Task ProcessClaimedAsync(
        FeedAutomationActionLease action,
        CancellationToken cancellationToken)
    {
        try
        {
            FeedAutomationLocalActionResult result =
                await _localActions.ExecuteAsync(
                    action,
                    cancellationToken).ConfigureAwait(false);
            if (result == FeedAutomationLocalActionResult.Completed)
            {
                await TryCompleteAsync(
                    action,
                    FeedAutomationActionRunOutcome.Succeeded,
                    errorCode: null,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (result == FeedAutomationLocalActionResult.EntryMissing)
            {
                await TryCompleteAsync(
                    action,
                    FeedAutomationActionRunOutcome.Failed,
                    "ENTRY_MISSING",
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await TryCompleteAsync(
                    action,
                    FeedAutomationActionRunOutcome.Failed,
                    "INVALID_ACTION",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AppException exception)
        {
            string errorCode = exception.Error.Code
                .ToString()
                .ToUpperInvariant();
            if (exception.Error.IsRetryable)
            {
                await RetryOrCompleteAsync(
                    action,
                    errorCode,
                    exception.Error.RetryAfter,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await TryCompleteAsync(
                    action,
                    FeedAutomationActionRunOutcome.Failed,
                    errorCode,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidDataException)
        {
            await TryCompleteAsync(
                action,
                FeedAutomationActionRunOutcome.Failed,
                "INVALID_ACTION",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await RetryOrCompleteAsync(
                action,
                "UNEXPECTED_ERROR",
                retryAfter: null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RetryOrCompleteAsync(
        FeedAutomationActionLease action,
        string errorCode,
        TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        if (action.AttemptCount >= _options.MaximumAttempts)
        {
            await TryCompleteAsync(
                action,
                FeedAutomationActionRunOutcome.Failed,
                errorCode,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        DateTimeOffset failedAt = _timeProvider.GetUtcNow();
        TimeSpan delay = retryAfter is null
            ? GetRetryDelay(action.AttemptCount)
            : ClampRetryDelay(retryAfter.Value);
        try
        {
            await _queue.ScheduleRetryAsync(
                action,
                errorCode,
                failedAt.Add(delay),
                failedAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The lease expired and another processor already reclaimed it.
        }
    }

    private async Task TryCompleteAsync(
        FeedAutomationActionLease action,
        FeedAutomationActionRunOutcome outcome,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await _queue.CompleteAsync(
                action,
                outcome,
                errorCode,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The lease expired and another processor already reclaimed it.
        }
    }

    private async Task ReleaseAfterCancellationAsync(
        FeedAutomationActionLease action)
    {
        try
        {
            await _queue.ReleaseAsync(
                action,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The durable lease expires if shutdown interrupts this best-effort release.
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

    private TimeSpan ClampRetryDelay(TimeSpan value)
    {
        if (value < _options.BaseRetryDelay)
        {
            return _options.BaseRetryDelay;
        }
        return value > _options.MaximumRetryDelay
            ? _options.MaximumRetryDelay
            : value;
    }

    private static void ValidateOptions(
        FeedAutomationActionProcessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BatchSize is < 1 or > 200
            || options.MaximumConcurrency is < 1 or > 8
            || options.MaximumAttempts is < 1 or > 20
            || options.LeaseDuration <= TimeSpan.Zero
            || options.LeaseDuration > TimeSpan.FromHours(1)
            || options.InitialDelay < TimeSpan.Zero
            || options.PollInterval <= TimeSpan.Zero
            || options.BaseRetryDelay <= TimeSpan.Zero
            || options.MaximumRetryDelay < options.BaseRetryDelay
            || options.MaximumRetryDelay > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
