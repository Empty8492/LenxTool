namespace LenxTool.Infrastructure.Networking;

public sealed record ArticleImageDownloadOptions(
    TimeSpan TotalTimeout,
    int MaximumRedirects,
    int MaximumConcurrentDownloads,
    TimeSpan FailureRetryDelay)
{
    public static ArticleImageDownloadOptions Default { get; } = new(
        TotalTimeout: TimeSpan.FromSeconds(20),
        MaximumRedirects: 5,
        MaximumConcurrentDownloads: 4,
        FailureRetryDelay: TimeSpan.FromMinutes(5));
}
