namespace LenxTool.Core.Models;

public enum FeedDocumentKind
{
    Rss20,
    Atom
}

public sealed record DiscoveredFeed(
    string FeedUrl,
    string? Title,
    FeedDocumentKind Kind);

public sealed record FeedDiscoveryResult(
    string RequestedUrl,
    IReadOnlyList<DiscoveredFeed> Feeds);
