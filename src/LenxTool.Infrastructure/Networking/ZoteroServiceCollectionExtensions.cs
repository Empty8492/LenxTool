using LenxTool.Core.Contracts;
using LenxTool.Infrastructure.Exports;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// 注册 Zotero 个人库的非敏感目标、API v3 客户端与只读健康探针。
/// 具体网络实现保持程序集内可见，业务层只能依赖封闭接口。
/// </summary>
public static class ZoteroServiceCollectionExtensions
{
    public static IServiceCollection AddZoteroExportInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<
            IZoteroExportTargetStore,
            AppSettingsZoteroExportTargetStore>();
        services.AddSingleton<IZoteroApiClient, ZoteroApiClient>();
        services.AddSingleton<
            IEntryIntegrationHealthProbe,
            ZoteroEntryIntegrationHealthProbe>();
        return services;
    }
}
