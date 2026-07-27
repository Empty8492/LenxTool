using LenxTool.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LenxTool.Infrastructure.Networking;

public static class FeedDiscoveryServiceCollectionExtensions
{
    public static IServiceCollection AddFeedDiscovery(
        this IServiceCollection services,
        FeedDiscoveryOptions options,
        UnifiedFeedDiscoveryOptions? unifiedOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        unifiedOptions ??= UnifiedFeedDiscoveryOptions.Default;

        services.AddSingleton(options);
        services.AddSingleton(unifiedOptions);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFeedHostResolver, SystemFeedHostResolver>();
        services.AddSingleton<IFeedDiscoveryTransport, PinnedFeedDiscoveryTransport>();
        services.AddSingleton<FeedDiscoveryService>();
        services.AddSingleton<IFeedDiscoveryService>(static provider =>
            provider.GetRequiredService<FeedDiscoveryService>());
        services.AddSingleton(FeedParserOptions.Default);
        services.AddSingleton<IFeedParser, FeedDocumentParser>();
        services.TryAddSingleton<
            IKnownCatalogDiscoveryClient,
            WorkerKnownCatalogDiscoveryClient>();
        services.AddSingleton<IFeedDiscoveryProvider>(provider =>
            new DirectFeedDiscoveryProvider(
                provider.GetRequiredService<IFeedDiscoveryService>(),
                unifiedOptions.DirectProbe));
        services.AddSingleton<IFeedDiscoveryProvider>(provider =>
            new KnownCatalogFeedDiscoveryProvider(
                provider.GetRequiredService<IKnownCatalogDiscoveryClient>(),
                unifiedOptions.KnownCatalog));
        services.AddSingleton<UnifiedFeedDiscoveryCoordinator>();
        services.AddSingleton<IUnifiedFeedDiscoveryService>(static provider =>
            provider.GetRequiredService<UnifiedFeedDiscoveryCoordinator>());
        return services;
    }
}
