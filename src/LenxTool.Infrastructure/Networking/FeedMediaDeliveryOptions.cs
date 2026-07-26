namespace LenxTool.Infrastructure.Networking;

public sealed record FeedMediaDeliveryOptions(
    long MaximumBytes,
    TimeSpan TotalTimeout,
    int MaximumRedirects,
    int MaximumConcurrentDownloads)
{
    public static FeedMediaDeliveryOptions Default { get; } = new(
        MaximumBytes: 512L * 1024 * 1024,
        TotalTimeout: TimeSpan.FromMinutes(10),
        MaximumRedirects: 5,
        MaximumConcurrentDownloads: 2);
}
