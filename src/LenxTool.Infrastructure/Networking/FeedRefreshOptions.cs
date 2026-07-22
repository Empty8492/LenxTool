namespace LenxTool.Infrastructure.Networking;

public sealed record FeedRefreshOptions(
    int MaximumConcurrency,
    int MaximumFeedsPerPass,
    TimeSpan InitialFailureDelay,
    TimeSpan MaximumFailureDelay,
    TimeSpan SchedulerInterval)
{
    public static FeedRefreshOptions Default { get; } = new(
        4,
        100,
        TimeSpan.FromMinutes(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromMinutes(1));
}
