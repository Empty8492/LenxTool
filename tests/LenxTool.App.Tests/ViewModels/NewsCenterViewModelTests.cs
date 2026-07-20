using LenxTool.App.ViewModels;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class NewsCenterViewModelTests
{
    [Fact]
    public async Task InitializeAsyncSelectsTodayByDefault()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        NewsArticle yesterday = CreateArticle("yesterday", today.AddDays(-1));
        NewsArticle current = CreateArticle("today", today);
        using NewsCenterViewModel viewModel = CreateViewModel(CreateSnapshot(yesterday, current));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(today, viewModel.SelectedDate);
        Assert.Equal("today", viewModel.SelectedArticle?.Id);
        Assert.Equal([today, today.AddDays(-1)], viewModel.ArticleDates);
    }

    [Fact]
    public async Task SelectedDateChangesDisplayedArticle()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly yesterday = today.AddDays(-1);
        using NewsCenterViewModel viewModel = CreateViewModel(CreateSnapshot(
            CreateArticle("today", today),
            CreateArticle("yesterday", yesterday)));
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedDate = yesterday;

        Assert.Equal("yesterday", viewModel.SelectedArticle?.Id);
    }

    [Fact]
    public async Task InitializeAsyncWhenTodayIsMissingSelectsNewestAvailableDate()
    {
        DateOnly newest = DateOnly.FromDateTime(DateTime.Today).AddDays(-2);
        using NewsCenterViewModel viewModel = CreateViewModel(CreateSnapshot(
            CreateArticle("older", newest.AddDays(-1)),
            CreateArticle("newest", newest)));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(newest, viewModel.SelectedDate);
        Assert.Equal("newest", viewModel.SelectedArticle?.Id);
    }

    [Fact]
    public async Task GenerateArticleReportPersistsAndSelectsResult()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        NewsArticle article = CreateArticle("today", today);
        AiReport generated = new(
            "report-1", "news", article.Id, "article_insight", "AI 解读",
            "核心判断：值得持续关注。", "deepseek-v4-flash", 1, 128, DateTimeOffset.UtcNow);
        var reports = new StubNewsRepository();
        using NewsCenterViewModel viewModel = new(
            new StubNewsCenterService(CreateSnapshot(article)),
            new StubAiReportService(generated),
            reports,
            new StubDesktopFileDialogService());
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.GenerateArticleReportCommand.ExecuteAsync();

        Assert.Equal(generated, reports.SavedReport);
        Assert.Same(generated, Assert.Single(viewModel.Reports));
        Assert.Same(generated, viewModel.SelectedReport);
        Assert.Equal("报告已生成 · 128 tokens", viewModel.ReportStatus);
    }

    [Fact]
    public async Task InitializeAsyncGroupsLocalRanksByPlatform()
    {
        NewsCenterSnapshot snapshot = new(
            [],
            [
                CreateTrend("github-1", "GitHub", 1),
                CreateTrend("hacker-news-1", "Hacker News", 1),
                CreateTrend("github-2", "GitHub", 2)
            ],
            true,
            DateTimeOffset.Now,
            null);
        using NewsCenterViewModel viewModel = CreateViewModel(snapshot);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Collection(
            viewModel.TrendGroups,
            group =>
            {
                Assert.Equal("GitHub", group.Platform);
                Assert.Equal([1, 2], group.Items.Select(item => item.Rank).ToArray());
            },
            group =>
            {
                Assert.Equal("Hacker News", group.Platform);
                Assert.Equal([1], group.Items.Select(item => item.Rank).ToArray());
            });
    }

    [Fact]
    public async Task SourceFilterHidesDeselectedPlatformAndCanRestoreAllSources()
    {
        NewsCenterSnapshot snapshot = new(
            [],
            [
                CreateTrend("github-1", "GitHub", 1),
                CreateTrend("hacker-news-1", "Hacker News", 1)
            ],
            true,
            DateTimeOffset.Now,
            null);
        using NewsCenterViewModel viewModel = CreateViewModel(snapshot);
        await viewModel.InitializeAsync(CancellationToken.None);

        TrendSourceFilter github = Assert.Single(
            viewModel.SourceFilters,
            filter => filter.Platform == "GitHub");
        github.IsSelected = false;

        TrendPlatformGroup visible = Assert.Single(viewModel.TrendGroups);
        Assert.Equal("Hacker News", visible.Platform);
        Assert.Equal("已显示 1/2 个来源", viewModel.SelectedSourceSummary);
        Assert.True(viewModel.SelectAllSourcesCommand.CanExecute(null));

        viewModel.SelectAllSourcesCommand.Execute(null);

        Assert.Equal(2, viewModel.TrendGroups.Count);
        Assert.All(viewModel.SourceFilters, filter => Assert.True(filter.IsSelected));
        Assert.False(viewModel.SelectAllSourcesCommand.CanExecute(null));
    }

    [Fact]
    public async Task TrendReportUsesOnlyCurrentlySelectedSources()
    {
        AiReport generated = new(
            "report-trends", "trend", "daily", "daily_trend", "趋势报告",
            "筛选后的趋势。", "deepseek-v4-flash", 1, 64, DateTimeOffset.UtcNow);
        var aiReports = new StubAiReportService(generated);
        using var viewModel = new NewsCenterViewModel(
            new StubNewsCenterService(new(
                [],
                [
                    CreateTrend("github-1", "GitHub", 1),
                    CreateTrend("hacker-news-1", "Hacker News", 1)
                ],
                true,
                DateTimeOffset.Now,
                null)),
            aiReports,
            new StubNewsRepository(),
            new StubDesktopFileDialogService());
        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.Single(viewModel.SourceFilters, filter => filter.Platform == "GitHub")
            .IsSelected = false;

        await viewModel.GenerateDailyTrendReportCommand.ExecuteAsync();

        TrendItem selected = Assert.Single(aiReports.LastTrendItems!);
        Assert.Equal("Hacker News", selected.Platform);
    }

    [Fact]
    public void OpenTrendCommandOnlyAcceptsHttpLinks()
    {
        var dialogs = new StubDesktopFileDialogService();
        using NewsCenterViewModel viewModel = CreateViewModel(CreateSnapshot(), dialogs);
        TrendItem safe = CreateTrend("safe", "知乎", 1);

        Assert.True(viewModel.OpenTrendCommand.CanExecute(safe));
        Assert.False(viewModel.OpenTrendCommand.CanExecute(
            CreateTrend("unsafe", "知乎", 1) with { Url = "javascript:alert(1)" }));
        viewModel.OpenTrendCommand.Execute(safe);
        Assert.Equal(safe.Url, dialogs.OpenedUri);
    }

    private static NewsCenterViewModel CreateViewModel(
        NewsCenterSnapshot snapshot,
        StubDesktopFileDialogService? dialogs = null) =>
        new(
            new StubNewsCenterService(snapshot),
            new StubAiReportService(null),
            new StubNewsRepository(),
            dialogs ?? new StubDesktopFileDialogService());

    private static NewsCenterSnapshot CreateSnapshot(params NewsArticle[] articles) =>
        new(articles, [], true, DateTimeOffset.Now, null);

    private static NewsArticle CreateArticle(string id, DateOnly date) =>
        new(id, date, "AI 早报", $"标题 {id}", $"摘要 {id}", $"正文 {id}", string.Empty, id, DateTimeOffset.Now);

    private static TrendItem CreateTrend(string id, string platform, int rank) =>
        new(id, platform, rank, $"标题 {id}", $"热度 {rank}", $"https://example.com/{id}", id, DateTimeOffset.Now);

    private sealed class StubNewsCenterService(NewsCenterSnapshot snapshot) : INewsCenterService
    {
        public Task<NewsCenterSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);

        public Task<NewsCenterSnapshot> LoadCachedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }

    private sealed class StubAiReportService(AiReport? report) : IAiReportService
    {
        public IReadOnlyList<TrendItem>? LastTrendItems { get; private set; }

        public Task<AiReport> GenerateArticleInsightAsync(
            NewsArticle article,
            CancellationToken cancellationToken) => Task.FromResult(
                report ?? throw new InvalidOperationException("本测试不应生成报告。"));

        public Task<AiReport> GenerateDailyTrendReportAsync(
            IReadOnlyList<TrendItem> trends,
            CancellationToken cancellationToken)
        {
            LastTrendItems = trends;
            return Task.FromResult(
                report ?? throw new InvalidOperationException("本测试不应生成报告。"));
        }
    }

    private sealed class StubNewsRepository : INewsRepository
    {
        public AiReport? SavedReport { get; private set; }

        public Task UpsertReportAsync(AiReport report, CancellationToken cancellationToken)
        {
            SavedReport = report;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AiReport>> GetLatestReportsAsync(
            int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AiReport>>([]);

        public Task UpsertAsync(IReadOnlyCollection<NewsArticle> articles, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<NewsArticle>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<NewsArticle>>([]);

        public Task<IReadOnlyList<ContentSearchResult>> SearchContentAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContentSearchResult>>([]);

        public Task<IReadOnlyList<NewsArticle>> GetLatestAsync(
            int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<NewsArticle>>([]);

        public Task UpsertTrendsAsync(IReadOnlyCollection<TrendItem> trends, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<TrendItem>> GetLatestTrendsAsync(
            int limit,
            string? platform,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TrendItem>>([]);
    }

    private sealed class StubDesktopFileDialogService : IDesktopFileDialogService
    {
        public string? OpenedUri { get; private set; }
        public IReadOnlyList<string> PickMediaFiles() => [];
        public string? PickWhisperModel() => null;
        public string? PickDatabaseBackup() => null;
        public string? PickFileForHash() => null;
        public (string Source, string Destination)? PickWordConversion() => null;
        public void OpenFolder(string path) { }
        public void OpenUri(string uri) => OpenedUri = uri;
    }
}
