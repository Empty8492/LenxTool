namespace LenxTool.Core.Models;

public enum ContentSearchResultType
{
    News,
    Trend,
    AiReport,
    FeedEntry,
    Subtitle,
    Tag,
    Favorite
}

public sealed record ContentSearchQuery(
    string Text,
    ContentSearchResultType? Type = null,
    DateTimeOffset? PublishedFrom = null,
    DateTimeOffset? PublishedBefore = null,
    string? FeedId = null,
    string? CategoryId = null,
    string? TagId = null,
    bool FavoritesOnly = false,
    int Offset = 0,
    int Limit = 50);

public sealed record ContentSearchPage(
    IReadOnlyList<ContentSearchResult> Items,
    bool HasMore);

public sealed record ContentSearchResult(
    string EntityId,
    ContentSearchResultType Type,
    string Title,
    string Summary,
    string Source,
    string? Url,
    DateTimeOffset Timestamp)
{
    public string TypeLabel => Type switch
    {
        ContentSearchResultType.News => "早报",
        ContentSearchResultType.Trend => "热点",
        ContentSearchResultType.AiReport => "AI 报告",
        ContentSearchResultType.FeedEntry => "订阅条目",
        ContentSearchResultType.Subtitle => "字幕",
        ContentSearchResultType.Tag => "标签",
        ContentSearchResultType.Favorite => "收藏",
        _ => "内容"
    };
}
