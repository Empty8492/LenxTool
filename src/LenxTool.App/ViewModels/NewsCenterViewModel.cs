using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed class NewsCenterViewModel : PageViewModel, IDisposable
{
    private readonly INewsCenterService _newsCenterService;
    private readonly IAiReportService _aiReportService;
    private readonly INewsRepository _repository;
    private NewsArticle? _selectedArticle;
    private AiReport? _selectedReport;
    private DateOnly? _selectedDate;
    private string _status = "正在读取本地缓存…";
    private string _reportStatus = "可为当前早报生成单条解读，或基于热点生成每日趋势报告。";
    private AppError? _reportError;
    private string _keyword = string.Empty;

    public NewsCenterViewModel(
        INewsCenterService newsCenterService,
        IAiReportService aiReportService,
        INewsRepository repository)
        : base("资讯中心", "每日早报与热点趋势")
    {
        _newsCenterService = newsCenterService;
        _aiReportService = aiReportService;
        _repository = repository;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GenerateArticleReportCommand = new AsyncRelayCommand(
            GenerateArticleReportAsync,
            () => SelectedArticle is not null);
        GenerateDailyTrendReportCommand = new AsyncRelayCommand(
            GenerateDailyTrendReportAsync,
            () => Trends.Count > 0);
    }

    public ObservableCollection<NewsArticle> Articles { get; } = [];
    public ObservableCollection<DateOnly> ArticleDates { get; } = [];
    public ObservableCollection<TrendItem> Trends { get; } = [];
    public ObservableCollection<AiReport> Reports { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand GenerateArticleReportCommand { get; }
    public AsyncRelayCommand GenerateDailyTrendReportCommand { get; }

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
    }

    public void Dispose()
    {
        RefreshCommand.Dispose();
        GenerateArticleReportCommand.Dispose();
        GenerateDailyTrendReportCommand.Dispose();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Status = "正在并行更新早报与热点…";
        NewsCenterSnapshot snapshot = await _newsCenterService.RefreshAsync(cancellationToken);
        ApplySnapshot(snapshot);
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
        if (Trends.Count == 0) return;
        TrendItem[] snapshot = Trends.ToArray();
        await GenerateAndSaveAsync(
            token => _aiReportService.GenerateDailyTrendReportAsync(snapshot, token),
            cancellationToken);
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

        Trends.Clear();
        foreach (TrendItem trend in snapshot.Trends.OrderBy(trend => trend.Rank))
        {
            Trends.Add(trend);
        }
        GenerateDailyTrendReportCommand.NotifyCanExecuteChanged();

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
}
