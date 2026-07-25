using System.Collections.Concurrent;
using System.IO;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public sealed class FeedAiAutomationQueueService :
    IFeedAiAutomationQueueService,
    IDisposable
{
    private readonly IFeedAiAutomationJobRepository _jobs;
    private readonly IFeedCatalogRepository _catalogRepository;
    private readonly IFeedEntryRepository _entryRepository;
    private readonly IFeedAiSummaryService _summaryService;
    private readonly IFeedAiTranslationService _translationService;
    private readonly TimeProvider _timeProvider;
    private readonly FeedAiAutomationOptions _options;
    private readonly SemaphoreSlim _globalGate;
    private bool _disposed;

    public FeedAiAutomationQueueService(
        IFeedAiAutomationJobRepository jobs,
        IFeedCatalogRepository catalogRepository,
        IFeedEntryRepository entryRepository,
        IFeedAiSummaryService summaryService,
        IFeedAiTranslationService translationService,
        TimeProvider timeProvider,
        FeedAiAutomationOptions options)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(catalogRepository);
        ArgumentNullException.ThrowIfNull(entryRepository);
        ArgumentNullException.ThrowIfNull(summaryService);
        ArgumentNullException.ThrowIfNull(translationService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateOptions(options);
        _jobs = jobs;
        _catalogRepository = catalogRepository;
        _entryRepository = entryRepository;
        _summaryService = summaryService;
        _translationService = translationService;
        _timeProvider = timeProvider;
        _options = options;
        _globalGate = new(options.MaximumConcurrency, options.MaximumConcurrency);
    }

    public async Task EnqueueAsync(
        string feedId,
        IReadOnlyList<FeedEntry> entries,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;

        FeedPolicyContext? context = await GetPolicyContextAsync(feedId, cancellationToken)
            .ConfigureAwait(false);
        if (context is null) return;

        await _jobs.EnqueueAsync(
            feedId,
            entries,
            context.Policy,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ProcessBackgroundBatchAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<FeedAiAutomationJob> claimed = await _jobs.ClaimDueAsync(
            _timeProvider.GetUtcNow(),
            _options.BatchSize,
            _options.LeaseDuration,
            cancellationToken).ConfigureAwait(false);
        if (claimed.Count == 0) return 0;

        var feedGates = new ConcurrentDictionary<string, FeedGate>(StringComparer.Ordinal);
        try
        {
            await Task.WhenAll(claimed.Select(job =>
                ProcessClaimedJobAsync(job, feedGates, cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            foreach (FeedGate gate in feedGates.Values)
            {
                gate.Semaphore.Dispose();
            }
        }

        return claimed.Count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _globalGate.Dispose();
    }

    private async Task ProcessClaimedJobAsync(
        FeedAiAutomationJob job,
        ConcurrentDictionary<string, FeedGate> feedGates,
        CancellationToken cancellationToken)
    {
        try
        {
            await ProcessClaimedJobCoreAsync(job, feedGates, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAfterCancellationAsync(job).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ProcessClaimedJobCoreAsync(
        FeedAiAutomationJob job,
        ConcurrentDictionary<string, FeedGate> feedGates,
        CancellationToken cancellationToken)
    {
        FeedPolicyContext? context;
        try
        {
            context = await GetPolicyContextAsync(job.FeedId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await ScheduleUnexpectedFailureAsync(job, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (context is null || !IsTaskEnabled(job, context.Policy))
        {
            await TryCompleteAsync(
                job,
                FeedAiAutomationJobOutcome.Skipped,
                "POLICY_DISABLED",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (job.TaskType == FeedAiAutomationTaskType.Translation
            && !string.Equals(
                job.TargetLanguage,
                context.Policy.TranslationTargetLanguage,
                StringComparison.Ordinal))
        {
            await SupersedeForPolicyChangeAsync(job, context.Policy, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        FeedGate feedGate = feedGates.GetOrAdd(
            job.FeedId,
            _ => new(context.Policy.MaxConcurrency));
        bool feedLease = false;
        bool globalLease = false;
        try
        {
            await feedGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            feedLease = true;
            await _globalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            globalLease = true;

            FeedPolicyContext? latest = await GetPolicyContextAsync(
                job.FeedId,
                cancellationToken).ConfigureAwait(false);
            if (latest is null || !IsTaskEnabled(job, latest.Policy))
            {
                await TryCompleteAsync(
                    job,
                    FeedAiAutomationJobOutcome.Skipped,
                    "POLICY_DISABLED",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            if (job.TaskType == FeedAiAutomationTaskType.Translation
                && !string.Equals(
                    job.TargetLanguage,
                    latest.Policy.TranslationTargetLanguage,
                    StringComparison.Ordinal))
            {
                await SupersedeForPolicyChangeAsync(job, latest.Policy, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await ExecuteJobAsync(job, latest.Policy, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AppException exception)
        {
            await ScheduleAppFailureAsync(job, exception.Error, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SubtitleTranslationException exception)
        {
            await ScheduleAppFailureAsync(job, exception.Error, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException)
        {
            await TryCompleteAsync(
                job,
                FeedAiAutomationJobOutcome.Skipped,
                "INVALID_CONTENT",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ScheduleUnexpectedFailureAsync(job, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (globalLease) _globalGate.Release();
            if (feedLease) feedGate.Semaphore.Release();
        }
    }

    private async Task ExecuteJobAsync(
        FeedAiAutomationJob job,
        ResolvedFeedAiPolicy policy,
        CancellationToken cancellationToken)
    {
        FeedEntry? entry = await _entryRepository.GetByIdAsync(job.EntryId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null
            || !string.Equals(entry.FeedId, job.FeedId, StringComparison.Ordinal)
            || !string.Equals(entry.ContentHash, job.ContentHash, StringComparison.Ordinal))
        {
            await TryCompleteAsync(
                job,
                FeedAiAutomationJobOutcome.Superseded,
                "ENTRY_CHANGED",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateOnly usageDate = DateOnly.FromDateTime(now.UtcDateTime);
        bool reserved = await _jobs.TryReserveDailyEntryAsync(
            usageDate,
            job.FeedId,
            job.EntryId,
            policy.DailyEntryLimit,
            now,
            cancellationToken).ConfigureAwait(false);
        if (!reserved)
        {
            DateTimeOffset nextDay = new(
                usageDate.AddDays(1).ToDateTime(new TimeOnly(0, 1)),
                TimeSpan.Zero);
            await TryScheduleRetryAsync(
                job,
                "DAILY_ENTRY_LIMIT",
                nextDay,
                now,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await FeedAiTaskExecution.ExecuteAsync(
            entry,
            job.TaskType,
            job.TargetLanguage,
            _summaryService,
            _translationService,
            cancellationToken).ConfigureAwait(false);

        await TryCompleteAsync(
            job,
            FeedAiAutomationJobOutcome.Succeeded,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<FeedPolicyContext?> GetPolicyContextAsync(
        string feedId,
        CancellationToken cancellationToken)
    {
        FeedCatalogState state = await _catalogRepository.GetStateAsync(cancellationToken)
            .ConfigureAwait(false);
        FeedCatalogSnapshot? catalog = await _catalogRepository
            .GetCatalogAsync(state.Scope, cancellationToken).ConfigureAwait(false);
        if (catalog is null) return null;

        FeedCatalogItem? feed = catalog.Feeds.FirstOrDefault(
            candidate => string.Equals(candidate.Id, feedId, StringComparison.Ordinal));
        if (feed is null || !feed.IsEnabled) return null;
        if (feed.CategoryId is not null)
        {
            FeedCategory? category = catalog.Categories.FirstOrDefault(
                candidate => string.Equals(candidate.Id, feed.CategoryId, StringComparison.Ordinal));
            if (category is null || !category.IsEnabled) return null;
        }

        return new(FeedAiPolicyResolver.Resolve(catalog, feed));
    }

    private async Task SupersedeForPolicyChangeAsync(
        FeedAiAutomationJob job,
        ResolvedFeedAiPolicy policy,
        CancellationToken cancellationToken)
    {
        FeedEntry? entry = await _entryRepository.GetByIdAsync(job.EntryId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is not null && string.Equals(entry.ContentHash, job.ContentHash, StringComparison.Ordinal))
        {
            await _jobs.EnqueueAsync(
                job.FeedId,
                [entry],
                policy,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        await TryCompleteAsync(
            job,
            FeedAiAutomationJobOutcome.Superseded,
            "TARGET_CHANGED",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ScheduleAppFailureAsync(
        FeedAiAutomationJob job,
        AppError error,
        CancellationToken cancellationToken)
    {
        DateTimeOffset failedAt = _timeProvider.GetUtcNow();
        TimeSpan delay = error.RetryAfter is { } retryAfter
            ? ClampRetryDelay(retryAfter)
            : GetRetryDelay(job.AttemptCount);
        await TryScheduleRetryAsync(
            job,
            error.Code.ToString().ToUpperInvariant(),
            failedAt.Add(delay),
            failedAt,
            cancellationToken).ConfigureAwait(false);
    }

    private Task ScheduleUnexpectedFailureAsync(
        FeedAiAutomationJob job,
        CancellationToken cancellationToken)
    {
        DateTimeOffset failedAt = _timeProvider.GetUtcNow();
        return TryScheduleRetryAsync(
            job,
            "UNEXPECTED_ERROR",
            failedAt.Add(GetRetryDelay(job.AttemptCount)),
            failedAt,
            cancellationToken);
    }

    private async Task TryCompleteAsync(
        FeedAiAutomationJob job,
        FeedAiAutomationJobOutcome outcome,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await _jobs.CompleteAsync(
                job,
                outcome,
                errorCode,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // An expired lease can be reclaimed while an older worker is finishing.
        }
    }

    private async Task TryScheduleRetryAsync(
        FeedAiAutomationJob job,
        string errorCode,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await _jobs.ScheduleRetryAsync(
                job,
                errorCode,
                nextAttemptAt,
                failedAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // An expired lease can be reclaimed while an older worker is finishing.
        }
    }

    private async Task ReleaseAfterCancellationAsync(FeedAiAutomationJob job)
    {
        try
        {
            await _jobs.ReleaseAsync(
                job,
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The durable lease eventually expires if shutdown interrupts this best-effort release.
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
        if (value < _options.BaseRetryDelay) return _options.BaseRetryDelay;
        if (value > _options.MaximumRetryDelay) return _options.MaximumRetryDelay;
        return value;
    }

    private static bool IsTaskEnabled(
        FeedAiAutomationJob job,
        ResolvedFeedAiPolicy policy) =>
        job.TaskType switch
        {
            FeedAiAutomationTaskType.Summary => policy.AutoSummaryEnabled,
            FeedAiAutomationTaskType.Translation => policy.AutoTranslationEnabled,
            _ => false
        };

    private static void ValidateOptions(FeedAiAutomationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BatchSize is < 1 or > 200
            || options.MaximumConcurrency is < 1 or > 8
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

    private sealed record FeedPolicyContext(ResolvedFeedAiPolicy Policy);

    private sealed class FeedGate(int capacity)
    {
        public SemaphoreSlim Semaphore { get; } = new(capacity, capacity);
    }
}
