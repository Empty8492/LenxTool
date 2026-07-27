using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IUnifiedFeedDiscoveryService
{
    Task<UnifiedFeedDiscoveryResult> DiscoverAsync(
        string input,
        CancellationToken cancellationToken);
}
