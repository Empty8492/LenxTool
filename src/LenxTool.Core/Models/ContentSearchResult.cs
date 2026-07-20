namespace LenxTool.Core.Models;

public enum ContentSearchResultType
{
    News,
    Trend,
    AiReport
}

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
        _ => "内容"
    };
}
