using LenxTool.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Networking;

public static class ArticleImageServiceCollectionExtensions
{
    public static IServiceCollection AddArticleImages(
        this IServiceCollection services,
        ArticleImageDownloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<IArticleImageTransport, PinnedArticleImageTransport>();
        services.AddSingleton<IArticleImageDownloader, CachedArticleImageDownloader>();
        return services;
    }
}
