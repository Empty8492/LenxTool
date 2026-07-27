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
        services.AddSingleton<CachedArticleImageDownloader>();
        services.AddSingleton<IArticleImageDownloader>(static services =>
            services.GetRequiredService<CachedArticleImageDownloader>());
        services.AddSingleton<IArticleImageStreamDownloader>(static services =>
            services.GetRequiredService<CachedArticleImageDownloader>());
        return services;
    }
}
