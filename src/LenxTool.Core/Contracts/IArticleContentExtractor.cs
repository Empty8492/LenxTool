using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IArticleContentExtractor
{
    Task<ArticleContentResult> ExtractAsync(
        string url,
        CancellationToken cancellationToken);
}
