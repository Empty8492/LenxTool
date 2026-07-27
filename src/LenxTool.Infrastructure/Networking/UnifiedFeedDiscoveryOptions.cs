using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed record UnifiedFeedDiscoveryOptions(
    FeedDiscoveryProviderPolicy DirectProbe,
    FeedDiscoveryProviderPolicy KnownCatalog)
{
    public static UnifiedFeedDiscoveryOptions Default { get; } = new(
        new(
            TimeSpan.FromSeconds(20),
            2,
            TimeSpan.FromMinutes(5),
            100,
            3,
            TimeSpan.FromMinutes(2),
            20),
        new(
            TimeSpan.FromSeconds(8),
            2,
            TimeSpan.FromMinutes(1),
            100,
            3,
            TimeSpan.FromMinutes(2),
            50));
}
