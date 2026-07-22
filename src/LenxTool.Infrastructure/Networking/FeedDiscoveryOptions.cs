using System.Collections.Frozen;

namespace LenxTool.Infrastructure.Networking;

public sealed record FeedDiscoveryOptions(
    TimeSpan TotalTimeout,
    TimeSpan ConnectTimeout,
    int MaximumRedirects,
    int MaximumCandidates,
    int MaximumCompressedBytes,
    int MaximumDecompressedBytes,
    IReadOnlySet<string> AllowedHttpHosts,
    IReadOnlySet<string> TrustedPrivateHosts)
{
    public static FeedDiscoveryOptions Default { get; } = new(
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(5),
        5,
        20,
        2 * 1024 * 1024,
        4 * 1024 * 1024,
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase));
}
