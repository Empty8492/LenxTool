using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.App.ViewModels;

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

    private sealed class StubFeedEntryRepository(FeedEntry entry) : IFeedEntryRepository
    {
        public FeedEntryQuery? LastQuery { get; private set; }

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

        public Task UpsertReportAsync(
            AiReport report,
            CancellationToken cancellationToken) => Task.CompletedTask;

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
