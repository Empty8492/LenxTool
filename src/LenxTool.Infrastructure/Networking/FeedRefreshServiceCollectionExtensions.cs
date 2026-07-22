using LenxTool.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Networking;

public static class FeedRefreshServiceCollectionExtensions
{
    public static IServiceCollection AddFeedRefresh(
        this IServiceCollection services,
        FeedRefreshOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<IFeedRefreshTransport, PinnedFeedRefreshTransport>();
        services.AddSingleton<IFeedRefreshService, FeedRefreshService>();
        return services;
    }
}
