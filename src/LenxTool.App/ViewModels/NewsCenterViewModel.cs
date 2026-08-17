using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.App.ViewModels;

public sealed partial class NewsCenterViewModel
    : PageViewModel, INavigationAware, IEntityNavigationAware, IDisposable
{
    private static readonly string[] SectionTitles =
        ["资讯列表", "每日早报", "热点趋势", "AI 报告"];

    private readonly INewsCenterService _newsCenterService;
    private readonly IAiReportService _aiReportService;
    private readonly INewsRepository _repository;
    private readonly IDesktopFileDialogService _dialogs;
    private readonly FeedDigestScheduleViewModel? _feedDigestSchedule;
    private readonly IAiReportFileDialogService? _aiReportDialogs;
    private readonly IAiReportTextExportService? _aiReportExporter;
    private NewsArticle? _selectedArticle;
    private AiReport? _selectedReport;
    private DateOnly? _selectedDate;
    private string _status = "正在读取本地缓存…";
    private string _reportStatus = "可为当前早报生成单条解读，或基于热点生成每日趋势报告。";
    private AppError? _reportError;
    private string _keyword = string.Empty;
    private bool _suppressSourceFilterChanges;
    private int _selectedSectionIndex;
    private int _selectedFeedViewIndex;
    private bool _pictureFeedInitialized;
    private Task _pictureFeedInitialization = Task.CompletedTask;
    private readonly IFeedAudioPlaybackService? _feedAudioPlayback;
    private readonly IFeedMediaDeliveryService? _feedMediaDelivery;
    private readonly IMediaJobInbox? _mediaJobInbox;
    private readonly IAppNavigationService? _appNavigation;
    private bool _audioFeedInitialized;
    private Task _audioFeedInitialization = Task.CompletedTask;
    private readonly IFeedVideoDeliveryPlanningService?
        _feedVideoDeliveryPlanning;
    private bool _videoFeedInitialized;
    private Task _videoFeedInitialization = Task.CompletedTask;
    private bool _notificationFeedInitialized;
    private Task _notificationFeedInitialization = Task.CompletedTask;

    public NewsCenterViewModel(
        INewsCenterService newsCenterService,
        IAiReportService aiReportService,
        INewsRepository repository,
        IDesktopFileDialogService dialogs,
        IFeedEntryRepository feedEntryRepository,
        IFeedCatalogRepository feedCatalogRepository,
        IFeedCatalogSyncService feedCatalogSync,
        IEntryStateRepository entryStateRepository,
        IFavoriteRepository favoriteRepository,
        IFeedFullTextQueueService feedFullTextQueueService,
        IFeedAiSummaryService feedAiSummaryService,
        IFeedAiTranslationService feedAiTranslationService,
        IFeedAudioPlaybackService? feedAudioPlayback = null,
        IFeedMediaDeliveryService? feedMediaDelivery = null,
        IMediaJobInbox? mediaJobInbox = null,
        IAppNavigationService? appNavigation = null,
        IFeedVideoDeliveryPlanningService?
            feedVideoDeliveryPlanning = null,
        IFeedSmartViewRepository? feedSmartViewRepository = null,
        IFeedSmartViewSyncService? feedSmartViewSync = null,
        TimeProvider? timeProvider = null,
        IEntryExportQueueService? entryExportQueueService = null,
        IEntryIntegrationPolicyService? entryIntegrationPolicyService = null,
        IObsidianExportTargetStore? obsidianExportTargetStore = null,
        IEagleExportTargetStore? eagleExportTargetStore = null,
        IEagleApiClient? eagleApiClient = null,
        IZoteroExportTargetStore? zoteroExportTargetStore = null,
        IEntryIntegrationCredentialStore?
            entryIntegrationCredentialStore = null,
        FeedDigestScheduleViewModel? feedDigestSchedule = null,
        IAiReportFileDialogService? aiReportDialogs = null,
        IAiReportTextExportService? aiReportExporter = null,
        IIntegrationExportTargetStore<ReadeckExportTarget>?
            readeckExportTargetStore = null,
        IIntegrationExportTargetStore<OutlineExportTarget>?
            outlineExportTargetStore = null,
        IIntegrationExportTargetStore<QBittorrentExportTarget>?
            qbittorrentExportTargetStore = null,
        IIntegrationExportTargetStore<WebhookExportTarget>?
            webhookExportTargetStore = null)
        : base("资讯列表", "订阅资讯、每日早报、热点趋势与 AI 报告")
    {
        bool hasSharedMediaDependency =
            feedMediaDelivery is not null
            || mediaJobInbox is not null
            || appNavigation is not null;
        bool hasCompleteSharedMediaDependencies =
            feedMediaDelivery is not null
            && mediaJobInbox is not null
            && appNavigation is not null;
        if ((hasSharedMediaDependency
                && !hasCompleteSharedMediaDependencies)
            || (feedAudioPlayback is not null
                && !hasCompleteSharedMediaDependencies)
            || (feedVideoDeliveryPlanning is not null
                && !hasCompleteSharedMediaDependencies))
        {
            throw new ArgumentException(
                "媒体视图的共享依赖必须完整提供或全部省略。",
                nameof(feedMediaDelivery));
        }
        if ((aiReportDialogs is null) != (aiReportExporter is null))
        {
            throw new ArgumentException(
                "AI 报告导出对话框与写入服务必须同时提供或同时省略。",
                nameof(aiReportDialogs));
        }

        _newsCenterService = newsCenterService;
        _aiReportService = aiReportService;
        _repository = repository;
        _dialogs = dialogs;
        _feedDigestSchedule = feedDigestSchedule;
        _aiReportDialogs = aiReportDialogs;
        _aiReportExporter = aiReportExporter;
        _feedEntryRepository = feedEntryRepository;
        _feedCatalogRepository = feedCatalogRepository;
        _feedCatalogSync = feedCatalogSync;
        _entryStateRepository = entryStateRepository;
        _favoriteRepository = favoriteRepository;
        _feedFullTextQueueService = feedFullTextQueueService;
        _feedAiSummaryService = feedAiSummaryService;
        _feedAiTranslationService = feedAiTranslationService;
        _feedAudioPlayback = feedAudioPlayback;
        _feedMediaDelivery = feedMediaDelivery;
        _mediaJobInbox = mediaJobInbox;
        _appNavigation = appNavigation;
        _feedVideoDeliveryPlanning =
            feedVideoDeliveryPlanning;
        _feedSmartViewRepository = feedSmartViewRepository;
        _feedSmartViewSync = feedSmartViewSync;
        _timelineTimeProvider = timeProvider ?? TimeProvider.System;
        _timelineSynchronizationContext =
            SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext dispatcherContext
            && System.Windows.Application.Current is not null
                ? dispatcherContext
                : null;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ReloadReportsCommand = new AsyncRelayCommand(ReloadReportsAsync);
        GenerateArticleReportCommand = new AsyncRelayCommand(
            GenerateArticleReportAsync,
            () => SelectedArticle is not null);
        GenerateDailyTrendReportCommand = new AsyncRelayCommand(
            GenerateDailyTrendReportAsync,
            () => TrendGroups.Count > 0);
        ExportSelectedReportCommand = new AsyncRelayCommand(
            ExportSelectedReportAsync,
            () => SelectedReport is not null
                && _aiReportDialogs is not null
                && _aiReportExporter is not null);
        OpenTrendCommand = new RelayCommand<TrendItem>(OpenTrend, CanOpenTrend);
        SelectAllSourcesCommand = new RelayCommand(
            SelectAllSources,
            () => SourceFilters.Any(filter => !filter.IsSelected));
        ConfigureTimeline();
        ConfigureFeedReader();
        ConfigureEntryExports(
            entryExportQueueService,
            entryIntegrationPolicyService,
            obsidianExportTargetStore,
            eagleExportTargetStore,
            eagleApiClient,
            zoteroExportTargetStore,
            entryIntegrationCredentialStore,
            readeckExportTargetStore,
            outlineExportTargetStore,
            qbittorrentExportTargetStore,
            webhookExportTargetStore);
    }

    public ObservableCollection<NewsArticle> Articles { get; } = [];
    public ObservableCollection<DateOnly> ArticleDates { get; } = [];
    public ObservableCollection<TrendItem> Trends { get; } = [];
    public ObservableCollection<TrendPlatformGroup> TrendGroups { get; } = [];
    public ObservableCollection<TrendSourceFilter> SourceFilters { get; } = [];
    public ObservableCollection<AiReport> Reports { get; } = [];
    public FeedDigestScheduleViewModel? FeedDigestSchedule =>
        _feedDigestSchedule;
    public FeedContentCollectionViewModel? PictureFeed { get; private set; }
    public Task PictureFeedInitialization => _pictureFeedInitialization;
    public FeedAudioViewModel? AudioFeed { get; private set; }
    public Task AudioFeedInitialization => _audioFeedInitialization;
    public FeedVideoViewModel? VideoFeed { get; private set; }
    public Task VideoFeedInitialization => _videoFeedInitialization;
    public FeedContentCollectionViewModel? NotificationFeed { get; private set; }
    public Task NotificationFeedInitialization =>
        _notificationFeedInitialization;
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ReloadReportsCommand { get; }
    public AsyncRelayCommand GenerateArticleReportCommand { get; }
    public AsyncRelayCommand GenerateDailyTrendReportCommand { get; }
    public AsyncRelayCommand ExportSelectedReportCommand { get; }
    public RelayCommand<TrendItem> OpenTrendCommand { get; }
    public RelayCommand SelectAllSourcesCommand { get; }
    public int SelectedSectionIndex
    {
        get => _selectedSectionIndex;
        set
        {
            if (value < 0 || value >= SectionTitles.Length
                || !SetProperty(ref _selectedSectionIndex, value))
            {
                return;
            }

            // 滚轮手感已下沉到全局 ScrollViewer，栏目切换只更新页面语义。
            OnPropertyChanged(nameof(ActiveSectionTitle));
        }
    }

    public string ActiveSectionTitle => SectionTitles[SelectedSectionIndex];
    public int SelectedFeedViewIndex
    {
        get => _selectedFeedViewIndex;
        set
        {
            if (value < 0 || value > 4
                || !SetProperty(ref _selectedFeedViewIndex, value))
            {
                return;
            }

            if (value == 1)
            {
                _pictureFeedInitialization = StartPictureFeedInitialization();
                OnPropertyChanged(nameof(PictureFeedInitialization));
            }
            else if (value == 2)
            {
                _audioFeedInitialization = StartAudioFeedInitialization();
                OnPropertyChanged(nameof(AudioFeedInitialization));
            }
            else if (value == 3)
            {
                _videoFeedInitialization = StartVideoFeedInitialization();
                OnPropertyChanged(nameof(VideoFeedInitialization));
            }
            else if (value == 4)
            {
                _notificationFeedInitialization =
                    StartNotificationFeedInitialization();
                OnPropertyChanged(nameof(NotificationFeedInitialization));
            }
        }
    }
    public string SelectedSourceSummary =>
        $"已显示 {SourceFilters.Count(filter => filter.IsSelected)}/{SourceFilters.Count} 个来源";

    public void OnNavigated(string routeId)
    {
        int sectionIndex = routeId switch
        {
            "daily-briefing" => 1,
            "trends" => 2,
            "ai-reports" => 3,
            _ => 0
        };
        SelectedSectionIndex = sectionIndex;
    }

    public DateOnly? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (SetProperty(ref _selectedDate, value))
            {
                SelectedArticle = value is null
                    ? null
                    : Articles.FirstOrDefault(article => article.PublishedDate == value.Value);
            }
        }
    }

    public NewsArticle? SelectedArticle
    {
        get => _selectedArticle;
        private set
        {
            if (SetProperty(ref _selectedArticle, value))
            {
                GenerateArticleReportCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public AiReport? SelectedReport
    {
        get => _selectedReport;
        set
        {
            if (SetProperty(ref _selectedReport, value))
            {
                ExportSelectedReportCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ReportStatus
    {
        get => _reportStatus;
        private set => SetProperty(ref _reportStatus, value);
    }

    public AppError? ReportError
    {
        get => _reportError;
        private set => SetProperty(ref _reportError, value);
    }

    public string Keyword
    {
        get => _keyword;
        set => SetProperty(ref _keyword, value ?? string.Empty);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        NewsCenterSnapshot snapshot = await _newsCenterService.LoadCachedAsync(cancellationToken);
        ApplySnapshot(snapshot);
        await LoadReportsAsync(cancellationToken);
        if (_feedDigestSchedule is not null)
        {
            await _feedDigestSchedule.InitializeAsync(cancellationToken);
        }
        await InitializeTimelineAsync(cancellationToken);
    }

    public void Dispose()
    {
        foreach (TrendSourceFilter filter in SourceFilters)
            filter.PropertyChanged -= OnSourceFilterChanged;
        RefreshCommand.Dispose();
        ReloadReportsCommand.Dispose();
        GenerateArticleReportCommand.Dispose();
        GenerateDailyTrendReportCommand.Dispose();
        ExportSelectedReportCommand.Dispose();
        PictureFeed?.Dispose();
        AudioFeed?.Dispose();
        VideoFeed?.Dispose();
        NotificationFeed?.Dispose();
        _feedDigestSchedule?.Dispose();
        DisposeEntryExports();
        DisposeTimeline();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Status = "正在并行更新早报与热点…";
        NewsCenterSnapshot snapshot = await _newsCenterService.RefreshAsync(cancellationToken);
        ApplySnapshot(snapshot);
        await ReloadTimelineCatalogAsync(preserveSelection: true, cancellationToken);
        await RefreshTimelineSmartViewsAsync(cancellationToken);
    }

    private void OpenFeedContentUri(string uri) => _dialogs.OpenUri(uri);

    private Task StartPictureFeedInitialization()
    {
        if (_pictureFeedInitialized)
        {
            return Task.CompletedTask;
        }
        if (!_pictureFeedInitialization.IsCompleted)
        {
            return _pictureFeedInitialization;
        }

        PictureFeed ??= new(
            EntryViewKind.Picture,
            "图片",
            _feedEntryRepository,
            _feedCatalogRepository,
            _entryStateRepository,
            _favoriteRepository,
            OpenFeedContentUri);
        OnPropertyChanged(nameof(PictureFeed));
        return InitializePictureFeedCoreAsync(PictureFeed);
    }

    private async Task InitializePictureFeedCoreAsync(
        FeedContentCollectionViewModel pictureFeed)
    {
        try
        {
            await pictureFeed.InitializeAsync(CancellationToken.None);
            _pictureFeedInitialized = true;
        }
        catch (Exception exception)
        {
            pictureFeed.ReportLoadFailure(exception);
        }
    }

    private Task StartAudioFeedInitialization()
    {
        if (_audioFeedInitialized)
        {
            return Task.CompletedTask;
        }
        if (!_audioFeedInitialization.IsCompleted)
        {
            return _audioFeedInitialization;
        }
        if (_feedAudioPlayback is null
            || _feedMediaDelivery is null
            || _mediaJobInbox is null
            || _appNavigation is null)
        {
            return Task.CompletedTask;
        }

        AudioFeed ??= new(
            new(
                EntryViewKind.Audio,
                "音频",
                _feedEntryRepository,
                _feedCatalogRepository,
                _entryStateRepository,
                _favoriteRepository,
                OpenFeedContentUri),
            _entryStateRepository,
            _feedAudioPlayback,
            _feedMediaDelivery,
            _mediaJobInbox,
            _appNavigation,
            OpenFeedContentUri);
        OnPropertyChanged(nameof(AudioFeed));
        return InitializeAudioFeedCoreAsync(AudioFeed);
    }

    private async Task InitializeAudioFeedCoreAsync(
        FeedAudioViewModel audioFeed)
    {
        try
        {
            await audioFeed.InitializeAsync(CancellationToken.None);
            _audioFeedInitialized = true;
        }
        catch (Exception exception)
        {
            audioFeed.ReportLoadFailure(exception);
        }
    }

    private Task StartVideoFeedInitialization()
    {
        if (_videoFeedInitialized)
        {
            return Task.CompletedTask;
        }
        if (!_videoFeedInitialization.IsCompleted)
        {
            return _videoFeedInitialization;
        }
        if (_feedVideoDeliveryPlanning is null
            || _feedMediaDelivery is null
            || _mediaJobInbox is null
            || _appNavigation is null)
        {
            return Task.CompletedTask;
        }

        VideoFeed ??= new(
            new(
                EntryViewKind.Video,
                "视频",
                _feedEntryRepository,
                _feedCatalogRepository,
                _entryStateRepository,
                _favoriteRepository,
                OpenFeedContentUri),
            _feedVideoDeliveryPlanning,
            _feedMediaDelivery,
            _mediaJobInbox,
            _appNavigation,
            OpenFeedContentUri);
        OnPropertyChanged(nameof(VideoFeed));
        return InitializeVideoFeedCoreAsync(VideoFeed);
    }

    private async Task InitializeVideoFeedCoreAsync(
        FeedVideoViewModel videoFeed)
    {
        try
        {
            await videoFeed.InitializeAsync(CancellationToken.None);
            _videoFeedInitialized = true;
        }
        catch (Exception exception)
        {
            videoFeed.ReportLoadFailure(exception);
        }
    }

    private Task StartNotificationFeedInitialization()
    {
        if (_notificationFeedInitialized)
        {
            return Task.CompletedTask;
        }
        if (!_notificationFeedInitialization.IsCompleted)
        {
            return _notificationFeedInitialization;
        }

        NotificationFeed ??= new(
            EntryViewKind.Notification,
            "通知",
            _feedEntryRepository,
            _feedCatalogRepository,
            _entryStateRepository,
            _favoriteRepository,
            OpenFeedContentUri);
        OnPropertyChanged(nameof(NotificationFeed));
        return InitializeNotificationFeedCoreAsync(NotificationFeed);
    }

    private async Task InitializeNotificationFeedCoreAsync(
        FeedContentCollectionViewModel notificationFeed)
    {
        try
        {
            await notificationFeed.InitializeAsync(CancellationToken.None);
            _notificationFeedInitialized = true;
        }
        catch (Exception exception)
        {
            notificationFeed.ReportLoadFailure(exception);
        }
    }

    private async Task GenerateArticleReportAsync(CancellationToken cancellationToken)
    {
        if (SelectedArticle is null) return;
        await GenerateAndSaveAsync(
            token => _aiReportService.GenerateArticleInsightAsync(SelectedArticle, token),
            cancellationToken);
    }

    private async Task GenerateDailyTrendReportAsync(CancellationToken cancellationToken)
    {
        TrendItem[] snapshot = TrendGroups.SelectMany(group => group.Items).ToArray();
        if (snapshot.Length == 0) return;
        await GenerateAndSaveAsync(
            token => _aiReportService.GenerateDailyTrendReportAsync(snapshot, token),
            cancellationToken);
    }

    private async Task ExportSelectedReportAsync(
        CancellationToken cancellationToken)
    {
        AiReport? report = SelectedReport;
        if (report is null
            || _aiReportDialogs is null
            || _aiReportExporter is null)
        {
            return;
        }
        string timestamp = report.CreatedAt.UtcDateTime.ToString(
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture);
        string? path = _aiReportDialogs.PickAiReportExport(
            $"LenxTool-AI-report-{timestamp}.txt");
        if (path is null)
        {
            ReportStatus = "已取消报告导出。";
            return;
        }

        ReportError = null;
        ReportStatus = "正在导出本地报告…";
        try
        {
            await _aiReportExporter.ExportAsync(
                path,
                report,
                cancellationToken).ConfigureAwait(true);
            ReportStatus = $"报告已导出 · {Path.GetFileName(path)}";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            ReportStatus = $"报告导出失败：{exception.Message}";
        }
    }

    private static bool CanOpenTrend(TrendItem? trend) =>
        trend is not null
        && Uri.TryCreate(trend.Url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "http" or "https";

    private void OpenTrend(TrendItem? trend)
    {
        if (!CanOpenTrend(trend)) return;
        _dialogs.OpenUri(trend!.Url);
    }

    private async Task GenerateAndSaveAsync(
        Func<CancellationToken, Task<AiReport>> generate,
        CancellationToken cancellationToken)
    {
        ReportError = null;
        ReportStatus = "正在调用 DeepSeek 生成报告…";
        try
        {
            AiReport report = await generate(cancellationToken);
            await _repository.UpsertReportAsync(report, cancellationToken);
            AiReport? existing = Reports.FirstOrDefault(item => item.Id == report.Id);
            if (existing is not null) Reports.Remove(existing);
            Reports.Insert(0, report);
            SelectedReport = report;
            ReportStatus = $"报告已生成 · {report.TokenUsage} tokens";
        }
        catch (AppException exception)
        {
            ReportError = exception.Error;
            ReportStatus = $"{exception.Error.UserMessage} {exception.Error.Suggestion}";
        }
    }

    private async Task LoadReportsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AiReport> reports = await _repository.GetLatestReportsAsync(50, cancellationToken);
        Reports.Clear();
        foreach (AiReport report in reports) Reports.Add(report);
        SelectedReport = Reports.FirstOrDefault();
    }

    private async Task ReloadReportsAsync(CancellationToken cancellationToken)
    {
        ReportStatus = "正在读取本地报告库…";
        try
        {
            await LoadReportsAsync(cancellationToken).ConfigureAwait(true);
            ReportStatus = $"报告库已刷新 · {Reports.Count} 份";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // UI 边界不回显可能包含本机路径的数据库异常。
            ReportStatus = "本地报告库刷新失败，请稍后重试。";
        }
    }

    private void ApplySnapshot(NewsCenterSnapshot snapshot)
    {
        SelectedDate = null;
        Articles.Clear();
        foreach (NewsArticle article in snapshot.Articles.OrderByDescending(article => article.PublishedDate))
        {
            Articles.Add(article);
        }

        ArticleDates.Clear();
        foreach (DateOnly date in Articles.Select(article => article.PublishedDate).Distinct())
        {
            ArticleDates.Add(date);
        }

        Dictionary<string, int> sourceOrder = TrendSourceCatalog.Default
            .Select((source, index) => (source.Name, index))
            .ToDictionary(item => item.Name, item => item.index, StringComparer.Ordinal);
        TrendItem[] orderedTrends = snapshot.Trends
            .OrderBy(trend => sourceOrder.GetValueOrDefault(trend.Platform, int.MaxValue))
            .ThenBy(trend => trend.Platform, StringComparer.CurrentCulture)
            .ThenBy(trend => trend.Rank)
            .ToArray();

        Trends.Clear();
        foreach (TrendItem trend in orderedTrends)
        {
            Trends.Add(trend);
        }
        HashSet<string> deselectedSources = SourceFilters
            .Where(filter => !filter.IsSelected)
            .Select(filter => filter.Platform)
            .ToHashSet(StringComparer.Ordinal);
        bool hadSourceFilters = SourceFilters.Count > 0;
        foreach (TrendSourceFilter filter in SourceFilters)
            filter.PropertyChanged -= OnSourceFilterChanged;
        SourceFilters.Clear();
        foreach (IGrouping<string, TrendItem> group in orderedTrends.GroupBy(
                     trend => trend.Platform,
                     StringComparer.Ordinal))
        {
            var filter = new TrendSourceFilter(
                group.Key,
                group.Count(),
                !hadSourceFilters || !deselectedSources.Contains(group.Key));
            filter.PropertyChanged += OnSourceFilterChanged;
            SourceFilters.Add(filter);
        }
        ApplySourceFilter();

        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        SelectedDate = ArticleDates.Contains(today) ? today : ArticleDates.FirstOrDefault();
        if (ArticleDates.Count == 0)
        {
            SelectedDate = null;
            SelectedArticle = null;
        }

        string cache = snapshot.CacheTime is null
            ? "暂无本地内容"
            : $"缓存于 {snapshot.CacheTime.Value.ToLocalTime():MM-dd HH:mm}";
        Status = snapshot.Warning is null
            ? snapshot.IsFromCache ? cache : $"更新完成 · {cache}"
            : $"{snapshot.Warning} · {cache}";
    }

    private void OnSourceFilterChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_suppressSourceFilterChanges
            || args.PropertyName != nameof(TrendSourceFilter.IsSelected)) return;
        ApplySourceFilter();
    }

    private void SelectAllSources()
    {
        _suppressSourceFilterChanges = true;
        try
        {
            foreach (TrendSourceFilter filter in SourceFilters) filter.IsSelected = true;
        }
        finally
        {
            _suppressSourceFilterChanges = false;
        }
        ApplySourceFilter();
    }

    private void ApplySourceFilter()
    {
        HashSet<string> selectedSources = SourceFilters
            .Where(filter => filter.IsSelected)
            .Select(filter => filter.Platform)
            .ToHashSet(StringComparer.Ordinal);
        TrendGroups.Clear();
        foreach (IGrouping<string, TrendItem> group in Trends
                     .Where(trend => selectedSources.Contains(trend.Platform))
                     .GroupBy(trend => trend.Platform, StringComparer.Ordinal))
        {
            TrendGroups.Add(new(group.Key, group.ToArray()));
        }
        OnPropertyChanged(nameof(SelectedSourceSummary));
        SelectAllSourcesCommand.NotifyCanExecuteChanged();
        GenerateDailyTrendReportCommand.NotifyCanExecuteChanged();
    }
}
