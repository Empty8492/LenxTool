using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class UnifiedFeedDiscoveryCoordinatorTests
{
    [Fact]
    public void DefaultPoliciesFreezeKeywordAndDirectProbeBudgets()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(8),
            UnifiedFeedDiscoveryOptions.Default.KnownCatalog.Timeout);
        Assert.Equal(
            TimeSpan.FromSeconds(20),
            UnifiedFeedDiscoveryOptions.Default.DirectProbe.Timeout);
        Assert.Equal(
            TimeSpan.FromSeconds(20),
            FeedDiscoveryOptions.Default.TotalTimeout);
        Assert.Equal(
            FeedDiscoveryOptions.Default.TotalTimeout,
            UnifiedFeedDiscoveryOptions.Default.DirectProbe.Timeout);
    }

    [Fact]
    public async Task DuplicateCandidatesMergeWithoutLosingSourceEvidence()
    {
        var catalog = Provider(
            "worker:known-catalog",
            FeedDiscoverySourceKind.KnownCatalog,
            Candidate(
                "https://example.com/feed.xml",
                "Catalog title",
                "worker:known-catalog",
                FeedDiscoverySourceKind.KnownCatalog,
                FeedDiscoveryConfidence.High));
        var direct = Provider(
            "direct-probe",
            FeedDiscoverySourceKind.DirectProbe,
            Candidate(
                "HTTPS://Example.COM:443/feed.xml",
                "Verified title",
                "direct-probe",
                FeedDiscoverySourceKind.DirectProbe,
                FeedDiscoveryConfidence.Exact));
        var coordinator = new UnifiedFeedDiscoveryCoordinator(
            [catalog, direct],
            TimeProvider.System);

        UnifiedFeedDiscoveryResult result = await coordinator.DiscoverAsync(
            "https://example.com/feed.xml",
            CancellationToken.None);

        Assert.Equal(FeedDiscoveryCompletionStatus.Complete, result.Status);
        FeedDiscoveryCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal("Verified title", candidate.Title);
        Assert.Equal(2, candidate.Evidence.Count);
        Assert.Equal(
            [FeedDiscoverySourceStatus.Succeeded, FeedDiscoverySourceStatus.Succeeded],
            result.Sources.Select(source => source.Status));
    }

    [Fact]
    public async Task TimedOutProviderDoesNotHideHealthyProvider()
    {
        var hanging = new StubProvider(
            "slow-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            Policy(timeout: TimeSpan.FromMilliseconds(25)),
            static async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new([]);
            });
        var healthy = Provider(
            "healthy-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            Candidate(
                "https://example.com/healthy.xml",
                "Healthy",
                "healthy-provider",
                FeedDiscoverySourceKind.ExternalProvider,
                FeedDiscoveryConfidence.Medium));
        var coordinator = new UnifiedFeedDiscoveryCoordinator(
            [hanging, healthy],
            TimeProvider.System);

        UnifiedFeedDiscoveryResult result = await coordinator.DiscoverAsync(
            "example",
            CancellationToken.None);

        Assert.Equal(FeedDiscoveryCompletionStatus.Partial, result.Status);
        Assert.Single(result.Candidates);
        Assert.Contains(
            result.Sources,
            source => source.SourceId == "slow-provider"
                && source.Status == FeedDiscoverySourceStatus.TimedOut);
        Assert.Contains(
            result.Sources,
            source => source.SourceId == "healthy-provider"
                && source.Status == FeedDiscoverySourceStatus.Succeeded);
    }

    [Fact]
    public async Task AllFailuresReturnOnlyTypedSanitizedSourceStates()
    {
        var limited = new StubProvider(
            "limited-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            Policy(),
            static (_, _) => throw new AppException(new(
                AppErrorCode.ProviderRateLimited,
                "upstream title",
                "secret upstream response",
                "internal suggestion")));
        var malformed = new StubProvider(
            "malformed-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            Policy(),
            static (_, _) => throw new InvalidDataException("private response body"));
        var coordinator = new UnifiedFeedDiscoveryCoordinator(
            [limited, malformed],
            TimeProvider.System);

        UnifiedFeedDiscoveryResult result = await coordinator.DiscoverAsync(
            "example",
            CancellationToken.None);

        Assert.Equal(FeedDiscoveryCompletionStatus.Unavailable, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Equal(
            [FeedDiscoverySourceStatus.RateLimited, FeedDiscoverySourceStatus.Unavailable],
            result.Sources.Select(source => source.Status));
        Assert.DoesNotContain(
            "secret",
            System.Text.Json.JsonSerializer.Serialize(result),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "private response",
            System.Text.Json.JsonSerializer.Serialize(result),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulResultIsCachedPerProviderAndQuery()
    {
        int calls = 0;
        var provider = new StubProvider(
            "cached-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            Policy(),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<FeedDiscoveryProviderResult>(new([
                    Candidate(
                        "https://example.com/cached.xml",
                        "Cached",
                        "cached-provider",
                        FeedDiscoverySourceKind.ExternalProvider,
                        FeedDiscoveryConfidence.Medium)
                ]));
            });
        var coordinator = new UnifiedFeedDiscoveryCoordinator(
            [provider],
            TimeProvider.System);

        await coordinator.DiscoverAsync("cached", CancellationToken.None);
        UnifiedFeedDiscoveryResult repeated = await coordinator.DiscoverAsync(
            "cached",
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.True(Assert.Single(repeated.Sources).IsFromCache);
    }

    [Fact]
    public async Task ProviderConcurrencyGateIsIndependentAndBounded()
    {
        int calls = 0;
        int active = 0;
        int maximumActive = 0;
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubProvider(
            "bounded-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            Policy(
                maximumConcurrency: 1,
                cacheDuration: TimeSpan.Zero),
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                int current = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximumActive, current);
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return new([]);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });
        var coordinator = new UnifiedFeedDiscoveryCoordinator(
            [provider],
            TimeProvider.System);

        Task first = coordinator.DiscoverAsync("first", CancellationToken.None);
        Task second = coordinator.DiscoverAsync("second", CancellationToken.None);

        Assert.Equal(1, calls);
        release.SetResult(true);
        await Task.WhenAll(first, second);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task CircuitOpensForOnlyTheFailingProviderAndRecoversAfterWindow()
    {
        int calls = 0;
        bool shouldFail = true;
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero));
        var provider = new StubProvider(
            "breaker-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            Policy(
                cacheDuration: TimeSpan.Zero,
                circuitBreakerFailureThreshold: 2,
                circuitBreakerOpenDuration: TimeSpan.FromMinutes(1)),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return shouldFail
                    ? throw new InvalidDataException("unsafe upstream detail")
                    : Task.FromResult<FeedDiscoveryProviderResult>(new([]));
            });
        var coordinator = new UnifiedFeedDiscoveryCoordinator([provider], time);

        await coordinator.DiscoverAsync("one", CancellationToken.None);
        await coordinator.DiscoverAsync("two", CancellationToken.None);
        UnifiedFeedDiscoveryResult open = await coordinator.DiscoverAsync(
            "three",
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(
            FeedDiscoverySourceStatus.CircuitOpen,
            Assert.Single(open.Sources).Status);

        shouldFail = false;
        time.Advance(TimeSpan.FromMinutes(1));
        UnifiedFeedDiscoveryResult recovered = await coordinator.DiscoverAsync(
            "four",
            CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.Equal(
            FeedDiscoverySourceStatus.NoResults,
            Assert.Single(recovered.Sources).Status);
    }

    [Fact]
    public async Task CallerCancellationIsPropagatedWithoutBecomingProviderTimeout()
    {
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubProvider(
            "cancel-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            Policy(),
            async (_, cancellationToken) =>
            {
                started.SetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new([]);
            });
        var coordinator = new UnifiedFeedDiscoveryCoordinator(
            [provider],
            TimeProvider.System);
        using var cancellation = new CancellationTokenSource();

        Task operation = coordinator.DiscoverAsync("cancel", cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await operation);
    }

    [Fact]
    public async Task UnsafeProviderMetadataIsIsolatedAsUnavailable()
    {
        FeedDiscoveryCandidate unsafeCandidate = Candidate(
            "https://example.com/feed.xml",
            "Unsafe site metadata",
            "unsafe-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            FeedDiscoveryConfidence.Medium) with
        {
            SiteUrl = "file:///c:/private.txt"
        };
        var provider = Provider(
            "unsafe-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            unsafeCandidate);
        var coordinator = new UnifiedFeedDiscoveryCoordinator(
            [provider],
            TimeProvider.System);

        UnifiedFeedDiscoveryResult result = await coordinator.DiscoverAsync(
            "unsafe",
            CancellationToken.None);

        Assert.Equal(FeedDiscoveryCompletionStatus.Unavailable, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Equal(
            FeedDiscoverySourceStatus.Unavailable,
            Assert.Single(result.Sources).Status);
    }

    [Fact]
    public async Task ProviderCannotAttributeEvidenceOrWarningsToAnotherSource()
    {
        FeedDiscoveryCandidate spoofedCandidate = Candidate(
            "https://example.com/feed.xml",
            "Spoofed",
            "actual-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            FeedDiscoveryConfidence.Medium) with
        {
            Evidence =
            [
                new(
                    "actual-provider",
                    FeedDiscoverySourceKind.ExternalProvider,
                    FeedDiscoveryMatchKind.Keyword,
                    FeedDiscoveryConfidence.Medium),
                new(
                    "trusted-provider",
                    FeedDiscoverySourceKind.KnownCatalog,
                    FeedDiscoveryMatchKind.ExactFeedUrl,
                    FeedDiscoveryConfidence.Exact)
            ],
            Warnings =
            [
                new(
                    FeedDiscoveryWarningCode.Unverified,
                    "trusted-provider")
            ]
        };
        var provider = Provider(
            "actual-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            spoofedCandidate);
        var coordinator = new UnifiedFeedDiscoveryCoordinator(
            [provider],
            TimeProvider.System);

        UnifiedFeedDiscoveryResult result = await coordinator.DiscoverAsync(
            "spoofed",
            CancellationToken.None);

        Assert.Equal(FeedDiscoveryCompletionStatus.Unavailable, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Equal(
            FeedDiscoverySourceStatus.Unavailable,
            Assert.Single(result.Sources).Status);
    }

    [Fact]
    public async Task PreCanceledRequestPropagatesWhenNoProviderSupportsQuery()
    {
        var provider = Provider(
            "keyword-only-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            Candidate(
                "https://example.com/feed.xml",
                "Keyword",
                "keyword-only-provider",
                FeedDiscoverySourceKind.ExternalProvider,
                FeedDiscoveryConfidence.Medium));
        var coordinator = new UnifiedFeedDiscoveryCoordinator(
            [provider],
            TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.DiscoverAsync(
                "rsshub://example/route",
                cancellation.Token));
    }

    [Fact]
    public async Task CachedCandidateSnapshotsMutableProviderCollections()
    {
        var evidence = new List<FeedDiscoveryEvidence>
        {
            new(
                "mutable-provider",
                FeedDiscoverySourceKind.ExternalProvider,
                FeedDiscoveryMatchKind.Keyword,
                FeedDiscoveryConfidence.Medium)
        };
        var warnings = new List<FeedDiscoveryWarning>();
        FeedDiscoveryCandidate candidate = Candidate(
            "https://example.com/feed.xml",
            "Stable",
            "mutable-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            FeedDiscoveryConfidence.Medium) with
        {
            Evidence = evidence,
            Warnings = warnings
        };
        var provider = Provider(
            "mutable-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            candidate);
        var coordinator = new UnifiedFeedDiscoveryCoordinator(
            [provider],
            TimeProvider.System);

        UnifiedFeedDiscoveryResult first = await coordinator.DiscoverAsync(
            "mutable",
            CancellationToken.None);
        evidence.Add(new(
            "mutable-provider",
            FeedDiscoverySourceKind.ExternalProvider,
            FeedDiscoveryMatchKind.ExactFeedUrl,
            FeedDiscoveryConfidence.Exact));
        warnings.Add(new(
            FeedDiscoveryWarningCode.Unverified,
            "mutable-provider"));
        UnifiedFeedDiscoveryResult cached = await coordinator.DiscoverAsync(
            "mutable",
            CancellationToken.None);

        Assert.Single(Assert.Single(first.Candidates).Evidence);
        Assert.Empty(Assert.Single(first.Candidates).Warnings);
        Assert.Single(Assert.Single(cached.Candidates).Evidence);
        Assert.Empty(Assert.Single(cached.Candidates).Warnings);
        Assert.True(Assert.Single(cached.Sources).IsFromCache);
    }

    private static StubProvider Provider(
        string sourceId,
        FeedDiscoverySourceKind sourceKind,
        FeedDiscoveryCandidate candidate) =>
        new(
            sourceId,
            sourceKind,
            Policy(),
            (_, _) => Task.FromResult<FeedDiscoveryProviderResult>(new([candidate])));

    private static FeedDiscoveryCandidate Candidate(
        string url,
        string title,
        string sourceId,
        FeedDiscoverySourceKind sourceKind,
        FeedDiscoveryConfidence confidence) =>
        new(
            url,
            title,
            null,
            FeedDocumentKind.Rss20,
            null,
            FeedDiscoveryHealth.Healthy,
            [new(sourceId, sourceKind, FeedDiscoveryMatchKind.Keyword, confidence)],
            []);

    private static FeedDiscoveryProviderPolicy Policy(
        TimeSpan? timeout = null,
        int maximumConcurrency = 2,
        TimeSpan? cacheDuration = null,
        int circuitBreakerFailureThreshold = 3,
        TimeSpan? circuitBreakerOpenDuration = null) =>
        new(
            timeout ?? TimeSpan.FromSeconds(1),
            maximumConcurrency,
            cacheDuration ?? TimeSpan.FromMinutes(1),
            10,
            circuitBreakerFailureThreshold,
            circuitBreakerOpenDuration ?? TimeSpan.FromMinutes(1),
            10);

    private sealed class StubProvider(
        string sourceId,
        FeedDiscoverySourceKind sourceKind,
        FeedDiscoveryProviderPolicy policy,
        Func<FeedDiscoveryQuery, CancellationToken, Task<FeedDiscoveryProviderResult>> discover)
        : IFeedDiscoveryProvider
    {
        public string SourceId { get; } = sourceId;

        public FeedDiscoverySourceKind SourceKind { get; } = sourceKind;

        public FeedDiscoveryProviderPolicy Policy { get; } = policy;

        public bool Supports(FeedDiscoveryQueryKind queryKind) =>
            queryKind is FeedDiscoveryQueryKind.Url or FeedDiscoveryQueryKind.Keyword;

        public Task<FeedDiscoveryProviderResult> DiscoverAsync(
            FeedDiscoveryQuery query,
            CancellationToken cancellationToken) =>
            discover(query, cancellationToken);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int candidate)
        {
            int current = Volatile.Read(ref target);
            while (candidate > current)
            {
                int observed = Interlocked.CompareExchange(
                    ref target,
                    candidate,
                    current);
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
