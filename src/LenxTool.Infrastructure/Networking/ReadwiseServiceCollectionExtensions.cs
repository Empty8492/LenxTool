using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// 注册 Reader Save API 客户端与无副作用 auth 健康探针；凭据仍由共享 DPAPI 存储提供。
/// </summary>
public static class ReadwiseServiceCollectionExtensions
{
    public static IServiceCollection AddReadwiseExportInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IReadwiseApiClient, ReadwiseApiClient>();
        services.AddSingleton<
            IEntryIntegrationHealthProbe,
            ReadwiseEntryIntegrationHealthProbe>();
        return services;
    }
}

/// <summary>
/// Readwise 健康探针只访问固定 auth 端点，并复用共享健康服务已经校验、固定的公网地址。
/// </summary>
internal sealed class ReadwiseEntryIntegrationHealthProbe(
    IReadwiseApiClient client)
    : IEntryIntegrationHealthProbe
{
    public EntryIntegrationKind Kind => EntryIntegrationKind.Readwise;

    public async Task<EntryIntegrationProbeResult> ProbeAsync(
        EntryIntegrationProbeContext context,
        string credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Uri.Compare(
                context.Endpoint,
                ReadwiseApiClient.ApiRoot,
                UriComponents.AbsoluteUri,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            return new(EntryIntegrationHealthStatus.BlockedEndpoint);
        }
        try
        {
            await client.ProbePinnedAsync(
                    credential,
                    context.PinnedAddresses,
                    cancellationToken)
                .ConfigureAwait(false);
            return EntryIntegrationProbeResult.Healthy();
        }
        catch (ReadwiseApiException exception)
            when (exception.Failure == ReadwiseApiFailure.Cancelled
                  && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ReadwiseApiException exception)
        {
            return exception.Failure switch
            {
                ReadwiseApiFailure.Unauthorized =>
                    new(EntryIntegrationHealthStatus.Unauthorized),
                ReadwiseApiFailure.BlockedEndpoint =>
                    new(EntryIntegrationHealthStatus.BlockedEndpoint),
                ReadwiseApiFailure.RateLimited =>
                    new(
                        EntryIntegrationHealthStatus.RateLimited,
                        exception.RetryAfter),
                _ => new(EntryIntegrationHealthStatus.Unavailable)
            };
        }
        catch (ArgumentException)
        {
            // 共享上下文已固定官方端点与公网地址，剩余参数错误只能来自凭据格式。
            return new(EntryIntegrationHealthStatus.Unauthorized);
        }
        catch
        {
            return new(EntryIntegrationHealthStatus.Unavailable);
        }
    }
}
