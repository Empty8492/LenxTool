using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class DashboardViewModelTests
{
    [Fact]
    public async Task InitializeLoadsRealFeedTrendsTasksAndFavoriteCount()
    {
        DateTimeOffset now = new(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        FeedEntry feedEntry = new(
            "feed-entry",
            "feed-id",
            "external",
            "https://daily.juya.uk/article/1",
            "Feed title",
            "Author",
            now.AddMinutes(-5),
            null,
            "Feed summary",
            "Feed content",
            [],
            [],
            "feed-hash",
            now);
        NewsArticle legacy = new(
            "legacy",
            DateOnly.FromDateTime(now.LocalDateTime),
            "AI 早报",
            "Legacy title",
            "Legacy summary",
            "Legacy content",
            "https://daily.juya.uk/legacy/1",
            "legacy-hash",
            now.AddMinutes(-10));
        TrendItem trend = new(
            "trend",
            "GitHub",
            1,
            "Trend title",
            "4.2k",
            "https://github.com/example",
            "trend-hash",
            now.AddMinutes(-2));
        MediaJob job = new(
            "job",
            "Transcription",
            "meeting.wav",
            null,
            MediaJobStatus.Running,
            .5,
            TranscriptionEngine.LocalWhisper,
            "ggml-small",
            0,
            0,
            null,
            now.AddMinutes(-20),
            now.AddMinutes(-1));

        var feedRepository = new StubFeedEntryRepository(feedEntry);
        var newsRepository = new StubNewsRepository([legacy], [trend]);
        var jobsRepository = new StubMediaJobRepository(job);
        var favorites = new StubFavoriteRepository(7);
        var viewModel = new DashboardViewModel(
            feedRepository,
            new StubFeedCatalogRepository(),
            newsRepository,
            jobsRepository,
            favorites);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(["Feed title", "Legacy title"], viewModel.News.Select(item => item.Title));
        Assert.Equal("Legacy title", Assert.Single(viewModel.LegacyNews).Title);
        Assert.Equal("Trend title", Assert.Single(viewModel.Trends).Title);
        Assert.Equal("meeting.wav", Assert.Single(viewModel.RecentTasks).Name);
        Assert.Equal(7, viewModel.FavoriteCount);
        Assert.True(feedRepository.LastQuery?.ActiveOnly);
        Assert.Equal("https://daily.juya.uk/rss.xml", DashboardViewModel.CompatibilityFeedUrl);
        Assert.Equal(
            FeedCompatibilitySeed.Url,
            FeedCompatibilitySeed.CreateInput().OriginalUrl);
        Assert.Contains("本地", viewModel.DataStatus);
    }

    [Fact]
    public async Task InitializeDeduplicatesLegacyArticleWhenFeedEntryHasSameIdentity()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FeedEntry feedEntry = new(
            "feed-entry",
            "feed-id",
            "external",
            "https://daily.juya.uk/article/1",
            "Feed title",
            null,
            now,
            null,
            "summary",
            "content",
            [],
            [],
            "same-hash",
            now);
        NewsArticle duplicate = new(
            "legacy",
            DateOnly.FromDateTime(now.LocalDateTime),
            "AI 早报",
            "Legacy title",
            "summary",
            "content",
            "https://daily.juya.uk/article/1",
            "same-hash",
            now);
        var viewModel = new DashboardViewModel(
            new StubFeedEntryRepository(feedEntry),
            new StubFeedCatalogRepository(),
            new StubNewsRepository([duplicate], []),
            new StubMediaJobRepository(),
            new StubFavoriteRepository(0));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Single(viewModel.News);
        Assert.Empty(viewModel.LegacyNews);
    }

    [Fact]
    public async Task InitializeBuildsHomepageBriefingFromLatestDailyOverview()
    {
        DateTimeOffset now = new(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);
        FeedEntry feedEntry = new(
            "feed-entry",
            "feed-id",
            "external",
            "https://daily.juya.uk/article/1",
            "Feed title",
            null,
            now,
            null,
            "summary",
            "content",
            [],
            [],
            "feed-hash",
            now);
        NewsArticle briefing = new(
            "briefing",
            new(2026, 7, 24),
            "AI 早报",
            "2026-07-24",
            "summary",
            "content",
            "https://daily.juya.uk/issues/2026-07-24/",
            "briefing-hash",
            now)
        {
            RichContent = """
                <h1>AI 早报 2026-07-24</h1>
                <h2>概览</h2>
                <h3>要闻</h3>
                <ul>
                  <li>ChatGPT Voice 支持语音控制电脑</li>
                  <li>Claude 更新语音模式</li>
                  <li>这一条超过首页每栏展示上限</li>
                </ul>
                <h3>模型发布</h3>
                <ul><li>FLUX 3 发布多模态模型</li></ul>
                <h2>详情</h2>
                <h3>不应进入首页概览</h3>
                <ul><li>正文内容</li></ul>
                """
        };
        var viewModel = new DashboardViewModel(
            new StubFeedEntryRepository(feedEntry),
            new StubFeedCatalogRepository(),
            new StubNewsRepository([briefing], []),
            new StubMediaJobRepository(),
            new StubFavoriteRepository(0));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.NotEmpty(viewModel.BriefingSections);
        Assert.Equal("2026-07-24", viewModel.BriefingTitle);
        Assert.Equal("AI 早报 · 07-24", viewModel.BriefingMeta);
        Assert.Collection(
            viewModel.BriefingSections,
            section =>
            {
                Assert.Equal("要闻", section.Title);
                Assert.Equal(
                    [
                        "ChatGPT Voice 支持语音控制电脑",
                        "Claude 更新语音模式",
                        "这一条超过首页每栏展示上限"
                    ],
                    section.Items);
            },
            section =>
            {
                Assert.Equal("模型发布", section.Title);
                Assert.Equal(["FLUX 3 发布多模态模型"], section.Items);
            });
    }

    [Fact]
    public async Task InitializeShowsEmptyBriefingStateWhenNoDailyCacheExists()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FeedEntry feedEntry = new(
            "feed-entry",
            "feed-id",
            "external",
            "https://example.com/article",
            "Feed title",
            null,
            now,
            null,
            "summary",
            "content",
            [],
            [],
            "feed-hash",
            now);
        var viewModel = new DashboardViewModel(
            new StubFeedEntryRepository(feedEntry),
            new StubFeedCatalogRepository(),
            new StubNewsRepository([], []),
            new StubMediaJobRepository(),
            new StubFavoriteRepository(0));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Empty(viewModel.BriefingSections);
        Assert.Equal("暂无每日早报", viewModel.BriefingTitle);
        Assert.Contains("刷新", viewModel.BriefingEmptyText);
    }

    private sealed class StubFeedEntryRepository(FeedEntry entry) : IFeedEntryRepository
    {
        public FeedEntryQuery? LastQuery { get; private set; }

        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedEntry?>(entry.Id == entryId ? entry : null);

        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(new FeedEntryPage([entry], query.Offset, false));
        }

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class StubNewsRepository(
        IReadOnlyList<NewsArticle> articles,
        IReadOnlyList<TrendItem> trends) : INewsRepository
    {
        public Task UpsertAsync(
            IReadOnlyCollection<NewsArticle> values,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<NewsArticle>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(articles);

        public Task<IReadOnlyList<ContentSearchResult>> SearchContentAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContentSearchResult>>([]);

        public Task<ContentSearchPage> SearchContentAsync(
            ContentSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ContentSearchPage([], false));

        public Task UpsertReportAsync(
            AiReport report,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AiReport?> GetReportByIdAsync(
            string reportId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AiReport?>(null);

        public Task<IReadOnlyList<AiReport>> GetLatestReportsAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiReport>>([]);

        public Task<IReadOnlyList<NewsArticle>> GetLatestAsync(
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(articles);

        public Task UpsertTrendsAsync(
            IReadOnlyCollection<TrendItem> values,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<TrendItem>> GetLatestTrendsAsync(
            int limit,
            string? platform,
            CancellationToken cancellationToken) => Task.FromResult(trends);
    }

    private sealed class StubMediaJobRepository : IMediaJobRepository
    {
        private readonly MediaJob[] _jobs;

        public StubMediaJobRepository(params MediaJob[] jobs)
        {
            _jobs = jobs;
        }

        public Task UpsertAsync(MediaJob job, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<MediaJob>> GetRecentAsync(
            int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MediaJob>>(_jobs);

        public Task<IReadOnlyList<MediaJob>> GetQueuedAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MediaJob>>([]);

        public Task<IReadOnlyList<MediaJob>> RecoverInterruptedAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MediaJob>>(_jobs);
    }

    private sealed class StubFavoriteRepository(int count) : IFavoriteRepository
    {
        public Task<int> GetCountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(count);

        public Task<FavoriteItem?> GetAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult<FavoriteItem?>(null);

        public Task<FavoriteItem> UpsertAsync(
            string entityType,
            string entityId,
            string note,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<IReadOnlyDictionary<string, FavoriteItem>> GetForEntitiesAsync(
            string entityType,
            IReadOnlyCollection<string> entityIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, FavoriteItem>>(
                new Dictionary<string, FavoriteItem>(StringComparer.Ordinal));

        public Task<TagItem> UpsertTagAsync(
            string name,
            string color,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TagItem> AddTagAsync(
            string entityType,
            string entityId,
            string name,
            string color,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TagItem>> GetTagsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagItem>>([]);

        public Task<IReadOnlyList<TagItem>> GetTagsForEntityAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagItem>>([]);

        public Task SetTagsAsync(
            string entityType,
            string entityId,
            IReadOnlyCollection<string> tagIds,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> DeleteTagAsync(
            string tagId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class StubFeedCatalogRepository : IFeedCatalogRepository
    {
        public Task ReplaceAsync(
            FeedCatalogSnapshot snapshot,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedCatalogSnapshot?>(null);

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeedCatalogState> GetStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FeedCatalogState(0, FeedCatalogScope.Active, null, null));
    }
}
