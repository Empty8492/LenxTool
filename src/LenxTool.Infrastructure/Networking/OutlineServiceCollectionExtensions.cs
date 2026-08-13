using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Networking;

public static class OutlineServiceCollectionExtensions
{
    public static IServiceCollection AddOutlineExportInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IOutlineApiClient, OutlineApiClient>();
        services.AddSingleton<
            IEntryIntegrationHealthProbe,
            OutlineEntryIntegrationHealthProbe>();
        services.AddSingleton<IIntegrationExportTargetStore<OutlineExportTarget>>(
            static provider => new AppSettingsIntegrationExportTargetStore<OutlineExportTarget>(
                provider.GetRequiredService<IAppSettingsRepository>(),
                OutlineExportTarget.SettingsKey,
                OutlineExportTarget.Normalize));
        return services;
    }
}

internal sealed class OutlineEntryIntegrationHealthProbe(
    IOutlineApiClient client)
    : IEntryIntegrationHealthProbe
{
    public EntryIntegrationKind Kind => EntryIntegrationKind.Outline;

    public async Task<EntryIntegrationProbeResult> ProbeAsync(
        EntryIntegrationProbeContext context,
        string credential,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.ProbeAsync(context, credential, cancellationToken)
                .ConfigureAwait(false);
            return EntryIntegrationProbeResult.Healthy();
        }
        catch (OutlineApiException exception)
            when (exception.Failure == OutlineApiFailure.Cancelled
                && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OutlineApiException exception)
        {
            return exception.Failure switch
            {
                OutlineApiFailure.Unauthorized =>
                    new(EntryIntegrationHealthStatus.Unauthorized),
                OutlineApiFailure.BlockedEndpoint =>
                    new(EntryIntegrationHealthStatus.BlockedEndpoint),
                OutlineApiFailure.RateLimited =>
                    new(
                        EntryIntegrationHealthStatus.RateLimited,
                        exception.RetryAfter),
                _ => new(EntryIntegrationHealthStatus.Unavailable)
            };
        }
        catch
        {
            return new(EntryIntegrationHealthStatus.Unavailable);
        }
    }
}
