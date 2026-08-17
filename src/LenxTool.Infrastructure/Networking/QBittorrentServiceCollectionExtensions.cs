using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Networking;

public static class QBittorrentServiceCollectionExtensions
{
    public static IServiceCollection AddQBittorrentExportInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IQBittorrentApiClient, QBittorrentApiClient>();
        services.AddSingleton<ITorrentFileFetcher, TorrentFileFetcher>();
        services.AddSingleton<
            IEntryIntegrationHealthProbe,
            QBittorrentHealthProbe>();
        services.AddSingleton<
            IIntegrationExportTargetStore<QBittorrentExportTarget>>(
            static provider =>
                new AppSettingsIntegrationExportTargetStore<QBittorrentExportTarget>(
                    provider.GetRequiredService<IAppSettingsRepository>(),
                    QBittorrentExportTarget.SettingsKey,
                    QBittorrentExportTarget.Normalize));
        return services;
    }
}

internal sealed class QBittorrentHealthProbe(IQBittorrentApiClient client)
    : IEntryIntegrationHealthProbe
{
    public EntryIntegrationKind Kind => EntryIntegrationKind.QBittorrent;

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
        catch (QBittorrentApiException exception)
            when (exception.Failure == QBittorrentApiFailure.Cancelled
                && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (QBittorrentApiException exception)
        {
            return exception.Failure switch
            {
                QBittorrentApiFailure.Unauthorized =>
                    new(EntryIntegrationHealthStatus.Unauthorized),
                QBittorrentApiFailure.BlockedEndpoint
                    or QBittorrentApiFailure.UnsupportedVersion =>
                    new(EntryIntegrationHealthStatus.BlockedEndpoint),
                QBittorrentApiFailure.RateLimited =>
                    new(
                        EntryIntegrationHealthStatus.RateLimited,
                        exception.RetryAfter),
                _ => new(EntryIntegrationHealthStatus.Unavailable)
            };
        }
    }
}
