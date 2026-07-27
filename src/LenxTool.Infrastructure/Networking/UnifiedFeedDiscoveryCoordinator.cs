using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal sealed class UnifiedFeedDiscoveryCoordinator : IUnifiedFeedDiscoveryService
{
    private readonly ProviderRuntime[] _providers;

    public UnifiedFeedDiscoveryCoordinator(
        IEnumerable<IFeedDiscoveryProvider> providers,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        _providers = providers.Select(provider =>
        {
            ValidateProvider(provider, sourceIds);
            return new ProviderRuntime(provider, timeProvider);
        }).ToArray();
    }

    public async Task<UnifiedFeedDiscoveryResult> DiscoverAsync(
        string input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FeedDiscoveryQuery query = FeedDiscoveryQueryClassifier.Classify(input);
        if (!query.IsValid)
        {
            throw new AppException(new(
                AppErrorCode.InvalidRequest,
                "发现输入无效",
                "发现内容为空、过长或使用了不受支持的地址格式。",
                "请输入关键词、HTTP/HTTPS 地址或受支持的 RSSHub 路由。"));
        }

        var tasks = new List<Task<ProviderOutcome>>(_providers.Length);
        foreach (ProviderRuntime provider in _providers)
        {
            try
            {
                if (!provider.Provider.Supports(query.Kind)) continue;
                tasks.Add(ExecuteProviderAsync(provider, query, cancellationToken));
            }
            catch (Exception)
            {
                provider.RecordFailure(provider.TimeProvider.GetUtcNow());
                tasks.Add(Task.FromResult(Failed(
                    provider,
                    FeedDiscoverySourceStatus.Unavailable)));
            }
        }

        ProviderOutcome[] outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        FeedDiscoveryCandidate[] candidates = FeedDiscoveryCandidateMerger.Merge(
            outcomes.SelectMany(outcome => outcome.Candidates)).ToArray();
        FeedDiscoverySourceReport[] reports = outcomes
            .Select(outcome => outcome.Report)
            .ToArray();
        bool hasSuccessfulSource = reports.Any(report =>
            report.Status is FeedDiscoverySourceStatus.Succeeded
                or FeedDiscoverySourceStatus.NoResults);
        bool hasIncompleteSource = reports.Any(report =>
            report.Status is not (
                FeedDiscoverySourceStatus.Succeeded
                or FeedDiscoverySourceStatus.NoResults)
            || report.IsTruncated);
        FeedDiscoveryCompletionStatus status = !hasSuccessfulSource
            ? FeedDiscoveryCompletionStatus.Unavailable
            : hasIncompleteSource
                ? FeedDiscoveryCompletionStatus.Partial
                : FeedDiscoveryCompletionStatus.Complete;
        return new(query, candidates, reports, status);
    }

    private static async Task<ProviderOutcome> ExecuteProviderAsync(
        ProviderRuntime runtime,
        FeedDiscoveryQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string cacheKey = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{(int)query.Kind}:{query.NormalizedValue}");
        DateTimeOffset now = runtime.TimeProvider.GetUtcNow();
        if (runtime.TryGetCached(cacheKey, now, out CachedProviderResult cached))
        {
            FeedDiscoverySourceStatus cachedStatus = cached.Candidates.Count == 0
                ? FeedDiscoverySourceStatus.NoResults
                : FeedDiscoverySourceStatus.Succeeded;
            return new(
                cached.Candidates,
                Report(
                    runtime,
                    cachedStatus,
                    cached.Candidates.Count,
                    isFromCache: true,
                    cached.IsTruncated));
        }
        if (runtime.IsCircuitOpen(now))
        {
            return Failed(runtime, FeedDiscoverySourceStatus.CircuitOpen);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(runtime.Provider.Policy.Timeout);
        bool entered = false;
        try
        {
            await runtime.ConcurrencyGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;

            now = runtime.TimeProvider.GetUtcNow();
            if (runtime.TryGetCached(cacheKey, now, out cached))
            {
                FeedDiscoverySourceStatus cachedStatus = cached.Candidates.Count == 0
                    ? FeedDiscoverySourceStatus.NoResults
                    : FeedDiscoverySourceStatus.Succeeded;
                return new(
                    cached.Candidates,
                    Report(
                        runtime,
                        cachedStatus,
                        cached.Candidates.Count,
                        isFromCache: true,
                        cached.IsTruncated));
            }
            if (runtime.IsCircuitOpen(now))
            {
                return Failed(runtime, FeedDiscoverySourceStatus.CircuitOpen);
            }

            FeedDiscoveryProviderResult result = await runtime.Provider
                .DiscoverAsync(query, timeout.Token)
                .ConfigureAwait(false);
            IReadOnlyList<FeedDiscoveryCandidate> candidates =
                ValidateResult(runtime.Provider, result);
            runtime.RecordSuccess();
            runtime.StoreCached(
                cacheKey,
                new(candidates, result.IsTruncated),
                runtime.TimeProvider.GetUtcNow());
            FeedDiscoverySourceStatus status = candidates.Count == 0
                ? FeedDiscoverySourceStatus.NoResults
                : FeedDiscoverySourceStatus.Succeeded;
            return new(
                candidates,
                Report(
                    runtime,
                    status,
                    candidates.Count,
                    isFromCache: false,
                    result.IsTruncated));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            runtime.RecordFailure(runtime.TimeProvider.GetUtcNow());
            return Failed(runtime, FeedDiscoverySourceStatus.TimedOut);
        }
        catch (AppException exception)
        {
            runtime.RecordFailure(runtime.TimeProvider.GetUtcNow());
            FeedDiscoverySourceStatus status = exception.Error.Code switch
            {
                AppErrorCode.ProviderRateLimited =>
                    FeedDiscoverySourceStatus.RateLimited,
                AppErrorCode.Timeout =>
                    FeedDiscoverySourceStatus.TimedOut,
                _ => FeedDiscoverySourceStatus.Unavailable
            };
            return Failed(runtime, status);
        }
        catch (Exception)
        {
            runtime.RecordFailure(runtime.TimeProvider.GetUtcNow());
            return Failed(runtime, FeedDiscoverySourceStatus.Unavailable);
        }
        finally
        {
            if (entered) runtime.ConcurrencyGate.Release();
        }
    }

    private static IReadOnlyList<FeedDiscoveryCandidate> ValidateResult(
        IFeedDiscoveryProvider provider,
        FeedDiscoveryProviderResult? result)
    {
        if (result?.Candidates is null
            || result.Candidates.Count > provider.Policy.MaximumCandidates
            || result.Candidates.Any(candidate =>
                candidate?.Evidence is null
                || candidate.Evidence.Count == 0
                || candidate.Warnings is null
                || !IsCandidateMetadataValid(candidate, provider)
                || candidate.Evidence.Any(evidence =>
                    !string.Equals(
                        evidence.SourceId,
                        provider.SourceId,
                        StringComparison.Ordinal)
                    || evidence.SourceKind != provider.SourceKind)
                || candidate.Warnings.Any(warning =>
                    warning.SourceId is not null
                    && !string.Equals(
                        warning.SourceId,
                        provider.SourceId,
                        StringComparison.Ordinal))))
        {
            throw new InvalidDataException("Discovery provider result is invalid.");
        }
        FeedDiscoveryCandidate[] snapshot = result.Candidates
            .Select(candidate => candidate with
            {
                Evidence = candidate.Evidence.ToArray(),
                Warnings = candidate.Warnings.ToArray()
            })
            .ToArray();
        return FeedDiscoveryCandidateMerger.Merge(snapshot);
    }

    private static bool IsCandidateMetadataValid(
        FeedDiscoveryCandidate candidate,
        IFeedDiscoveryProvider provider)
    {
        if (candidate.Title is not null
            && (string.IsNullOrWhiteSpace(candidate.Title)
                || candidate.Title != candidate.Title.Trim()
                || candidate.Title.EnumerateRunes().Count() > 200
                || candidate.Title.Any(char.IsControl)))
        {
            return false;
        }
        if (candidate.SiteUrl is not null
            && !IsSafeMetadataUrl(candidate.SiteUrl))
        {
            return false;
        }
        if (!Uri.TryCreate(
                candidate.NormalizedFeedUrl,
                UriKind.Absolute,
                out Uri? feedUri)
            || !IsSafeMetadataUrl(candidate.NormalizedFeedUrl))
        {
            return false;
        }

        bool usesHttp = feedUri.Scheme == Uri.UriSchemeHttp
            || (candidate.SiteUrl is not null
                && Uri.TryCreate(
                    candidate.SiteUrl,
                    UriKind.Absolute,
                    out Uri? siteUri)
                && siteUri.Scheme == Uri.UriSchemeHttp);
        return !usesHttp || candidate.Warnings.Any(warning =>
            warning.Code == FeedDiscoveryWarningCode.InsecureTransport
            && string.Equals(
                warning.SourceId,
                provider.SourceId,
                StringComparison.Ordinal));
    }

    private static bool IsSafeMetadataUrl(string value) =>
        value.Length <= FeedDiscoveryQueryClassifier.MaximumInputCodePoints
        && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp
            || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(uri.IdnHost)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    private static ProviderOutcome Failed(
        ProviderRuntime runtime,
        FeedDiscoverySourceStatus status) =>
        new([], Report(runtime, status, 0, isFromCache: false, isTruncated: false));

    private static FeedDiscoverySourceReport Report(
        ProviderRuntime runtime,
        FeedDiscoverySourceStatus status,
        int candidateCount,
        bool isFromCache,
        bool isTruncated) =>
        new(
            runtime.Provider.SourceId,
            runtime.Provider.SourceKind,
            status,
            candidateCount,
            isFromCache,
            isTruncated);

    private static void ValidateProvider(
        IFeedDiscoveryProvider? provider,
        HashSet<string> sourceIds)
    {
        if (provider is null
            || string.IsNullOrWhiteSpace(provider.SourceId)
            || provider.SourceId != provider.SourceId.Trim()
            || provider.SourceId.Length > 128
            || provider.SourceId.Any(char.IsControl)
            || !Enum.IsDefined(provider.SourceKind)
            || !sourceIds.Add(provider.SourceId))
        {
            throw new ArgumentException(
                "Discovery providers must have unique bounded source identities.",
                nameof(provider));
        }

        FeedDiscoveryProviderPolicy policy = provider.Policy
            ?? throw new ArgumentException(
                "Discovery provider policy is required.",
                nameof(provider));
        if (policy.Timeout <= TimeSpan.Zero
            || policy.Timeout > TimeSpan.FromMinutes(1)
            || policy.MaximumConcurrency is < 1 or > 8
            || policy.CacheDuration < TimeSpan.Zero
            || policy.CacheDuration > TimeSpan.FromHours(1)
            || policy.MaximumCacheEntries is < 1 or > 1_000
            || policy.CircuitBreakerFailureThreshold is < 1 or > 20
            || policy.CircuitBreakerOpenDuration <= TimeSpan.Zero
            || policy.CircuitBreakerOpenDuration > TimeSpan.FromHours(1)
            || policy.MaximumCandidates is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(provider),
                "Discovery provider policy exceeds safe limits.");
        }
    }

    private sealed record ProviderOutcome(
        IReadOnlyList<FeedDiscoveryCandidate> Candidates,
        FeedDiscoverySourceReport Report);

    private sealed record CachedProviderResult(
        IReadOnlyList<FeedDiscoveryCandidate> Candidates,
        bool IsTruncated);

    private sealed record CacheEntry(
        CachedProviderResult Result,
        DateTimeOffset ExpiresAt);

    private sealed class ProviderRuntime
    {
        private readonly object _stateGate = new();
        private readonly Dictionary<string, CacheEntry> _cache =
            new(StringComparer.Ordinal);
        private int _consecutiveFailures;
        private DateTimeOffset? _circuitOpenUntil;

        public ProviderRuntime(
            IFeedDiscoveryProvider provider,
            TimeProvider timeProvider)
        {
            Provider = provider;
            TimeProvider = timeProvider;
            ConcurrencyGate = new(
                provider.Policy.MaximumConcurrency,
                provider.Policy.MaximumConcurrency);
        }

        public IFeedDiscoveryProvider Provider { get; }

        public TimeProvider TimeProvider { get; }

        public SemaphoreSlim ConcurrencyGate { get; }

        public bool TryGetCached(
            string key,
            DateTimeOffset now,
            out CachedProviderResult result)
        {
            result = null!;
            if (Provider.Policy.CacheDuration == TimeSpan.Zero) return false;
            lock (_stateGate)
            {
                if (!_cache.TryGetValue(key, out CacheEntry? entry)) return false;
                if (entry.ExpiresAt <= now)
                {
                    _cache.Remove(key);
                    return false;
                }
                result = entry.Result;
                return true;
            }
        }

        public void StoreCached(
            string key,
            CachedProviderResult result,
            DateTimeOffset now)
        {
            if (Provider.Policy.CacheDuration == TimeSpan.Zero) return;
            lock (_stateGate)
            {
                if (!_cache.ContainsKey(key)
                    && _cache.Count >= Provider.Policy.MaximumCacheEntries)
                {
                    string oldestKey = _cache
                        .MinBy(pair => pair.Value.ExpiresAt)
                        .Key;
                    _cache.Remove(oldestKey);
                }
                _cache[key] = new(
                    result,
                    now.Add(Provider.Policy.CacheDuration));
            }
        }

        public bool IsCircuitOpen(DateTimeOffset now)
        {
            lock (_stateGate)
            {
                if (_circuitOpenUntil is null) return false;
                if (_circuitOpenUntil > now) return true;
                _circuitOpenUntil = null;
                _consecutiveFailures = 0;
                return false;
            }
        }

        public void RecordSuccess()
        {
            lock (_stateGate)
            {
                _consecutiveFailures = 0;
                _circuitOpenUntil = null;
            }
        }

        public void RecordFailure(DateTimeOffset now)
        {
            lock (_stateGate)
            {
                if (_circuitOpenUntil > now) return;
                _consecutiveFailures++;
                if (_consecutiveFailures
                    < Provider.Policy.CircuitBreakerFailureThreshold)
                {
                    return;
                }
                _consecutiveFailures = 0;
                _circuitOpenUntil = now.Add(
                    Provider.Policy.CircuitBreakerOpenDuration);
            }
        }
    }
}
