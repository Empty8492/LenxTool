using LenxTool.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Networking;

public static class FeedDiscoveryServiceCollectionExtensions
{
    public static IServiceCollection AddFeedDiscovery(
        this IServiceCollection services,
        FeedDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<IFeedHostResolver, SystemFeedHostResolver>();
        services.AddSingleton<IFeedDiscoveryTransport, PinnedFeedDiscoveryTransport>();
        services.AddSingleton<IFeedDiscoveryService, FeedDiscoveryService>();
        return services;
    }
}
