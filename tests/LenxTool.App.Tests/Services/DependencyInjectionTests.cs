using System.Reflection;
using LenxTool.App.ViewModels;
using LenxTool.App.Services;
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
        Assert.Same(account, provider.GetRequiredService<IWorkerAiProxyClient>());
        Assert.IsType<DeepSeekChatTransport>(
            provider.GetRequiredService<IDeepSeekChatTransport>());
        Assert.IsType<FeedCatalogRepository>(provider.GetRequiredService<IFeedCatalogRepository>());
        Assert.IsType<FeedCatalogSyncService>(provider.GetRequiredService<IFeedCatalogSyncService>());
        Assert.IsType<FeedCatalogAdminService>(provider.GetRequiredService<IFeedCatalogAdminService>());
        Assert.Same(
            provider.GetRequiredService<IFeedCatalogAdminService>(),
            provider.GetRequiredService<IFeedCatalogBatchService>());
        Assert.NotNull(provider.GetRequiredService<IOpmlCodec>());
        Assert.NotNull(provider.GetRequiredService<IOpmlFileService>());
        Assert.Same(
            provider.GetRequiredService<IDesktopFileDialogService>(),
            provider.GetRequiredService<IOpmlFileDialogService>());
        Assert.NotNull(provider.GetRequiredService<FeedAdminViewModel>());
        Assert.NotNull(provider.GetRequiredService<IFeedDiscoveryService>());
        Assert.NotNull(provider.GetRequiredService<IFeedParser>());
        Assert.NotNull(provider.GetRequiredService<IArticleImageDownloader>());
        Assert.NotNull(provider.GetRequiredService<IArticleContentExtractor>());
        Assert.IsType<FeedFetchStateRepository>(provider.GetRequiredService<IFeedFetchStateRepository>());
        Assert.IsType<FeedEntryRepository>(provider.GetRequiredService<IFeedEntryWriter>());
        Assert.Same(
            provider.GetRequiredService<IFeedEntryWriter>(),
            provider.GetRequiredService<IFeedEntryRepository>());
        Assert.IsType<FeedFullTextRepository>(
            provider.GetRequiredService<IFeedFullTextRepository>());
        Assert.IsType<FeedAiResultRepository>(
            provider.GetRequiredService<IFeedAiResultRepository>());
        Assert.IsType<DeepSeekFeedAiSummaryService>(
            provider.GetRequiredService<IFeedAiSummaryService>());
        Assert.IsType<CachedFeedAiTranslationService>(
            provider.GetRequiredService<IFeedAiTranslationService>());
        Assert.IsType<FeedAiAutomationJobRepository>(
            provider.GetRequiredService<IFeedAiAutomationJobRepository>());
        Assert.IsType<FeedAutomationRunRepository>(
            provider.GetRequiredService<IFeedAutomationRunRepository>());
        Assert.IsType<FeedAutomationActionQueueRepository>(
            provider.GetRequiredService<IFeedAutomationActionQueueRepository>());
        Assert.IsType<FeedAutomationLocalActionService>(
            provider.GetRequiredService<IFeedAutomationLocalActionService>());
        Assert.IsType<FeedAutomationActionProcessor>(
            provider.GetRequiredService<IFeedAutomationActionProcessor>());
        Assert.IsType<FeedAiAutomationQueueService>(
            provider.GetRequiredService<IFeedAiAutomationQueueService>());
        Assert.IsType<FeedFullTextQueueService>(
            provider.GetRequiredService<IFeedFullTextQueueService>());
        Assert.NotNull(provider.GetRequiredService<IFeedRefreshService>());
        Assert.NotNull(provider.GetRequiredService<NewsCenterViewModel>());
        Assert.IsType<FavoriteRepository>(provider.GetRequiredService<IFavoriteRepository>());
        Assert.NotNull(provider.GetRequiredService<DashboardViewModel>());

        ShellViewModel shell = provider.GetRequiredService<ShellViewModel>();
        Assert.Equal(
            ["首页", "资讯列表", "每日早报", "热点趋势", "AI 报告"],
            shell.NavigationItems.Take(5).Select(item => item.Label));
    }
}
