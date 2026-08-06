using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IAiReportService
{
    Task<AiReport> GenerateFeedDigestAsync(
        FeedDigestPlan plan,
        CancellationToken cancellationToken);

    Task<AiReport> GenerateArticleInsightAsync(
        NewsArticle article,
        CancellationToken cancellationToken);

    Task<AiReport> GenerateDailyTrendReportAsync(
        IReadOnlyList<TrendItem> trends,
        CancellationToken cancellationToken);
}
