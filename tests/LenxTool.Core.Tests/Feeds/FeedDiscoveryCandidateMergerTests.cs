using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Feeds;

public sealed class FeedDiscoveryCandidateMergerTests
{
    [Fact]
    public void MergeUsesCanonicalFeedUrlAndPreservesSourceEvidence()
    {
        FeedDiscoveryCandidate[] candidates =
        [
            Candidate(
                "HTTPS://Example.COM:443/feed",
                "Catalog title",
                FeedDiscoveryConfidence.Medium,
                FeedDiscoverySourceKind.KnownCatalog,
                FeedDiscoveryWarningCode.Stale),
            Candidate(
                "https://example.com/feed",
                "Verified title",
                FeedDiscoveryConfidence.Exact,
                FeedDiscoverySourceKind.DirectProbe,
                FeedDiscoveryWarningCode.InsecureTransport)
        ];

        FeedDiscoveryCandidate merged =
            Assert.Single(FeedDiscoveryCandidateMerger.Merge(candidates));

        Assert.Equal("https://example.com/feed", merged.NormalizedFeedUrl);
        Assert.Equal("Verified title", merged.Title);
        Assert.Equal(FeedDiscoveryHealth.Healthy, merged.Health);
        Assert.Equal(FeedDiscoveryConfidence.Exact, merged.Confidence);
        Assert.Collection(
            merged.Evidence,
            evidence => Assert.Equal(
                FeedDiscoverySourceKind.KnownCatalog,
                evidence.SourceKind),
            evidence => Assert.Equal(
                FeedDiscoverySourceKind.DirectProbe,
                evidence.SourceKind));
        Assert.Equal(
            [FeedDiscoveryWarningCode.Stale, FeedDiscoveryWarningCode.InsecureTransport],
            merged.Warnings.Select(warning => warning.Code));
    }

    [Fact]
    public void MergeTreatsRedirectedFinalUrlAsCandidateIdentity()
    {
        FeedDiscoveryCandidate[] candidates =
        [
            Candidate(
                "https://feeds.example.com/current.xml",
                "Known result",
                FeedDiscoveryConfidence.High,
                FeedDiscoverySourceKind.KnownCatalog),
            Candidate(
                "https://feeds.example.com/current.xml",
                "Redirect result",
                FeedDiscoveryConfidence.Exact,
                FeedDiscoverySourceKind.DirectProbe,
                matchKind: FeedDiscoveryMatchKind.Redirect)
        ];

        FeedDiscoveryCandidate merged =
            Assert.Single(FeedDiscoveryCandidateMerger.Merge(candidates));

        Assert.Equal(2, merged.Evidence.Count);
        Assert.Contains(
            merged.Evidence,
            evidence => evidence.MatchKind == FeedDiscoveryMatchKind.Redirect);
    }

    [Fact]
    public void MergeKeepsDistinctFeedPathsCaseSensitive()
    {
        FeedDiscoveryCandidate[] candidates =
        [
            Candidate(
                "https://example.com/Feed",
                "Upper",
                FeedDiscoveryConfidence.High,
                FeedDiscoverySourceKind.KnownCatalog),
            Candidate(
                "https://example.com/feed",
                "Lower",
                FeedDiscoveryConfidence.High,
                FeedDiscoverySourceKind.KnownCatalog)
        ];

        IReadOnlyList<FeedDiscoveryCandidate> result =
            FeedDiscoveryCandidateMerger.Merge(candidates);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void MergeKeepsDistinctTrailingSlashPaths()
    {
        FeedDiscoveryCandidate[] candidates =
        [
            Candidate(
                "https://example.com/feed/",
                "Slash",
                FeedDiscoveryConfidence.High,
                FeedDiscoverySourceKind.KnownCatalog),
            Candidate(
                "https://example.com/feed",
                "No slash",
                FeedDiscoveryConfidence.High,
                FeedDiscoverySourceKind.KnownCatalog)
        ];

        IReadOnlyList<FeedDiscoveryCandidate> result =
            FeedDiscoveryCandidateMerger.Merge(candidates);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void MergeRejectsCandidateWithUnsafeIdentity()
    {
        FeedDiscoveryCandidate candidate = Candidate(
            "file:///c:/private.xml",
            "Unsafe",
            FeedDiscoveryConfidence.High,
            FeedDiscoverySourceKind.ExternalProvider);

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => FeedDiscoveryCandidateMerger.Merge([candidate]));

        Assert.Equal("candidates", error.ParamName);
    }

    [Fact]
    public void MergeRejectsCandidateWithoutSourceEvidence()
    {
        FeedDiscoveryCandidate candidate = Candidate(
            "https://example.com/feed.xml",
            "Missing evidence",
            FeedDiscoveryConfidence.High,
            FeedDiscoverySourceKind.ExternalProvider) with
        {
            Evidence = []
        };

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => FeedDiscoveryCandidateMerger.Merge([candidate]));

        Assert.Equal("candidates", error.ParamName);
    }

    [Fact]
    public void CandidateUsesTypedSignalsInsteadOfDisplayText()
    {
        FeedDiscoveryCandidate candidate = Candidate(
            "https://example.com/feed.xml",
            "Example",
            FeedDiscoveryConfidence.High,
            FeedDiscoverySourceKind.ExternalProvider,
            FeedDiscoveryWarningCode.ProviderPartialFailure);

        Assert.Equal(FeedDiscoveryHealth.Healthy, candidate.Health);
        Assert.Equal(
            FeedDiscoveryMatchKind.Keyword,
            Assert.Single(candidate.Evidence).MatchKind);
        Assert.Equal(
            FeedDiscoveryWarningCode.ProviderPartialFailure,
            Assert.Single(candidate.Warnings).Code);
    }

    private static FeedDiscoveryCandidate Candidate(
        string url,
        string title,
        FeedDiscoveryConfidence confidence,
        FeedDiscoverySourceKind sourceKind,
        FeedDiscoveryWarningCode? warning = null,
        FeedDiscoveryMatchKind matchKind = FeedDiscoveryMatchKind.Keyword) =>
        new(
            url,
            title,
            "https://example.com",
            FeedDocumentKind.Rss20,
            new DateTimeOffset(2026, 7, 27, 8, 0, 0, TimeSpan.Zero),
            FeedDiscoveryHealth.Healthy,
            [
                new(
                    sourceKind.ToString(),
                    sourceKind,
                    matchKind,
                    confidence)
            ],
            warning is null
                ? []
                : [new(warning.Value, sourceKind.ToString())]);
}
