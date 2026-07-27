using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedDiscoveryProvider
{
    string SourceId { get; }

    FeedDiscoverySourceKind SourceKind { get; }

    FeedDiscoveryProviderPolicy Policy { get; }

    bool Supports(FeedDiscoveryQueryKind queryKind);

    Task<FeedDiscoveryProviderResult> DiscoverAsync(
        FeedDiscoveryQuery query,
        CancellationToken cancellationToken);
}
