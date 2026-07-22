namespace LenxTool.Infrastructure.Networking;

internal sealed record FeedParserOptions(int MaximumDocumentBytes, int MaximumEntries)
{
    public static FeedParserOptions Default { get; } = new(4 * 1024 * 1024, 2000);
}
