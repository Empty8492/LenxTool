namespace LenxTool.Infrastructure.Networking;

public sealed record FeedFullTextQueueOptions(
    int BatchSize,
    int MaximumConcurrency,
    int MaximumConcurrencyPerHost,
    TimeSpan LeaseDuration,
    TimeSpan BaseRetryDelay,
    TimeSpan MaximumRetryDelay,
    TimeSpan InitialDelay,
    TimeSpan PollInterval)
{
    public static FeedFullTextQueueOptions Default { get; } = new(
        BatchSize: 8,
        MaximumConcurrency: 2,
        MaximumConcurrencyPerHost: 1,
        LeaseDuration: TimeSpan.FromMinutes(5),
        BaseRetryDelay: TimeSpan.FromMinutes(2),
        MaximumRetryDelay: TimeSpan.FromHours(6),
        InitialDelay: TimeSpan.FromSeconds(20),
        PollInterval: TimeSpan.FromMinutes(2));
}
