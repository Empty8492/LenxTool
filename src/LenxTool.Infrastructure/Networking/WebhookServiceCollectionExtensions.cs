using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Networking;

public static class WebhookServiceCollectionExtensions
{
    public static IServiceCollection AddWebhookExportInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IWebhookApiClient, WebhookApiClient>();
        services.AddSingleton<IEntryIntegrationHealthProbe, WebhookHealthProbe>();
        services.AddSingleton<IIntegrationExportTargetStore<WebhookExportTarget>>(
            static provider => new AppSettingsIntegrationExportTargetStore<WebhookExportTarget>(
                provider.GetRequiredService<IAppSettingsRepository>(),
                WebhookExportTarget.SettingsKey,
                WebhookExportTarget.Normalize));
        return services;
    }
}

internal sealed class WebhookHealthProbe(IWebhookApiClient client)
    : IEntryIntegrationHealthProbe
{
    public EntryIntegrationKind Kind => EntryIntegrationKind.Webhook;
    public bool RequiresCredential => false;

    public async Task<EntryIntegrationProbeResult> ProbeAsync(
        EntryIntegrationProbeContext context,
        string credential,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.ProbeAsync(context, cancellationToken).ConfigureAwait(false);
            return EntryIntegrationProbeResult.Healthy();
        }
        catch (WebhookApiException exception)
            when (exception.Failure == WebhookApiFailure.Cancelled
                && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (WebhookApiException exception)
        {
            return exception.Failure switch
            {
                WebhookApiFailure.BlockedEndpoint
                    or WebhookApiFailure.CapabilityMissing =>
                    new(EntryIntegrationHealthStatus.BlockedEndpoint),
                WebhookApiFailure.RateLimited =>
                    new(
                        EntryIntegrationHealthStatus.RateLimited,
                        exception.RetryAfter),
                _ => new(EntryIntegrationHealthStatus.Unavailable)
            };
        }
    }
}
