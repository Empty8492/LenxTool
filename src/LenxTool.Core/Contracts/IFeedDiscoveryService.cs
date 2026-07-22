using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedDiscoveryService
{
    Task<FeedDiscoveryResult> DiscoverAsync(
        string url,
        CancellationToken cancellationToken);
}
