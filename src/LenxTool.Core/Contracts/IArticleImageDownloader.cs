using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IArticleImageDownloader
{
    Task<ArticleImageContent?> GetAsync(
        string entryId,
        string imageUrl,
        string? referrer,
        ArticleImageDownloadBudget budget,
        CancellationToken cancellationToken);
}
