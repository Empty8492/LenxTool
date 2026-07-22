namespace LenxTool.Infrastructure.Networking;

public sealed record FeedCatalogSyncOptions(
    TimeSpan SynchronizationInterval,
    TimeSpan InitialRetryDelay,
    TimeSpan MaximumRetryDelay,
    TimeSpan StaleAfter)
{
    public static FeedCatalogSyncOptions Default { get; } = new(
        TimeSpan.FromMinutes(15),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(24));
}
