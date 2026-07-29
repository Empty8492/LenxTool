using System.Reflection;
using LenxTool.App.ViewModels;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Exports;
using LenxTool.Core.Models;
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
        // 统一发现页必须消费 DISC-03 的协调服务，而不是旧 URL 单候选服务。
        Assert.NotNull(provider.GetRequiredService<FeedDiscoveryViewModel>());
        Assert.NotNull(provider.GetRequiredService<FeedAdminViewModel>());
        Assert.NotNull(provider.GetRequiredService<IFeedDiscoveryService>());
        Assert.NotNull(provider.GetRequiredService<IUnifiedFeedDiscoveryService>());
        IFeedDiscoveryProvider[] discoveryProviders =
            provider.GetServices<IFeedDiscoveryProvider>().ToArray();
        Assert.Collection(
            discoveryProviders,
            item => Assert.IsType<DirectFeedDiscoveryProvider>(item),
            item => Assert.IsType<KnownCatalogFeedDiscoveryProvider>(item));
        Assert.DoesNotContain(
            discoveryProviders,
            item => item.SourceKind is
                FeedDiscoverySourceKind.RssHubAdapter or
                FeedDiscoverySourceKind.ExternalProvider);
        Assert.NotNull(provider.GetRequiredService<IFeedParser>());
        Assert.NotNull(provider.GetRequiredService<IArticleImageDownloader>());
        Assert.IsType<FeedMediaDeliveryRepository>(
            provider.GetRequiredService<IFeedMediaDeliveryRepository>());
        Assert.NotNull(provider.GetRequiredService<IFeedMediaDeliveryService>());
        Assert.IsType<WpfFeedAudioPlaybackService>(
            provider.GetRequiredService<IFeedAudioPlaybackService>());
        Assert.IsType<FeedMediaStorageProbe>(
            provider.GetRequiredService<IFeedMediaStorageProbe>());
        Assert.IsType<FeedVideoDeliveryPlanningService>(
            provider.GetRequiredService<IFeedVideoDeliveryPlanningService>());
        Assert.IsType<MediaJobInbox>(
            provider.GetRequiredService<IMediaJobInbox>());
        Assert.NotNull(provider.GetRequiredService<IArticleContentExtractor>());
        Assert.IsType<FeedFetchStateRepository>(provider.GetRequiredService<IFeedFetchStateRepository>());
        Assert.IsType<FeedEntryRepository>(provider.GetRequiredService<IFeedEntryWriter>());
        Assert.Same(
            provider.GetRequiredService<IFeedEntryWriter>(),
            provider.GetRequiredService<IFeedEntryRepository>());
        Assert.IsType<FeedDiscoveryPreviewRepository>(
            provider.GetRequiredService<IFeedDiscoveryPreviewRepository>());
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
        Assert.IsType<FeedAutomationPlanningService>(
            provider.GetRequiredService<IFeedAutomationPlanningService>());
        Assert.IsType<FeedAutomationRuleRepository>(
            provider.GetRequiredService<IFeedAutomationRuleRepository>());
        Assert.IsType<FeedAutomationRuleSyncService>(
            provider.GetRequiredService<IFeedAutomationRuleSyncService>());
        Assert.IsType<FeedAutomationRuleAdminService>(
            provider.GetRequiredService<IFeedAutomationRuleAdminService>());
        Assert.IsType<FeedAutomationRuleSimulationService>(
            provider.GetRequiredService<IFeedAutomationRuleSimulationService>());
        Assert.IsType<FeedAutomationActionQueueRepository>(
            provider.GetRequiredService<IFeedAutomationActionQueueRepository>());
        Assert.IsType<FeedAutomationLocalActionService>(
            provider.GetRequiredService<IFeedAutomationLocalActionService>());
        Assert.IsType<FeedAutomationActionProcessor>(
            provider.GetRequiredService<IFeedAutomationActionProcessor>());
        Assert.IsType<FeedAutomationAiActionService>(
            provider.GetRequiredService<IFeedAutomationAiActionService>());
        Assert.IsType<FeedAutomationAiActionProcessor>(
            provider.GetRequiredService<IFeedAutomationAiActionProcessor>());
        Assert.IsType<FeedAutomationMediaActionService>(
            provider.GetRequiredService<IFeedAutomationMediaActionService>());
        Assert.IsType<FeedAutomationMediaActionProcessor>(
            provider.GetRequiredService<IFeedAutomationMediaActionProcessor>());
        Assert.IsType<AppNotificationRepository>(
            provider.GetRequiredService<IAppNotificationRepository>());
        Assert.IsType<AppNotificationInbox>(
            provider.GetRequiredService<IAppNotificationInbox>());
        Assert.IsType<LocalAppNotificationPublisher>(
            provider.GetRequiredService<IAppNotificationPublisher>());
        Assert.IsType<FeedAutomationNotificationActionService>(
            provider.GetRequiredService<
                IFeedAutomationNotificationActionService>());
        Assert.IsType<FeedAutomationNotificationActionProcessor>(
            provider.GetRequiredService<
                IFeedAutomationNotificationActionProcessor>());
        Assert.IsType<FeedAiAutomationQueueService>(
            provider.GetRequiredService<IFeedAiAutomationQueueService>());
        Assert.IsType<FeedFullTextQueueService>(
            provider.GetRequiredService<IFeedFullTextQueueService>());
        Assert.NotNull(provider.GetRequiredService<IFeedRefreshService>());
        Assert.NotNull(provider.GetRequiredService<NewsCenterViewModel>());
        Assert.IsType<FavoriteRepository>(provider.GetRequiredService<IFavoriteRepository>());
        Assert.NotNull(provider.GetRequiredService<DashboardViewModel>());
        Assert.NotNull(provider.GetRequiredService<NotificationCenterViewModel>());
        Assert.IsType<FeedSmartViewRepository>(
            provider.GetRequiredService<IFeedSmartViewRepository>());
        Assert.IsType<FeedSmartViewSyncService>(
            provider.GetRequiredService<IFeedSmartViewSyncService>());
        Assert.IsType<FeedSmartViewAdminService>(
            provider.GetRequiredService<IFeedSmartViewAdminService>());
        Assert.NotNull(provider.GetRequiredService<SmartViewAdminViewModel>());
        Assert.NotNull(provider.GetRequiredService<AutomationAdminViewModel>());
        Assert.IsType<WorkerEntryIntegrationPolicyService>(
            provider.GetRequiredService<
                IEntryIntegrationPolicyService>());
        Assert.NotNull(provider.GetRequiredService<
            IEntryIntegrationCredentialStore>());
        Assert.NotNull(provider.GetRequiredService<
            IEntryIntegrationHealthService>());
        Assert.Empty(provider.GetServices<
            IEntryIntegrationHealthProbe>());
        Assert.IsType<EntryExportTaskRepository>(
            provider.GetRequiredService<IEntryExportTaskRepository>());
        Assert.IsType<EntryExportCoordinator>(
            provider.GetRequiredService<IEntryExportCoordinator>());
        Assert.Same(
            provider.GetRequiredService<IEntryExportQueueService>(),
            provider.GetRequiredService<IEntryExportQueueProcessor>());
        // 没有显式目标配置时，不注册任何适配器，后台队列不会静默投递。
        Assert.Empty(provider.GetServices<IEntryExporter>());
        Assert.Empty(
            provider.GetRequiredService<IEntryExportCoordinator>()
                .Capabilities);
        Assert.NotNull(provider.GetRequiredService<
            IntegrationSettingsViewModel>());
        Assert.NotNull(provider.GetRequiredService<
            IntegrationAdminViewModel>());

        ShellViewModel shell = provider.GetRequiredService<ShellViewModel>();
        Assert.Equal(
            ["首页", "资讯列表", "每日早报", "热点趋势", "AI 报告"],
            shell.NavigationItems.Take(5).Select(item => item.Label));
    }
}
