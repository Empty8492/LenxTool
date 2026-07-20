using LenxTool.App.ViewModels;
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
            reports);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.GenerateArticleReportCommand.ExecuteAsync();

        Assert.Equal(generated, reports.SavedReport);
        Assert.Same(generated, Assert.Single(viewModel.Reports));
        Assert.Same(generated, viewModel.SelectedReport);
        Assert.Equal("报告已生成 · 128 tokens", viewModel.ReportStatus);
    }

    private static NewsCenterViewModel CreateViewModel(NewsCenterSnapshot snapshot) =>
        new(new StubNewsCenterService(snapshot), new StubAiReportService(null), new StubNewsRepository());

    private static NewsCenterSnapshot CreateSnapshot(params NewsArticle[] articles) =>
        new(articles, [], true, DateTimeOffset.Now, null);

    private static NewsArticle CreateArticle(string id, DateOnly date) =>
        new(id, date, "AI 早报", $"标题 {id}", $"摘要 {id}", $"正文 {id}", string.Empty, id, DateTimeOffset.Now);

    private sealed class StubNewsCenterService(NewsCenterSnapshot snapshot) : INewsCenterService
    {
        public Task<NewsCenterSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);

        public Task<NewsCenterSnapshot> LoadCachedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }

    private sealed class StubAiReportService(AiReport? report) : IAiReportService
    {
        public Task<AiReport> GenerateArticleInsightAsync(
            NewsArticle article,
            CancellationToken cancellationToken) => Task.FromResult(
                report ?? throw new InvalidOperationException("本测试不应生成报告。"));

        public Task<AiReport> GenerateDailyTrendReportAsync(
            IReadOnlyList<TrendItem> trends,
            CancellationToken cancellationToken) => Task.FromResult(
                report ?? throw new InvalidOperationException("本测试不应生成报告。"));
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
}
