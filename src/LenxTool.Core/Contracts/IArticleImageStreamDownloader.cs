using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IArticleImageStreamDownloader
{
    Task<ArticleImageStreamContent?> OpenAsync(
        string entryId,
        string imageUrl,
        string? referrer,
        ArticleImageDownloadBudget budget,
        CancellationToken cancellationToken);
}
