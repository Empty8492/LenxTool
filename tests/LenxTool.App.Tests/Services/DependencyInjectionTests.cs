using System.Reflection;
using LenxTool.Core.Contracts;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.Networking;
using Microsoft.Extensions.DependencyInjection;

namespace LenxTool.App.Tests.Services;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void ConfigureServicesResolvesSubtitleTranslator()
    {
        var services = new ServiceCollection();
        MethodInfo configureServices = typeof(LenxTool.App.App).GetMethod(
            "ConfigureServices",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("App.ConfigureServices was not found.");

        configureServices.Invoke(null, [services]);
        using ServiceProvider provider = services.BuildServiceProvider();

        ISubtitleTranslator translator = provider.GetRequiredService<ISubtitleTranslator>();
        Assert.IsType<DeepSeekSubtitleTranslator>(translator);
        var account = provider.GetRequiredService<WorkerAccountSessionService>();
        Assert.Same(account, provider.GetRequiredService<IAccountSessionService>());
        Assert.IsType<FeedCatalogRepository>(provider.GetRequiredService<IFeedCatalogRepository>());
        Assert.IsType<FeedCatalogSyncService>(provider.GetRequiredService<IFeedCatalogSyncService>());
    }
}
