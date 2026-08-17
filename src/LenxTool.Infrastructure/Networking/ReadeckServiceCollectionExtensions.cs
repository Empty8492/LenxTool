using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Networking;

public static class ReadeckServiceCollectionExtensions
{
    public static IServiceCollection AddReadeckExportInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IReadeckApiClient, ReadeckApiClient>();
        services.AddSingleton<IEntryIntegrationHealthProbe, ReadeckHealthProbe>();
        services.AddSingleton<IIntegrationExportTargetStore<ReadeckExportTarget>>(
            static provider => new AppSettingsIntegrationExportTargetStore<ReadeckExportTarget>(
                provider.GetRequiredService<IAppSettingsRepository>(),
                ReadeckExportTarget.SettingsKey,
                ReadeckExportTarget.Normalize));
        return services;
    }
}

internal sealed class ReadeckHealthProbe(IReadeckApiClient client)
    : IEntryIntegrationHealthProbe
{
    public EntryIntegrationKind Kind => EntryIntegrationKind.Readeck;

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
        catch (ReadeckApiException exception)
            when (exception.Failure == ReadeckApiFailure.Cancelled
                && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ReadeckApiException exception)
        {
            return exception.Failure switch
            {
                ReadeckApiFailure.Unauthorized =>
                    new(EntryIntegrationHealthStatus.Unauthorized),
                ReadeckApiFailure.BlockedEndpoint =>
                    new(EntryIntegrationHealthStatus.BlockedEndpoint),
                ReadeckApiFailure.RateLimited =>
                    new(
                        EntryIntegrationHealthStatus.RateLimited,
                        exception.RetryAfter),
                _ => new(EntryIntegrationHealthStatus.Unavailable)
            };
        }
    }
}
