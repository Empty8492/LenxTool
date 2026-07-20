using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface INewsRepository
{
    Task UpsertAsync(
        IReadOnlyCollection<NewsArticle> articles,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NewsArticle>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ContentSearchResult>> SearchContentAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);

    Task UpsertReportAsync(
        AiReport report,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AiReport>> GetLatestReportsAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NewsArticle>> GetLatestAsync(
        int limit,
        CancellationToken cancellationToken);

    Task UpsertTrendsAsync(
        IReadOnlyCollection<TrendItem> trends,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TrendItem>> GetLatestTrendsAsync(
        int limit,
        string? platform,
        CancellationToken cancellationToken);
}
