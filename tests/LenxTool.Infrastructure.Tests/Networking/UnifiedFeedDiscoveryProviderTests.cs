using System.Net;
using System.Net.Http.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class UnifiedFeedDiscoveryProviderTests
{
    [Fact]
    public async Task DirectProviderMapsVerifiedRedirectAndInsecureTransport()
    {
        var legacy = new StubLegacyDiscoveryService(new(
            "http://allowed.example/start",
            [new(
                "https://allowed.example/feed.xml",
                "Verified feed",
                FeedDocumentKind.Rss20)]));
        var provider = new DirectFeedDiscoveryProvider(
            legacy,
            UnifiedFeedDiscoveryOptions.Default.DirectProbe);
        FeedDiscoveryQuery query = FeedDiscoveryQueryClassifier.Classify(
            "http://allowed.example/start");

        FeedDiscoveryProviderResult result = await provider.DiscoverAsync(
            query,
            CancellationToken.None);

        FeedDiscoveryCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal(
            FeedDiscoveryMatchKind.Redirect,
            Assert.Single(candidate.Evidence).MatchKind);
        Assert.Equal(
            FeedDiscoverySourceKind.DirectProbe,
            Assert.Single(candidate.Evidence).SourceKind);
        Assert.Equal(
            FeedDiscoveryWarningCode.InsecureTransport,
            Assert.Single(candidate.Warnings).Code);
        Assert.Equal("https://allowed.example/feed.xml", candidate.NormalizedFeedUrl);
        Assert.True(provider.Supports(FeedDiscoveryQueryKind.Url));
        Assert.False(provider.Supports(FeedDiscoveryQueryKind.Keyword));
        Assert.False(provider.Supports(FeedDiscoveryQueryKind.RssHubRoute));
    }

    [Fact]
    public async Task KnownCatalogProviderMapsValidatedPageAndPreservesTruncation()
    {
        var client = new StubKnownCatalogClient(
            _ => KnownCatalogResponse(nextCursor: "abc", totalItems: 51));
        var provider = new KnownCatalogFeedDiscoveryProvider(
            client,
            UnifiedFeedDiscoveryOptions.Default.KnownCatalog);
        FeedDiscoveryQuery query = FeedDiscoveryQueryClassifier.Classify("技术");

        FeedDiscoveryProviderResult result = await provider.DiscoverAsync(
            query,
            CancellationToken.None);

        FeedDiscoveryCandidate candidate = Assert.Single(result.Candidates);
        Assert.True(result.IsTruncated);
        Assert.Equal("技术日报", candidate.Title);
        Assert.Equal(
            FeedDiscoverySourceKind.KnownCatalog,
            Assert.Single(candidate.Evidence).SourceKind);
        Assert.Equal(
            FeedDiscoveryMatchKind.ExactTitle,
            Assert.Single(candidate.Evidence).MatchKind);
        Assert.Equal(
            "/v1/feeds/discoveries?query=%E6%8A%80%E6%9C%AF&pageSize=50&scope=ACTIVE",
            client.LastPath);
    }

    [Fact]
    public async Task KnownCatalogProviderRejectsSpoofedEvidenceWithoutLeakingPayload()
    {
        var client = new StubKnownCatalogClient(
            _ => KnownCatalogResponse(evidenceSourceId: "spoofed-source"));
        var provider = new KnownCatalogFeedDiscoveryProvider(
            client,
            UnifiedFeedDiscoveryOptions.Default.KnownCatalog);
        FeedDiscoveryQuery query = FeedDiscoveryQueryClassifier.Classify("技术");

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => provider.DiscoverAsync(query, CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
        Assert.Equal("LenxTool Worker", error.Error.Provider);
        Assert.Null(error.Error.TechnicalDetails);
        Assert.DoesNotContain(
            "spoofed-source",
            error.Error.UserMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownCatalogProviderPreservesRateLimitClassification()
    {
        var client = new StubKnownCatalogClient(
            _ => new(HttpStatusCode.TooManyRequests)
            {
                Content = JsonContent.Create(new { error = "rate_limited" })
            });
        var provider = new KnownCatalogFeedDiscoveryProvider(
            client,
            UnifiedFeedDiscoveryOptions.Default.KnownCatalog);
        FeedDiscoveryQuery query = FeedDiscoveryQueryClassifier.Classify("技术");

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => provider.DiscoverAsync(query, CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderRateLimited, error.Error.Code);
    }

    [Fact]
    public async Task KnownCatalogProviderRejectsNullCatalogItemAsSanitizedFailure()
    {
        const string payload =
            """
            {
              "catalogVersion": 12,
              "query": "技术",
              "scope": "ACTIVE",
              "items": [null],
              "pagination": {
                "pageSize": 50,
                "totalItems": 1,
                "nextCursor": null
              }
            }
            """;
        var client = new StubKnownCatalogClient(
            _ => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    payload,
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
        var provider = new KnownCatalogFeedDiscoveryProvider(
            client,
            UnifiedFeedDiscoveryOptions.Default.KnownCatalog);
        FeedDiscoveryQuery query = FeedDiscoveryQueryClassifier.Classify("技术");

        AppException error = await Assert.ThrowsAsync<AppException>(
            () => provider.DiscoverAsync(query, CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, error.Error.Code);
        Assert.Null(error.Error.TechnicalDetails);
    }

    private static HttpResponseMessage KnownCatalogResponse(
        string evidenceSourceId = "worker:known-catalog",
        string? nextCursor = null,
        int totalItems = 1)
    {
        var payload = new
        {
            catalogVersion = 12,
            query = "技术",
            scope = "ACTIVE",
            items = new[]
            {
                new
                {
                    normalizedFeedUrl = "https://example.com/feed.xml",
                    title = "技术日报",
                    siteUrl = "https://example.com/",
                    documentKind = (string?)null,
                    lastUpdatedAt = "2026-07-28T00:00:00Z",
                    health = "UNKNOWN",
                    evidence = new[]
                    {
                        new
                        {
                            sourceId = evidenceSourceId,
                            sourceKind = "KNOWN_CATALOG",
                            matchKind = "EXACT_TITLE",
                            confidence = "HIGH"
                        }
                    },
                    warnings = Array.Empty<object>(),
                    catalog = new
                    {
                        feedId = "72000000-0000-4000-8000-000000000001",
                        categoryId = (string?)null,
                        categoryName = (string?)null,
                        viewKind = "ARTICLE",
                        isEnabled = true
                    }
                }
            },
            pagination = new
            {
                pageSize = 50,
                totalItems,
                nextCursor
            }
        };
        return new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload)
        };
    }

    private sealed class StubLegacyDiscoveryService(FeedDiscoveryResult result)
        : IFeedDiscoveryService
    {
        public Task<FeedDiscoveryResult> DiscoverAsync(
            string url,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class StubKnownCatalogClient(
        Func<string, HttpResponseMessage> send)
        : IKnownCatalogDiscoveryClient
    {
        public string? LastPath { get; private set; }

        public Task<HttpResponseMessage> GetAsync(
            string pathAndQuery,
            CancellationToken cancellationToken)
        {
            LastPath = pathAndQuery;
            return Task.FromResult(send(pathAndQuery));
        }
    }
}
