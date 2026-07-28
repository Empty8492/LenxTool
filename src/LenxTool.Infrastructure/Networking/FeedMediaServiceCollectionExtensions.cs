using LenxTool.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Networking;

public static class FeedMediaServiceCollectionExtensions
{
    public static IServiceCollection AddFeedMediaDelivery(
        this IServiceCollection services,
        FeedMediaDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<IFeedMediaTransport, PinnedFeedMediaTransport>();
        services.AddSingleton<
            IFeedMediaCompatibilityProbe,
            MediaFoundationFeedMediaCompatibilityProbe>();
        services.AddSingleton<IFeedMediaDeliveryService, FeedMediaDeliveryService>();
        return services;
    }
}
