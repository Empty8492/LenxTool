namespace LenxTool.Infrastructure.Networking;

public sealed record ArticleContentExtractionOptions(
    TimeSpan TotalTimeout,
    int MaximumRedirects,
    int MaximumDownloadBytes,
    int MaximumDecodedBytes,
    int MaximumConcurrentRequestsPerHost,
    int MaximumNestingDepth,
    int MaximumDocumentNodes,
    int MaximumBlocks,
    int MaximumTotalTextCharacters)
{
    public static ArticleContentExtractionOptions Default { get; } = new(
        TotalTimeout: TimeSpan.FromSeconds(20),
        MaximumRedirects: 5,
        MaximumDownloadBytes: 2 * 1024 * 1024,
        MaximumDecodedBytes: 5 * 1024 * 1024,
        MaximumConcurrentRequestsPerHost: 2,
        MaximumNestingDepth: 256,
        MaximumDocumentNodes: 50_000,
        MaximumBlocks: 512,
        MaximumTotalTextCharacters: 500_000);
}
