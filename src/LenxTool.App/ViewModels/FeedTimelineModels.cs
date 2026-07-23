using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record FeedTimelineFilterOption(
    string? Id,
    string Label,
    string? CategoryId = null);

public sealed record FeedTimelineReadFilterOption(
    FeedEntryReadFilter Value,
    string Label);

public sealed record FeedTimelineItem(
    FeedEntry Entry,
    string FeedName,
    string CategoryName,
    EntryState? State = null,
    FavoriteItem? Favorite = null)
{
    public DateTimeOffset DisplayTime =>
        Entry.PublishedAt ?? Entry.UpdatedAt ?? Entry.FetchedAt;

    public string Summary => string.IsNullOrWhiteSpace(Entry.Summary)
        ? Entry.SanitizedContent
        : Entry.Summary;

    public bool IsRead => State?.IsRead ?? false;
    public bool IsStarred => Favorite is not null || (State?.IsStarred ?? false);
    public double Progress => State?.Progress ?? 0;
    public string Note => Favorite?.Note ?? State?.Note ?? string.Empty;
    public string ReadGlyph => IsRead ? "●" : "○";
    public string StarGlyph => IsStarred ? "★" : "☆";
    public string ReadActionLabel => IsRead ? "标为未读" : "标为已读";
    public string StarActionLabel => IsStarred ? "取消收藏" : "收藏";
}
