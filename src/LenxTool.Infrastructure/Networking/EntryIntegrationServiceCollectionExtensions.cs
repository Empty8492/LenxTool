using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LenxTool.Infrastructure.Networking;

public static class EntryIntegrationServiceCollectionExtensions
{
    /// <summary>
    /// 注册 P2-08 安全基础设施。未注册具体探针时健康检查只返回 AdapterUnavailable，
    /// 因而默认不会访问任何第三方地址。
    /// </summary>
    public static IServiceCollection AddEntryIntegrationInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<
            IEntryIntegrationPolicyService,
            WorkerEntryIntegrationPolicyService>();
        services.AddSingleton<
            IEntryIntegrationCredentialStore,
            EntryIntegrationCredentialStore>();
        services.TryAddSingleton(EntryIntegrationHealthOptions.Default);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<
            IEntryIntegrationEndpointAuthorizer,
            EntryIntegrationEndpointAuthorizer>();
        services.AddSingleton<IEntryIntegrationHealthService>(
            static provider =>
                new EntryIntegrationHealthService(
                    provider.GetRequiredService<
                        IEntryIntegrationPolicyService>(),
                    provider.GetRequiredService<
                        IEntryIntegrationCredentialStore>(),
                    provider.GetServices<
                        IEntryIntegrationHealthProbe>(),
                    provider.GetRequiredService<IFeedHostResolver>(),
                    provider.GetRequiredService<
                        EntryIntegrationHealthOptions>(),
                    provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
