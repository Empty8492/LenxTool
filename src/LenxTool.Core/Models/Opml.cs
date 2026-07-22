namespace LenxTool.Core.Models;

public sealed record OpmlFeed(
    string Title,
    string XmlUrl,
    string? HtmlUrl,
    IReadOnlyList<string> GroupPath);

public sealed record OpmlDocument(
    string Title,
    IReadOnlyList<OpmlFeed> Feeds);
