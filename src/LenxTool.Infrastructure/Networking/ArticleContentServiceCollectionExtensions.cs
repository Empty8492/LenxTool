using LenxTool.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Networking;

public static class ArticleContentServiceCollectionExtensions
{
    public static IServiceCollection AddArticleContentExtraction(
        this IServiceCollection services,
        ArticleContentExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<HtmlArticleContentParser>();
        services.AddSingleton<IArticleContentTransport, PinnedArticleContentTransport>();
        services.AddSingleton<IArticleContentExtractor, ArticleContentExtractor>();
        return services;
    }
}
