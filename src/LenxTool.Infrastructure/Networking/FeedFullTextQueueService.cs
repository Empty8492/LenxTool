using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed class FeedFullTextQueueService :
    IFeedFullTextQueueService,
    IDisposable
{
    private readonly IFeedFullTextRepository _repository;
    private readonly IArticleContentExtractor _extractor;
    private readonly FeedFullTextQueueOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _globalGate;
    private readonly PerHostConcurrencyLimiter _hostLimiter;
    private bool _disposed;

    public FeedFullTextQueueService(
        IFeedFullTextRepository repository,
        IArticleContentExtractor extractor,
        FeedFullTextQueueOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateOptions(options);
        _repository = repository;
        _extractor = extractor;
        _options = options;
        _timeProvider = timeProvider;
        _globalGate = new(options.MaximumConcurrency, options.MaximumConcurrency);
        _hostLimiter = new(options.MaximumConcurrencyPerHost);
    }

    public async Task<FeedFullTextContent?> FetchOnOpenAsync(
        string entryId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FeedFullTextContent? existing = await _repository
            .GetContentAsync(entryId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        FeedFullTextWorkItem? workItem = await _repository.ClaimOnOpenAsync(
            entryId,
            now,
            _options.LeaseDuration,
            cancellationToken).ConfigureAwait(false);
        if (workItem is null)
        {
            return null;
        }

        await ProcessWorkItemAsync(workItem, cancellationToken).ConfigureAwait(false);
        return await _repository.GetContentAsync(entryId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ProcessBackgroundBatchAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<FeedFullTextWorkItem> workItems = await _repository
            .ClaimBackgroundAsync(
                _timeProvider.GetUtcNow(),
                _options.BatchSize,
                _options.LeaseDuration,
                cancellationToken)
            .ConfigureAwait(false);
        if (workItems.Count == 0)
        {
            return 0;
        }

        await Task.WhenAll(workItems.Select(item =>
            ProcessWorkItemAsync(item, cancellationToken))).ConfigureAwait(false);
        return workItems.Count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hostLimiter.Dispose();
        _globalGate.Dispose();
    }

    private async Task ProcessWorkItemAsync(
        FeedFullTextWorkItem workItem,
        CancellationToken cancellationToken)
    {
        bool globalLeaseAcquired = false;
        try
        {
            await _globalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            globalLeaseAcquired = true;
            using PerHostConcurrencyLimiter.Lease hostLease = await _hostLimiter
                .AcquireAsync(workItem.Host, cancellationToken).ConfigureAwait(false);
            try
            {
                ArticleContentResult article = await _extractor
                    .ExtractAsync(workItem.Url, cancellationToken).ConfigureAwait(false);
                DateTimeOffset completedAt = _timeProvider.GetUtcNow();
                if (article.Blocks.Count == 0
                    || article.Warnings.Any(warning =>
                        warning.Code == ArticleExtractionWarningCode.NoReadableContent))
                {
                    await _repository.BlockAsync(
                        workItem,
                        "NO_READABLE_CONTENT",
                        completedAt,
                        completedAt.Add(_options.MaximumRetryDelay),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                await _repository.SaveContentAsync(
                    workItem,
                    article,
                    completedAt,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AppException exception)
            {
                await SaveAppFailureAsync(workItem, exception.Error, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                DateTimeOffset failedAt = _timeProvider.GetUtcNow();
                await _repository.ScheduleRetryAsync(
                    workItem,
                    "UNEXPECTED_ERROR",
                    failedAt.Add(GetRetryDelay(workItem.AttemptCount)),
                    failedAt,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAfterCancellationAsync(workItem).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (globalLeaseAcquired)
            {
                _globalGate.Release();
            }
        }
    }

    private async Task SaveAppFailureAsync(
        FeedFullTextWorkItem workItem,
        AppError error,
        CancellationToken cancellationToken)
    {
        DateTimeOffset failedAt = _timeProvider.GetUtcNow();
        string errorCode = error.Code.ToString().ToUpperInvariant();
        if (IsBlocked(error))
        {
            await _repository.BlockAsync(
                workItem,
                errorCode,
                failedAt,
                failedAt.Add(_options.MaximumRetryDelay),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        TimeSpan retryDelay = error.RetryAfter is { } retryAfter
            ? ClampRetryDelay(retryAfter)
            : GetRetryDelay(workItem.AttemptCount);
        await _repository.ScheduleRetryAsync(
            workItem,
            errorCode,
            failedAt.Add(retryDelay),
            failedAt,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ReleaseAfterCancellationAsync(FeedFullTextWorkItem workItem)
    {
        try
        {
            await _repository.ReleaseAsync(
                workItem,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The lease expires and becomes claimable even if shutdown prevents this best-effort release.
        }
    }

    private TimeSpan GetRetryDelay(int attemptCount)
    {
        int exponent = Math.Min(20, Math.Max(0, attemptCount));
        double multiplier = Math.Pow(2, exponent);
        double ticks = Math.Min(
            _options.MaximumRetryDelay.Ticks,
            _options.BaseRetryDelay.Ticks * multiplier);
        return TimeSpan.FromTicks((long)ticks);
    }

    private TimeSpan ClampRetryDelay(TimeSpan retryAfter)
    {
        if (retryAfter < _options.BaseRetryDelay) return _options.BaseRetryDelay;
        if (retryAfter > _options.MaximumRetryDelay) return _options.MaximumRetryDelay;
        return retryAfter;
    }

    private static bool IsBlocked(AppError error) =>
        !error.IsRetryable
        && error.Code is
            AppErrorCode.InvalidRequest
            or AppErrorCode.CredentialsInvalid
            or AppErrorCode.AccessDenied
            or AppErrorCode.FileAccessDenied;

    private static void ValidateOptions(FeedFullTextQueueOptions options)
    {
        if (options.BatchSize is < 1 or > 100
            || options.MaximumConcurrency is < 1 or > 8
            || options.MaximumConcurrencyPerHost < 1
            || options.MaximumConcurrencyPerHost > options.MaximumConcurrency
            || options.LeaseDuration <= TimeSpan.Zero
            || options.LeaseDuration > TimeSpan.FromHours(1)
            || options.BaseRetryDelay <= TimeSpan.Zero
            || options.MaximumRetryDelay < options.BaseRetryDelay
            || options.MaximumRetryDelay > TimeSpan.FromDays(1)
            || options.InitialDelay < TimeSpan.Zero
            || options.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
