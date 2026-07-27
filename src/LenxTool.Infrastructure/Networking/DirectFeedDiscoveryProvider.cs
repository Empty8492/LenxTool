using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal sealed class DirectFeedDiscoveryProvider(
    IFeedDiscoveryService discoveryService,
    FeedDiscoveryProviderPolicy policy) : IFeedDiscoveryProvider
{
    public const string ProviderSourceId = "direct-probe";

    private readonly IFeedDiscoveryService _discoveryService =
        discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));

    public string SourceId => ProviderSourceId;

    public FeedDiscoverySourceKind SourceKind =>
        FeedDiscoverySourceKind.DirectProbe;

    public FeedDiscoveryProviderPolicy Policy { get; } =
        policy ?? throw new ArgumentNullException(nameof(policy));

    public bool Supports(FeedDiscoveryQueryKind queryKind) =>
        queryKind == FeedDiscoveryQueryKind.Url;

    public async Task<FeedDiscoveryProviderResult> DiscoverAsync(
        FeedDiscoveryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsValid
            || query.Kind != FeedDiscoveryQueryKind.Url
            || query.NormalizedValue is null)
        {
            throw new ArgumentException(
                "Direct discovery requires a valid URL query.",
                nameof(query));
        }

        FeedDiscoveryResult result = await _discoveryService
            .DiscoverAsync(query.NormalizedValue, cancellationToken)
            .ConfigureAwait(false);
        FeedDiscoveryCandidate[] candidates = result.Feeds
            .Select(feed => MapCandidate(query.NormalizedValue, feed))
            .ToArray();
        return new(candidates);
    }

    private static FeedDiscoveryCandidate MapCandidate(
        string requestedUrl,
        DiscoveredFeed feed)
    {
        FeedDiscoveryMatchKind matchKind = string.Equals(
            requestedUrl,
            feed.FeedUrl,
            StringComparison.Ordinal)
            ? FeedDiscoveryMatchKind.ExactFeedUrl
            : FeedDiscoveryMatchKind.Redirect;
        bool usesHttp = IsHttp(requestedUrl) || IsHttp(feed.FeedUrl);
        return new(
            feed.FeedUrl,
            feed.Title,
            null,
            feed.Kind,
            null,
            FeedDiscoveryHealth.Healthy,
            [new(
                ProviderSourceId,
                FeedDiscoverySourceKind.DirectProbe,
                matchKind,
                FeedDiscoveryConfidence.Exact)],
            usesHttp
                ? [new(
                    FeedDiscoveryWarningCode.InsecureTransport,
                    ProviderSourceId)]
                : []);
    }

    private static bool IsHttp(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttp;
}
