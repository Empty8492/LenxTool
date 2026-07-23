using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record FeedTimelineFilterOption(
    string? Id,
    string Label,
    string? CategoryId = null);

public sealed record FeedTimelineItem(
    FeedEntry Entry,
    string FeedName,
    string CategoryName)
{
    public DateTimeOffset DisplayTime =>
        Entry.PublishedAt ?? Entry.UpdatedAt ?? Entry.FetchedAt;

    public string Summary => string.IsNullOrWhiteSpace(Entry.Summary)
        ? Entry.SanitizedContent
        : Entry.Summary;
}
