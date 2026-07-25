namespace LenxTool.Infrastructure.Networking;

public sealed record FeedAutomationRuleSyncOptions(
    TimeSpan SynchronizationInterval,
    TimeSpan RetryInterval)
{
    public static FeedAutomationRuleSyncOptions Default { get; } = new(
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(1));
}
