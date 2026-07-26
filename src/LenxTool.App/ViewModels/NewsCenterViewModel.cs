using System.Collections.ObjectModel;
using System.ComponentModel;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

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
    private NewsArticle? _selectedArticle;
    private AiReport? _selectedReport;
    private DateOnly? _selectedDate;
    private string _status = "正在读取本地缓存…";
    private string _reportStatus = "可为当前早报生成单条解读，或基于热点生成每日趋势报告。";
    private AppError? _reportError;
    private string _keyword = string.Empty;
    private bool _suppressSourceFilterChanges;
    private int _selectedSectionIndex;

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
        IFeedAiTranslationService feedAiTranslationService)
        : base("资讯列表", "订阅资讯、每日早报、热点趋势与 AI 报告")
    {
        _newsCenterService = newsCenterService;
        _aiReportService = aiReportService;
        _repository = repository;
        _dialogs = dialogs;
        _feedEntryRepository = feedEntryRepository;
        _feedCatalogRepository = feedCatalogRepository;
        _feedCatalogSync = feedCatalogSync;
        _entryStateRepository = entryStateRepository;
        _favoriteRepository = favoriteRepository;
        _feedFullTextQueueService = feedFullTextQueueService;
        _feedAiSummaryService = feedAiSummaryService;
        _feedAiTranslationService = feedAiTranslationService;
        _timelineSynchronizationContext =
            SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext dispatcherContext
            && System.Windows.Application.Current is not null
                ? dispatcherContext
                : null;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GenerateArticleReportCommand = new AsyncRelayCommand(
            GenerateArticleReportAsync,
            () => SelectedArticle is not null);
        GenerateDailyTrendReportCommand = new AsyncRelayCommand(
            GenerateDailyTrendReportAsync,
            () => TrendGroups.Count > 0);
        OpenTrendCommand = new RelayCommand<TrendItem>(OpenTrend, CanOpenTrend);
        SelectAllSourcesCommand = new RelayCommand(
            SelectAllSources,
            () => SourceFilters.Any(filter => !filter.IsSelected));
        ConfigureTimeline();
        ConfigureFeedReader();
    }

    public ObservableCollection<NewsArticle> Articles { get; } = [];
    public ObservableCollection<DateOnly> ArticleDates { get; } = [];
    public ObservableCollection<TrendItem> Trends { get; } = [];
    public ObservableCollection<TrendPlatformGroup> TrendGroups { get; } = [];
    public ObservableCollection<TrendSourceFilter> SourceFilters { get; } = [];
    public ObservableCollection<AiReport> Reports { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand GenerateArticleReportCommand { get; }
    public AsyncRelayCommand GenerateDailyTrendReportCommand { get; }
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

            OnPropertyChanged(nameof(ActiveSectionTitle));
            OnPropertyChanged(nameof(WheelScrollMultiplier));
        }
    }

    public string ActiveSectionTitle => SectionTitles[SelectedSectionIndex];
    public double WheelScrollMultiplier => SelectedSectionIndex == 1 ? 1.45d : 1d;
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
        set => SetProperty(ref _selectedReport, value);
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
        await InitializeTimelineAsync(cancellationToken);
    }

    public void Dispose()
    {
        foreach (TrendSourceFilter filter in SourceFilters)
            filter.PropertyChanged -= OnSourceFilterChanged;
        RefreshCommand.Dispose();
        GenerateArticleReportCommand.Dispose();
        GenerateDailyTrendReportCommand.Dispose();
        DisposeTimeline();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Status = "正在并行更新早报与热点…";
        NewsCenterSnapshot snapshot = await _newsCenterService.RefreshAsync(cancellationToken);
        ApplySnapshot(snapshot);
        await ReloadTimelineCatalogAsync(preserveSelection: true, cancellationToken);
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
