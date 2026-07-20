namespace LenxTool.Core.Models;

public sealed record NewsArticle(
    string Id,
    DateOnly PublishedDate,
    string Source,
    string Title,
    string Summary,
    string Content,
    string Url,
    string ContentHash,
    DateTimeOffset FetchedAt)
{
    public string RichContent { get; init; } = string.Empty;
}
