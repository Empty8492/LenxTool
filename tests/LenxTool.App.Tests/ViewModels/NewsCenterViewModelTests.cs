using System.Diagnostics;
using System.Globalization;
using LenxTool.App.ViewModels;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class NewsCenterViewModelTests
{
    private const string CategoryId = "10000000-0000-4000-8000-000000000001";
    private const string FeedId = "30000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset TimelineNow = new(
        2026,
        7,
        23,
        9,
        30,
        0,
        TimeSpan.Zero);

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
            new StubDesktopFileDialogService(),
            new StubFeedEntryRepository([]),
            new StubFeedCatalogRepository(CreateCatalog()),
            new StubFeedCatalogSyncService());
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
            new StubDesktopFileDialogService(),
            new StubFeedEntryRepository([]),
            new StubFeedCatalogRepository(CreateCatalog()),
            new StubFeedCatalogSyncService());
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

    [Fact]
    public async Task InitializeAsyncLoadsFirstFeedTimelinePageAndSelectsNativeReader()
    {
        var entries = new StubFeedEntryRepository(
            Enumerable.Range(0, 75)
                .Select(index => CreateFeedEntry(index))
                .ToArray());
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(50, viewModel.TimelineEntries.Count);
        Assert.True(viewModel.HasMoreTimelineEntries);
        Assert.Equal(2, viewModel.TimelineCategories.Count);
        Assert.Equal(2, viewModel.TimelineFeeds.Count);
        FeedTimelineItem selected = Assert.IsType<FeedTimelineItem>(viewModel.SelectedTimelineEntry);
        Assert.Equal("Daily Feed", selected.FeedName);
        Assert.Equal("Technology", selected.CategoryName);
        Assert.Equal(selected.Entry.Title, viewModel.SelectedFeedArticle?.Title);
        Assert.Equal(selected.Entry.SanitizedContent, viewModel.SelectedFeedArticle?.RichContent);
        Assert.Equal("Daily Feed", viewModel.SelectedFeedArticle?.Source);
        FeedEntryQuery query = Assert.Single(entries.Queries);
        Assert.Equal(0, query.Offset);
        Assert.Equal(50, query.Limit);
    }

    [Fact]
    public async Task TimelineFiltersReloadFromFirstPageWithCategoryFeedDateAndKeyword()
    {
        var entries = new StubFeedEntryRepository([CreateFeedEntry(0)]);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedTimelineCategory = Assert.Single(
            viewModel.TimelineCategories,
            option => option.Id == CategoryId);
        viewModel.SelectedTimelineFeed = Assert.Single(
            viewModel.TimelineFeeds,
            option => option.Id == FeedId);
        viewModel.SelectedTimelineDate = new DateTime(2026, 7, 22);
        viewModel.TimelineKeyword = "  local model  ";

        await viewModel.ApplyTimelineFiltersCommand.ExecuteAsync();

        FeedEntryQuery query = entries.Queries[^1];
        Assert.Equal(CategoryId, query.CategoryId);
        Assert.Equal(FeedId, query.FeedId);
        Assert.Equal("local model", query.SearchText);
        Assert.Equal(new DateOnly(2026, 7, 22), DateOnly.FromDateTime(query.PublishedFrom!.Value.LocalDateTime));
        Assert.Equal(new DateOnly(2026, 7, 23), DateOnly.FromDateTime(query.PublishedBefore!.Value.LocalDateTime));
        Assert.Equal(0, query.Offset);
    }

    [Fact]
    public async Task LoadMoreTimelineCommandAppendsStablePagesWithoutDuplicates()
    {
        var entries = new StubFeedEntryRepository(
            Enumerable.Range(0, 75)
                .Select(index => CreateFeedEntry(index))
                .ToArray());
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.LoadMoreTimelineCommand.ExecuteAsync();

        Assert.Equal(75, viewModel.TimelineEntries.Count);
        Assert.Equal(75, viewModel.TimelineEntries.Select(item => item.Entry.Id).Distinct().Count());
        Assert.False(viewModel.HasMoreTimelineEntries);
        Assert.Equal([0, 50], entries.Queries.Select(query => query.Offset).ToArray());
    }

    [Fact]
    public async Task TimelineStatusShowsOfflineCacheAndLastRefreshAndSyncTimes()
    {
        var sync = new StubFeedCatalogSyncService(new(
            false,
            7,
            FeedCatalogScope.Active,
            TimelineNow.AddMinutes(-20),
            true,
            2,
            TimelineNow.AddMinutes(5),
            new(
                AppErrorCode.NetworkUnavailable,
                "网络不可用",
                "目录同步失败。",
                "稍后自动重试。")));
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([CreateFeedEntry(0)]),
            catalogSync: sync);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Contains("离线缓存", viewModel.TimelineStatus);
        Assert.Contains("最后抓取", viewModel.TimelineStatus);
        Assert.Contains("目录同步", viewModel.TimelineStatus);
    }

    [Fact]
    public async Task CatalogVersionChangeRefreshesTimelineFilterChoices()
    {
        var catalog = new StubFeedCatalogRepository(CreateCatalog());
        var sync = new StubFeedCatalogSyncService();
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([CreateFeedEntry(0)]),
            catalogSync: sync,
            catalogRepository: catalog);
        await viewModel.InitializeAsync(CancellationToken.None);
        FeedCatalogSnapshot updated = CreateCatalog() with
        {
            State = CreateCatalog().State with { Version = 8 },
            Feeds =
            [
                CreateCatalog().Feeds[0] with
                {
                    DisplayName = "Renamed Feed",
                    Version = 8
                }
            ]
        };
        catalog.Catalog = updated;
        var refreshed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.TimelineFeeds.CollectionChanged += (_, _) =>
        {
            if (viewModel.TimelineFeeds.Any(option => option.Label == "Renamed Feed"))
            {
                refreshed.TrySetResult();
            }
        };

        sync.Publish(sync.Current with { Version = 8 });
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, catalog.GetCatalogCallCount);
        Assert.Contains(viewModel.TimelineFeeds, option => option.Label == "Renamed Feed");
        Assert.Equal("Renamed Feed", viewModel.SelectedTimelineEntry?.FeedName);
    }

    [Fact]
    public async Task TenThousandCachedEntriesOnlyMaterializeTheFirstPage()
    {
        var entries = new StubFeedEntryRepository(
            Enumerable.Range(0, 10_000)
                .Select(index => CreateFeedEntry(index))
                .ToArray());
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries);
        var stopwatch = Stopwatch.StartNew();

        await viewModel.InitializeAsync(CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(50, viewModel.TimelineEntries.Count);
        Assert.True(viewModel.HasMoreTimelineEntries);
        Assert.Single(entries.Queries);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"首屏加载耗时 {stopwatch.Elapsed.TotalMilliseconds:F0} ms。");
    }

    private static NewsCenterViewModel CreateViewModel(
        NewsCenterSnapshot snapshot,
        StubDesktopFileDialogService? dialogs = null,
        StubFeedEntryRepository? feedEntries = null,
        StubFeedCatalogSyncService? catalogSync = null,
        StubFeedCatalogRepository? catalogRepository = null) =>
        new(
            new StubNewsCenterService(snapshot),
            new StubAiReportService(null),
            new StubNewsRepository(),
            dialogs ?? new StubDesktopFileDialogService(),
            feedEntries ?? new StubFeedEntryRepository([]),
            catalogRepository ?? new StubFeedCatalogRepository(CreateCatalog()),
            catalogSync ?? new StubFeedCatalogSyncService());

    private static NewsCenterSnapshot CreateSnapshot(params NewsArticle[] articles) =>
        new(articles, [], true, DateTimeOffset.Now, null);

    private static NewsArticle CreateArticle(string id, DateOnly date) =>
        new(id, date, "AI 早报", $"标题 {id}", $"摘要 {id}", $"正文 {id}", string.Empty, id, DateTimeOffset.Now);

    private static TrendItem CreateTrend(string id, string platform, int rank) =>
        new(id, platform, rank, $"标题 {id}", $"热度 {rank}", $"https://example.com/{id}", id, DateTimeOffset.Now);

    private static FeedCatalogSnapshot CreateCatalog() => new(
        new(7, FeedCatalogScope.Active, TimelineNow.AddHours(-1), TimelineNow.AddMinutes(-20)),
        [
            new(
                CategoryId,
                "Technology",
                "technology",
                1,
                true,
                7,
                TimelineNow.AddDays(-2),
                TimelineNow.AddDays(-1))
        ],
        [
            new(
                FeedId,
                "https://feeds.example/daily.xml",
                "https://feeds.example/daily.xml",
                "Daily Feed",
                "https://feeds.example/",
                CategoryId,
                FeedViewKind.Article,
                60,
                1,
                true,
                7,
                TimelineNow.AddDays(-2),
                TimelineNow.AddDays(-1))
        ]);

    private static FeedEntry CreateFeedEntry(int index) => new(
        $"entry-{index:D5}",
        FeedId,
        $"external-{index:D5}",
        $"https://feeds.example/articles/{index}",
        $"Timeline title {index}",
        "Author",
        TimelineNow.AddMinutes(-index),
        null,
        $"Summary {index} local model",
        $"Content {index}",
        [],
        [],
        index.ToString("x64", CultureInfo.InvariantCulture),
        TimelineNow);

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

    private sealed class StubFeedEntryRepository(IReadOnlyList<FeedEntry> entries) : IFeedEntryRepository
    {
        public List<FeedEntryQuery> Queries { get; } = [];

        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            IEnumerable<FeedEntry> filtered = entries;
            if (!string.IsNullOrWhiteSpace(query.FeedId))
                filtered = filtered.Where(entry => entry.FeedId == query.FeedId);
            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                filtered = filtered.Where(entry =>
                    entry.Title.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)
                    || entry.Summary.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)
                    || entry.SanitizedContent.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase));
            }
            if (query.PublishedFrom is not null)
                filtered = filtered.Where(entry => EntryTime(entry) >= query.PublishedFrom);
            if (query.PublishedBefore is not null)
                filtered = filtered.Where(entry => EntryTime(entry) < query.PublishedBefore);

            FeedEntry[] ordered = filtered
                .OrderByDescending(EntryTime)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToArray();
            FeedEntry[] page = ordered.Skip(query.Offset).Take(query.Limit).ToArray();
            return Task.FromResult(new FeedEntryPage(
                page,
                query.Offset,
                ordered.Length > query.Offset + page.Length));
        }

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> values,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) => Task.FromResult(0);

        private static DateTimeOffset EntryTime(FeedEntry entry) =>
            entry.PublishedAt ?? entry.UpdatedAt ?? entry.FetchedAt;
    }

    private sealed class StubFeedCatalogRepository(FeedCatalogSnapshot catalog) : IFeedCatalogRepository
    {
        public FeedCatalogSnapshot Catalog { get; set; } = catalog;
        public int GetCatalogCallCount { get; private set; }

        public Task ReplaceAsync(
            FeedCatalogSnapshot snapshot,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken)
        {
            GetCatalogCallCount++;
            return Task.FromResult<FeedCatalogSnapshot?>(Catalog);
        }

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeedCatalogState> GetStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Catalog.State);
    }

    private sealed class StubFeedCatalogSyncService : IFeedCatalogSyncService
    {
        public StubFeedCatalogSyncService(FeedCatalogSyncStatus? status = null)
        {
            Current = status ?? new(
                false,
                7,
                FeedCatalogScope.Active,
                TimelineNow.AddMinutes(-20),
                false,
                0,
                null,
                null);
        }

        public FeedCatalogSyncStatus Current { get; private set; }
        public event EventHandler<FeedCatalogSyncStatusChangedEventArgs>? StatusChanged;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeedCatalogSyncResult> SyncAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FeedCatalogSyncResult(
                FeedCatalogSyncOutcome.Unchanged,
                Current.Version,
                Current.LastSynchronizedAt));

        public void Publish(FeedCatalogSyncStatus status)
        {
            Current = status;
            StatusChanged?.Invoke(this, new(status));
        }
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
