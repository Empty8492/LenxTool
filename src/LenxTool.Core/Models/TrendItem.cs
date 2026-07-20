namespace LenxTool.Core.Models;

public sealed record TrendItem(
    string Id,
    string Platform,
    int Rank,
    string Title,
    string Heat,
    string Url,
    string ContentHash,
    DateTimeOffset CapturedAt);

public sealed record NewsCenterSnapshot(
    IReadOnlyList<NewsArticle> Articles,
    IReadOnlyList<TrendItem> Trends,
    bool IsFromCache,
    DateTimeOffset? CacheTime,
    string? Warning);
