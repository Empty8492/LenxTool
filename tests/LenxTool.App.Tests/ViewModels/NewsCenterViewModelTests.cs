using System.Diagnostics;
using System.Globalization;
using LenxTool.App.Controls;
using LenxTool.App.ViewModels;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class NewsCenterViewModelTests
{
    [Fact]
    public async Task PictureFeedLoadsOnlyAfterSelectingItsTab()
    {
        var entries = new StubFeedEntryRepository([]);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Null(viewModel.PictureFeed);
        Assert.DoesNotContain(entries.Queries, query => query.ViewKind == EntryViewKind.Picture);

        viewModel.SelectedFeedViewIndex = 1;
        await viewModel.PictureFeedInitialization.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.NotNull(viewModel.PictureFeed);
        Assert.Contains(entries.Queries, query => query.ViewKind == EntryViewKind.Picture);
    }

    [Fact]
    public async Task AudioFeedLoadsOnlyAfterSelectingItsTab()
    {
        var entries = new StubFeedEntryRepository([]);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries,
            audioPlayback: new StubFeedAudioPlaybackService(),
            mediaDelivery: new StubFeedMediaDeliveryService(),
            navigation: new StubAppNavigationService());

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Null(viewModel.AudioFeed);
        Assert.DoesNotContain(
            entries.Queries,
            query => query.ViewKind == EntryViewKind.Audio);

        viewModel.SelectedFeedViewIndex = 2;
        await viewModel.AudioFeedInitialization.WaitAsync(
            TimeSpan.FromSeconds(1));

        Assert.NotNull(viewModel.AudioFeed);
        Assert.Contains(
            entries.Queries,
            query => query.ViewKind == EntryViewKind.Audio);
    }

    [Fact]
    public async Task VideoFeedLoadsOnlyAfterSelectingItsTab()
    {
        var entries = new StubFeedEntryRepository([]);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries,
            mediaDelivery: new StubFeedMediaDeliveryService(),
            navigation: new StubAppNavigationService(),
            videoPlanner: new StubFeedVideoDeliveryPlanningService());

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Null(viewModel.VideoFeed);
        Assert.DoesNotContain(
            entries.Queries,
            query => query.ViewKind == EntryViewKind.Video);

        viewModel.SelectedFeedViewIndex = 3;
        await viewModel.VideoFeedInitialization.WaitAsync(
            TimeSpan.FromSeconds(1));

        Assert.NotNull(viewModel.VideoFeed);
        Assert.Contains(
            entries.Queries,
            query => query.ViewKind == EntryViewKind.Video);
    }

    [Fact]
    public async Task NotificationFeedLoadsOnlyAfterSelectingItsTab()
    {
        var entries = new StubFeedEntryRepository([]);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Null(viewModel.NotificationFeed);
        Assert.DoesNotContain(
            entries.Queries,
            query => query.ViewKind == EntryViewKind.Notification);

        viewModel.SelectedFeedViewIndex = 4;
        await viewModel.NotificationFeedInitialization.WaitAsync(
            TimeSpan.FromSeconds(1));

        Assert.NotNull(viewModel.NotificationFeed);
        Assert.Contains(
            entries.Queries,
            query => query.ViewKind == EntryViewKind.Notification);
    }

    [Fact]
    public async Task NotificationFeedKeepsItsFiltersWhenSwitchingAwayAndBack()
    {
        var entries = new StubFeedEntryRepository([]);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedFeedViewIndex = 4;
        await viewModel.NotificationFeedInitialization.WaitAsync(
            TimeSpan.FromSeconds(1));
        FeedContentCollectionViewModel notificationFeed =
            Assert.IsType<FeedContentCollectionViewModel>(
                viewModel.NotificationFeed);
        var selectedDate = new DateTime(2026, 7, 20);
        notificationFeed.SelectedDate = selectedDate;
        notificationFeed.FavoritesOnly = true;
        int notificationQueryCount = entries.Queries.Count(
            query => query.ViewKind == EntryViewKind.Notification);

        viewModel.SelectedFeedViewIndex = 0;
        viewModel.SelectedFeedViewIndex = 4;
        await viewModel.NotificationFeedInitialization.WaitAsync(
            TimeSpan.FromSeconds(1));

        Assert.Same(notificationFeed, viewModel.NotificationFeed);
        Assert.Equal(selectedDate, notificationFeed.SelectedDate);
        Assert.True(notificationFeed.FavoritesOnly);
        Assert.Equal(
            notificationQueryCount,
            entries.Queries.Count(
                query => query.ViewKind
                         == EntryViewKind.Notification));
    }

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
    private const string SmartViewId =
        "40000000-0000-4000-8000-000000000001";

    [Fact]
    public async Task PublishedSmartViewLoadsFromLocalCacheWithoutApplyingIt()
    {
        var entries = new StubFeedEntryRepository([CreateFeedEntry(0)]);
        var smartViews = new StubFeedSmartViewRepository(
            CreateSmartViewSnapshot());
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries,
            smartViews: smartViews,
            timeProvider: new FixedTimeProvider(TimelineNow));

        await viewModel.InitializeAsync(CancellationToken.None);

        FeedSmartView selected = Assert.Single(
            viewModel.TimelineSmartViews);
        Assert.Same(selected, viewModel.SelectedTimelineSmartView);
        Assert.False(viewModel.IsTimelineSmartViewApplied);
        Assert.Equal(1, smartViews.GetCount);
        Assert.Null(entries.Queries[^1].ViewKind);
        Assert.Contains("v9", viewModel.TimelineSmartViewStatus);
    }

    [Fact]
    public async Task ApplyingPublishedSmartViewUsesPrivateFiltersOnlyInLocalQuery()
    {
        var entries = new StubFeedEntryRepository([CreateFeedEntry(0)]);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries,
            smartViews: new StubFeedSmartViewRepository(
                CreateSmartViewSnapshot()),
            timeProvider: new FixedTimeProvider(TimelineNow));
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.ApplyTimelineSmartViewCommand.ExecuteAsync();

        FeedEntryQuery query = entries.Queries[^1];
        Assert.True(viewModel.IsTimelineSmartViewApplied);
        Assert.Equal(FeedId, query.FeedId);
        Assert.Equal(CategoryId, query.CategoryId);
        Assert.Equal(EntryViewKind.Video, query.ViewKind);
        Assert.Equal(FeedEntryReadFilter.Unread, query.ReadFilter);
        Assert.True(query.FavoritesOnly);
        Assert.Equal("release", query.SearchText);
        Assert.Equal(TimelineNow.AddDays(-30), query.PublishedFrom);
        Assert.Null(query.PublishedBefore);
        Assert.Equal("default", query.LocalProfile);

        viewModel.TimelineKeyword = "temporary";
        await viewModel.ApplyTimelineFiltersCommand.ExecuteAsync();

        FeedEntryQuery temporary = entries.Queries[^1];
        Assert.False(viewModel.IsTimelineSmartViewApplied);
        Assert.Null(temporary.ViewKind);
        Assert.False(temporary.FavoritesOnly);
        Assert.Equal("temporary", temporary.SearchText);
    }

    [Fact]
    public async Task ApplyingViewReloadsLatestBackgroundSnapshotBeforeQuery()
    {
        var entries = new StubFeedEntryRepository([CreateFeedEntry(0)]);
        var repository = new StubFeedSmartViewRepository(
            CreateSmartViewSnapshot());
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries,
            smartViews: repository,
            timeProvider: new FixedTimeProvider(TimelineNow));
        await viewModel.InitializeAsync(CancellationToken.None);
        FeedSmartView updated = CreateSmartViewSnapshot().Views[0] with
        {
            Version = 4,
            Name = "最近七天",
            Filter = CreateSmartViewSnapshot().Views[0].Filter with
            {
                PublishedWithinDays = 7
            }
        };
        repository.Snapshot = CreateSmartViewSnapshot() with
        {
            ViewSetVersion = 10,
            Views = [updated]
        };

        await viewModel.ApplyTimelineSmartViewCommand.ExecuteAsync();

        Assert.True(viewModel.IsTimelineSmartViewApplied);
        Assert.Equal(
            "最近七天",
            viewModel.SelectedTimelineSmartView?.Name);
        Assert.Equal(
            TimelineNow.AddDays(-7),
            entries.Queries[^1].PublishedFrom);
        Assert.Equal(2, repository.GetCount);
    }

    [Fact]
    public async Task RefreshSynchronizesAndReappliesUpdatedPublishedView()
    {
        var entries = new StubFeedEntryRepository([CreateFeedEntry(0)]);
        var repository = new StubFeedSmartViewRepository(
            CreateSmartViewSnapshot());
        var sync = new StubFeedSmartViewSyncService(() =>
        {
            FeedSmartView updated =
                CreateSmartViewSnapshot().Views[0] with
                {
                    Version = 4,
                    Name = "最近七天",
                    Filter = CreateSmartViewSnapshot().Views[0].Filter with
                    {
                        PublishedWithinDays = 7
                    }
                };
            repository.Snapshot =
                CreateSmartViewSnapshot() with
                {
                    ViewSetVersion = 10,
                    Views = [updated]
                };
        });
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries,
            smartViews: repository,
            smartViewSync: sync,
            timeProvider: new FixedTimeProvider(TimelineNow));
        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.ApplyTimelineSmartViewCommand.ExecuteAsync();

        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal(1, sync.Count);
        Assert.True(viewModel.IsTimelineSmartViewApplied);
        Assert.Equal(
            "最近七天",
            viewModel.SelectedTimelineSmartView?.Name);
        Assert.Equal(
            TimelineNow.AddDays(-7),
            entries.Queries[^1].PublishedFrom);
    }

    [Fact]
    public async Task RefreshKeepsLastValidViewsWhenSynchronizationFails()
    {
        var repository = new StubFeedSmartViewRepository(
            CreateSmartViewSnapshot());
        var sync = new StubFeedSmartViewSyncService(
            static () => { })
        {
            ThrowOnSync = true
        };
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            smartViews: repository,
            smartViewSync: sync,
            timeProvider: new FixedTimeProvider(TimelineNow));
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Single(viewModel.TimelineSmartViews);
        Assert.Equal(1, sync.Count);
        Assert.Contains(
            "同步失败",
            viewModel.TimelineSmartViewStatus,
            StringComparison.Ordinal);
        Assert.Contains(
            "离线版本",
            viewModel.TimelineSmartViewStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovedPublishedViewCannotBeAppliedFromStaleMemory()
    {
        var entries = new StubFeedEntryRepository([CreateFeedEntry(0)]);
        var repository = new StubFeedSmartViewRepository(
            CreateSmartViewSnapshot());
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries,
            smartViews: repository,
            timeProvider: new FixedTimeProvider(TimelineNow));
        await viewModel.InitializeAsync(CancellationToken.None);
        int queryCount = entries.Queries.Count;
        repository.Snapshot = CreateSmartViewSnapshot() with
        {
            ViewSetVersion = 10,
            Views = []
        };

        await viewModel.ApplyTimelineSmartViewCommand.ExecuteAsync();

        Assert.False(viewModel.IsTimelineSmartViewApplied);
        Assert.Empty(viewModel.TimelineSmartViews);
        Assert.Equal(queryCount, entries.Queries.Count);
        Assert.Contains(
            "已被管理员移除",
            viewModel.TimelineSmartViewStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CacheReadFailureRefusesToApplyStaleMemoryDefinition()
    {
        var entries = new StubFeedEntryRepository([CreateFeedEntry(0)]);
        var repository = new StubFeedSmartViewRepository(
            CreateSmartViewSnapshot());
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries,
            smartViews: repository,
            timeProvider: new FixedTimeProvider(TimelineNow));
        await viewModel.InitializeAsync(CancellationToken.None);
        int queryCount = entries.Queries.Count;
        repository.ThrowOnGet = true;

        await viewModel.ApplyTimelineSmartViewCommand.ExecuteAsync();

        Assert.False(viewModel.IsTimelineSmartViewApplied);
        Assert.Equal(queryCount, entries.Queries.Count);
        Assert.Contains(
            "读取",
            viewModel.TimelineSmartViewStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TopLevelRoutesSelectSectionTitles()
    {
        using NewsCenterViewModel viewModel = CreateViewModel(CreateSnapshot());

        // 一级入口只负责选择栏目，滚轮手感由共享控件层统一验证。
        Assert.Equal("资讯列表", viewModel.ActiveSectionTitle);
        Assert.Equal(0, viewModel.SelectedSectionIndex);

        viewModel.OnNavigated("daily-briefing");

        Assert.Equal("每日早报", viewModel.ActiveSectionTitle);
        Assert.Equal(1, viewModel.SelectedSectionIndex);

        viewModel.OnNavigated("trends");
        Assert.Equal("热点趋势", viewModel.ActiveSectionTitle);
        Assert.Equal(2, viewModel.SelectedSectionIndex);

        viewModel.OnNavigated("ai-reports");
        Assert.Equal("AI 报告", viewModel.ActiveSectionTitle);
        Assert.Equal(3, viewModel.SelectedSectionIndex);
    }

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
            new StubFeedCatalogSyncService(),
            new StubEntryStateRepository(),
            new StubFavoriteRepository(),
            new StubFeedFullTextQueueService(),
            new StubFeedAiSummaryService(),
            new StubFeedAiTranslationService());
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
            new StubFeedCatalogSyncService(),
            new StubEntryStateRepository(),
            new StubFavoriteRepository(),
            new StubFeedFullTextQueueService(),
            new StubFeedAiSummaryService(),
            new StubFeedAiTranslationService());
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
        FeedEntryQuery query = Assert.Single(
            entries.Queries,
            candidate => candidate.ViewKind is null);
        Assert.Equal(0, query.Offset);
        Assert.Equal(50, query.Limit);
    }

    [Fact]
    public async Task TimelineReaderPrefersExtractedContentAndCanSwitchBackToRss()
    {
        FeedEntry entry = CreateFeedEntry(0) with
        {
            Enclosures =
            [
                new(
                    "https://cdn.example/audio.mp3",
                    "audio/mpeg",
                    128,
                    "Audio")
            ]
        };
        DateTimeOffset extractedAt = TimelineNow.AddMinutes(5);
        var fullText = new StubFeedFullTextQueueService();
        fullText.Contents[entry.Id] = new(
            entry.Id,
            new(
                entry.NormalizedUrl!,
                entry.NormalizedUrl!,
                entry.Title,
                entry.Author,
                entry.PublishedAt,
                [
                    new(
                        ArticleContentBlockKind.Heading,
                        "Extracted heading",
                        null,
                        1,
                        []),
                    new(
                        ArticleContentBlockKind.Paragraph,
                        "Extracted body",
                        null,
                        null,
                        [])
                ],
                [],
                "readability-v1"),
            "extracted-hash",
            extractedAt);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            fullText: fullText);

        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.SelectedFeedReaderLoad;

        Assert.Equal(["RSS 正文", "提取全文"], viewModel.FeedReaderSourceOptions.Select(x => x.Label));
        Assert.Equal(FeedReaderContentSource.Extracted, viewModel.SelectedFeedReaderSource.Source);
        Assert.Equal("提取全文", viewModel.FeedReaderSourceLabel);
        Assert.Equal(extractedAt, viewModel.FeedReaderExtractedAt);
        Assert.Contains(
            viewModel.SelectedFeedArticleDocument!.Blocks,
            block => block.Text == "Extracted body");
        Assert.Contains(
            viewModel.SelectedFeedArticleDocument.Blocks,
            block => block.Kind == RichArticleBlockKind.Bullet
                     && block.Text
                     == "音频 · Audio · 128 B · 外部来源，打开前请确认"
                     && block.Inlines.Count == 1
                     && block.Inlines[0].Url
                     == "https://cdn.example/audio.mp3");

        viewModel.SelectedFeedReaderSource = viewModel.FeedReaderSourceOptions[0];

        Assert.Equal(FeedReaderContentSource.Rss, viewModel.SelectedFeedReaderSource.Source);
        Assert.Equal("RSS 正文", viewModel.FeedReaderSourceLabel);
        Assert.Null(viewModel.FeedReaderExtractedAt);
        Assert.Contains(
            viewModel.SelectedFeedArticleDocument!.Blocks,
            block => block.Kind == RichArticleBlockKind.Bullet
                     && block.Text
                     == "音频 · Audio · 128 B · 外部来源，打开前请确认"
                     && block.Inlines.Count == 1
                     && block.Inlines[0].Url
                     == "https://cdn.example/audio.mp3");
        Assert.Equal(entry.SanitizedContent, viewModel.SelectedFeedArticle?.RichContent);
    }

    [Fact]
    public async Task TimelineReaderCancelsPreviousFullTextLoadWhenSelectionChanges()
    {
        FeedEntry first = CreateFeedEntry(0);
        FeedEntry second = CreateFeedEntry(1);
        var fullText = new StubFeedFullTextQueueService
        {
            DelayedEntryId = first.Id
        };
        fullText.Contents[second.Id] = CreateFullTextContent(second, "Second extracted");
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([first, second]),
            fullText: fullText);
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedTimelineEntry = Assert.Single(
            viewModel.TimelineEntries,
            item => item.Entry.Id == second.Id);
        await viewModel.SelectedFeedReaderLoad;
        await fullText.DelayedRequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(second.Id, viewModel.SelectedFeedArticle?.Id);
        Assert.Contains(
            viewModel.SelectedFeedArticleDocument!.Blocks,
            block => block.Text == "Second extracted");
    }

    [Fact]
    public async Task GenerateSelectedFeedSummaryUsesCurrentExtractedContent()
    {
        FeedEntry entry = CreateFeedEntry(0);
        string extractedHash = new('e', 64);
        var fullText = new StubFeedFullTextQueueService();
        fullText.Contents[entry.Id] = CreateFullTextContent(entry, "提取后的完整正文") with
        {
            ContentHash = extractedHash
        };
        var summaries = new StubFeedAiSummaryService();
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            fullText: fullText,
            summaries: summaries);
        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.SelectedFeedReaderLoad;

        await viewModel.GenerateFeedSummaryCommand.ExecuteAsync();

        FeedAiSummaryInput input = Assert.Single(summaries.SingleInputs);
        Assert.Equal(entry.Id, input.EntryId);
        Assert.Equal(extractedHash, input.ContentHash);
        Assert.Contains("提取后的完整正文", input.Content, StringComparison.Ordinal);
        Assert.Equal("测试摘要", viewModel.SelectedFeedSummary?.Content);
        Assert.Contains("15 tokens", viewModel.FeedSummaryMeta, StringComparison.Ordinal);
        Assert.Contains("已生成", viewModel.FeedSummaryStatus, StringComparison.Ordinal);

        viewModel.SelectedFeedReaderSource = viewModel.FeedReaderSourceOptions[0];

        Assert.Null(viewModel.SelectedFeedSummary);
        Assert.Contains("RSS 正文", viewModel.FeedSummaryStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledManualSummaryPolicyBlocksSelectedAndBatchGeneration()
    {
        FeedCatalogSnapshot baseCatalog = CreateCatalog();
        FeedCatalogSnapshot policyCatalog = baseCatalog with
        {
            AiPolicyDefaults = FeedAiPolicy.SafeDefaults,
            Feeds =
            [
                baseCatalog.Feeds[0] with
                {
                    AiPolicy = FeedAiPolicy.Inherited with
                    {
                        ManualSummary = FeedAiPolicySwitch.Disabled
                    }
                }
            ]
        };
        var summaries = new StubFeedAiSummaryService();
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([CreateFeedEntry(0)]),
            catalogRepository: new StubFeedCatalogRepository(policyCatalog),
            summaries: summaries);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.GenerateFeedSummaryCommand.CanExecute(null));
        Assert.False(viewModel.GenerateVisibleFeedSummariesCommand.CanExecute(null));
        await viewModel.GenerateFeedSummaryCommand.ExecuteAsync();
        await viewModel.GenerateVisibleFeedSummariesCommand.ExecuteAsync();
        Assert.Empty(summaries.SingleInputs);
        Assert.Empty(summaries.BatchInputs);
        Assert.Contains("管理员", viewModel.FeedSummaryStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateVisibleFeedSummariesUsesBoundedFirstPageAndShowsSelectedResult()
    {
        var entries = new StubFeedEntryRepository(
            Enumerable.Range(0, 50).Select(CreateFeedEntry).ToArray());
        var summaries = new StubFeedAiSummaryService();
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries,
            summaries: summaries);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.GenerateVisibleFeedSummariesCommand.ExecuteAsync();

        Assert.Equal(20, Assert.Single(summaries.BatchInputs).Count);
        Assert.Equal("测试摘要", viewModel.SelectedFeedSummary?.Content);
        Assert.Contains("20/20", viewModel.FeedBatchSummaryStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingSelectionCancelsRunningFeedSummary()
    {
        FeedEntry first = CreateFeedEntry(0);
        FeedEntry second = CreateFeedEntry(1);
        var summaries = new StubFeedAiSummaryService
        {
            DelayedEntryId = first.Id
        };
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([first, second]),
            summaries: summaries);
        await viewModel.InitializeAsync(CancellationToken.None);

        Task generation = viewModel.GenerateFeedSummaryCommand.ExecuteAsync();
        await summaries.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedTimelineEntry = Assert.Single(
            viewModel.TimelineEntries,
            item => item.Entry.Id == second.Id);
        await generation;
        await summaries.RequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(second.Id, viewModel.SelectedTimelineEntry?.Entry.Id);
        Assert.Null(viewModel.SelectedFeedSummary);
    }

    [Fact]
    public async Task GenerateSelectedFeedTranslationUsesCurrentSourceAndSwitchesReadingModes()
    {
        FeedEntry entry = CreateFeedEntry(0);
        string extractedHash = new('e', 64);
        var fullText = new StubFeedFullTextQueueService();
        fullText.Contents[entry.Id] = CreateFullTextContent(entry, "Extracted body") with
        {
            ContentHash = extractedHash
        };
        var translations = new StubFeedAiTranslationService();
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            fullText: fullText,
            translations: translations);
        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.SelectedFeedReaderLoad;

        await viewModel.GenerateFeedTranslationCommand.ExecuteAsync();

        FeedAiTranslationInput input = Assert.Single(translations.Inputs);
        Assert.Equal((entry.Id, extractedHash, "简体中文"),
            (input.EntryId, input.ContentHash, input.TargetLanguage));
        Assert.Equal(FeedAiTranslationBlockKind.Title, input.Blocks[0].Kind);
        Assert.Contains(input.Blocks, block => block.Text == "Extracted body");
        Assert.Equal(
            ["原文", "译文", "双语"],
            viewModel.FeedReaderLanguageOptions.Select(option => option.Label));
        Assert.Equal(
            FeedReaderLanguageMode.Translation,
            viewModel.SelectedFeedReaderLanguage.Mode);
        Assert.Contains(
            viewModel.SelectedFeedArticleDocument!.Blocks,
            block => block.Text == "译：Extracted body");
        Assert.Contains("简体中文", viewModel.FeedTranslationMeta, StringComparison.Ordinal);

        viewModel.SelectedFeedReaderLanguage = Assert.Single(
            viewModel.FeedReaderLanguageOptions,
            option => option.Mode == FeedReaderLanguageMode.Bilingual);

        Assert.Contains(
            viewModel.SelectedFeedArticleDocument!.Blocks,
            block => block.Text == "Extracted body");
        Assert.Contains(
            viewModel.SelectedFeedArticleDocument.Blocks,
            block => block.Kind == RichArticleBlockKind.Translation
                && block.Text == "译：Extracted body");

        viewModel.SelectedFeedTranslationTargetLanguage = "English";

        Assert.Equal(
            FeedReaderLanguageMode.Original,
            viewModel.SelectedFeedReaderLanguage.Mode);
        Assert.Null(viewModel.SelectedFeedTranslation);
        Assert.Contains(
            viewModel.SelectedFeedArticleDocument!.Blocks,
            block => block.Text == "Extracted body");
        Assert.DoesNotContain(
            viewModel.SelectedFeedArticleDocument.Blocks,
            block => block.Text == "译：Extracted body");
    }

    [Fact]
    public async Task FeedTranslationFailureKeepsOriginalReadable()
    {
        FeedEntry entry = CreateFeedEntry(0);
        var translations = new StubFeedAiTranslationService
        {
            Error = new(
                AppErrorCode.ProviderUnavailable,
                "翻译暂不可用",
                "无法生成译文。",
                "请稍后重试。",
                Provider: "DeepSeek",
                IsRetryable: true)
        };
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            translations: translations);
        await viewModel.InitializeAsync(CancellationToken.None);
        string originalText = viewModel.SelectedFeedArticleDocument?.Blocks
            .FirstOrDefault(block => block.Kind != RichArticleBlockKind.Image)?.Text
            ?? viewModel.SelectedFeedArticle!.RichContent;

        await viewModel.GenerateFeedTranslationCommand.ExecuteAsync();

        Assert.Equal(
            FeedReaderLanguageMode.Original,
            viewModel.SelectedFeedReaderLanguage.Mode);
        Assert.Null(viewModel.SelectedFeedTranslation);
        Assert.Equal(AppErrorCode.ProviderUnavailable, viewModel.FeedTranslationError?.Code);
        Assert.Contains("无法生成译文", viewModel.FeedTranslationStatus, StringComparison.Ordinal);
        Assert.Contains(
            originalText,
            viewModel.SelectedFeedArticleDocument?.Blocks.Select(block => block.Text)
                ?? [viewModel.SelectedFeedArticle!.RichContent]);
    }

    [Fact]
    public async Task ChangingSelectionCancelsRunningFeedTranslation()
    {
        FeedEntry first = CreateFeedEntry(0);
        FeedEntry second = CreateFeedEntry(1);
        var translations = new StubFeedAiTranslationService
        {
            DelayedEntryId = first.Id
        };
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([first, second]),
            translations: translations);
        await viewModel.InitializeAsync(CancellationToken.None);

        Task generation = viewModel.GenerateFeedTranslationCommand.ExecuteAsync();
        await translations.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedTimelineEntry = Assert.Single(
            viewModel.TimelineEntries,
            item => item.Entry.Id == second.Id);
        await generation;
        await translations.RequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(second.Id, viewModel.SelectedTimelineEntry?.Entry.Id);
        Assert.Null(viewModel.SelectedFeedTranslation);
        Assert.Equal(
            FeedReaderLanguageMode.Original,
            viewModel.SelectedFeedReaderLanguage.Mode);
    }

    [Fact]
    public async Task OpenSelectedFeedOriginalOnlyAcceptsEntryHttpLinks()
    {
        FeedEntry safe = CreateFeedEntry(0);
        var dialogs = new StubDesktopFileDialogService();
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            dialogs,
            new([safe]));
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.OpenSelectedFeedOriginalCommand.CanExecute(null));
        viewModel.OpenSelectedFeedOriginalCommand.Execute(null);
        Assert.Equal(safe.NormalizedUrl, dialogs.OpenedUri);

        FeedEntry unsafeEntry = CreateFeedEntry(1) with
        {
            NormalizedUrl = "javascript:alert(1)"
        };
        viewModel.SelectedTimelineEntry = new(
            unsafeEntry,
            "Daily Feed",
            "Technology");

        Assert.False(viewModel.OpenSelectedFeedOriginalCommand.CanExecute(null));

        viewModel.SelectedTimelineEntry = new(
            CreateFeedEntry(2) with
            {
                NormalizedUrl = "https://user:password@example.com/story"
            },
            "Daily Feed",
            "Technology");

        Assert.False(viewModel.OpenSelectedFeedOriginalCommand.CanExecute(null));
    }

    [Fact]
    public async Task TimelineHydratesPrivateStateAndToggleCommandsPersistChanges()
    {
        FeedEntry entry = CreateFeedEntry(0);
        var stateRepository = new StubEntryStateRepository();
        stateRepository.States[entry.Id] = new(
            entry.Id,
            "default",
            true,
            false,
            false,
            42,
            "稍后回看",
            TimelineNow);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            entryStates: stateRepository);

        await viewModel.InitializeAsync(CancellationToken.None);

        FeedTimelineItem item = Assert.Single(viewModel.TimelineEntries);
        Assert.True(item.IsRead);
        Assert.False(item.IsStarred);
        Assert.Equal(42, item.Progress);
        Assert.Equal("稍后回看", item.Note);

        await viewModel.ToggleTimelineStarCommand.ExecuteAsync(item);
        FeedTimelineItem updated = Assert.Single(viewModel.TimelineEntries);
        Assert.True(updated.IsStarred);
        Assert.True(updated.IsRead);
        Assert.Equal(1, stateRepository.PatchCalls);
    }

    [Fact]
    public async Task SelectingUnreadTimelineEntryMarksItReadAndManualToggleRestoresUnread()
    {
        FeedEntry entry = CreateFeedEntry(0);
        var states = new StubEntryStateRepository();
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            entryStates: states);

        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.SelectedTimelineEditorLoad;

        FeedTimelineItem opened = Assert.Single(viewModel.TimelineEntries);
        Assert.True(opened.IsRead);
        Assert.True(states.States[entry.Id].IsRead);

        await viewModel.ToggleTimelineReadCommand.ExecuteAsync(opened);

        Assert.False(Assert.Single(viewModel.TimelineEntries).IsRead);
        Assert.False(states.States[entry.Id].IsRead);
        Assert.Equal(2, states.PatchCalls);
    }

    [Fact]
    public async Task TimelineProgressIsDebouncedPersistsAndCanReset()
    {
        FeedEntry entry = CreateFeedEntry(0);
        var states = new StubEntryStateRepository();
        states.States[entry.Id] = new(
            entry.Id,
            "default",
            true,
            false,
            false,
            10,
            string.Empty,
            TimelineNow);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            entryStates: states);
        await viewModel.InitializeAsync(CancellationToken.None);
        FeedTimelineItem item = Assert.Single(viewModel.TimelineEntries);

        viewModel.QueueTimelineProgress(item, 25);
        viewModel.QueueTimelineProgress(item, 55);
        await viewModel.TimelineProgressWrite;

        Assert.Equal(55, states.States[entry.Id].Progress);
        Assert.Equal(55, Assert.Single(viewModel.TimelineEntries).Progress);

        viewModel.ResetTimelineProgressCommand.Execute(null);
        await viewModel.TimelineProgressWrite;

        Assert.Equal(0, states.States[entry.Id].Progress);
        Assert.Equal(0, Assert.Single(viewModel.TimelineEntries).Progress);
    }

    [Fact]
    public async Task TimelineFavoriteNoteAndTagsPersistThroughPrivateRepositories()
    {
        FeedEntry entry = CreateFeedEntry(0);
        var favorites = new StubFavoriteRepository();
        TagItem existingTag = favorites.SeedTag("稍后阅读", "#4B6B88");
        favorites.SeedFavorite(entry.Id, "初始备注", existingTag);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            favorites: favorites);

        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.SelectedTimelineEditorLoad;

        FeedTimelineItem initial = Assert.Single(viewModel.TimelineEntries);
        Assert.True(initial.IsStarred);
        Assert.Equal("初始备注", initial.Note);
        Assert.Equal("初始备注", viewModel.SelectedTimelineNote);
        Assert.Equal(existingTag, Assert.Single(viewModel.SelectedTimelineTags));

        viewModel.SelectedTimelineNote = "更新后的私人备注";
        await viewModel.SaveTimelineNoteCommand.ExecuteAsync();

        FavoriteItem saved = favorites.GetFavorite(entry.Id)!;
        Assert.NotNull(saved);
        Assert.Equal("更新后的私人备注", saved.Note);
        Assert.Equal("更新后的私人备注", Assert.Single(viewModel.TimelineEntries).Note);

        viewModel.TimelineTagInput = "  本地模型  ";
        await viewModel.AddTimelineTagCommand.ExecuteAsync();

        Assert.Equal(string.Empty, viewModel.TimelineTagInput);
        Assert.Contains(viewModel.SelectedTimelineTags, tag => tag.Name == "本地模型");
        Assert.Contains(viewModel.TimelineTags, tag => tag.Label == "本地模型");
        TagItem added = Assert.Single(viewModel.SelectedTimelineTags, tag => tag.Name == "本地模型");

        await viewModel.RemoveTimelineTagCommand.ExecuteAsync(added);

        Assert.DoesNotContain(viewModel.SelectedTimelineTags, tag => tag.Id == added.Id);
        Assert.DoesNotContain(favorites.GetEntityTags(entry.Id), tag => tag.Id == added.Id);
        Assert.True(favorites.Tags.ContainsKey(added.Id));
    }

    [Fact]
    public async Task TimelineNoteFailureKeepsPersistedItemAndReportsFailure()
    {
        FeedEntry entry = CreateFeedEntry(0);
        var favorites = new StubFavoriteRepository();
        favorites.SeedFavorite(entry.Id, "原备注");
        favorites.ThrowOnFavoriteUpsert = true;
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            favorites: favorites);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedTimelineNote = "未保存的新备注";

        await viewModel.SaveTimelineNoteCommand.ExecuteAsync();

        Assert.Equal("原备注", Assert.Single(viewModel.TimelineEntries).Note);
        Assert.Equal("原备注", favorites.GetFavorite(entry.Id)?.Note);
        Assert.Contains("保存失败", viewModel.TimelineEditorStatus);
    }

    [Fact]
    public async Task TimelineNoteDoesNotImplicitlyFavoriteEntry()
    {
        FeedEntry entry = CreateFeedEntry(0);
        var states = new StubEntryStateRepository();
        var favorites = new StubFavoriteRepository();
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            entryStates: states,
            favorites: favorites);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedTimelineNote = "只记备注，不收藏";

        await viewModel.SaveTimelineNoteCommand.ExecuteAsync();

        FeedTimelineItem saved = Assert.Single(viewModel.TimelineEntries);
        Assert.False(saved.IsStarred);
        Assert.Equal("只记备注，不收藏", saved.Note);
        Assert.Equal("只记备注，不收藏", states.States[entry.Id].Note);
        Assert.Null(favorites.GetFavorite(entry.Id));
    }

    [Fact]
    public async Task TimelineNoteCancelRestoresSavedTextAfterStateToggle()
    {
        FeedEntry entry = CreateFeedEntry(0);
        var states = new StubEntryStateRepository();
        var favorites = new StubFavoriteRepository();
        favorites.SeedFavorite(entry.Id, "已保存备注");
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            entryStates: states,
            favorites: favorites);
        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.SelectedTimelineEditorLoad;
        viewModel.SelectedTimelineNote = "尚未保存的编辑";
        FeedTimelineItem opened = Assert.Single(viewModel.TimelineEntries);

        await viewModel.ToggleTimelineReadCommand.ExecuteAsync(opened);

        Assert.Equal("尚未保存的编辑", viewModel.SelectedTimelineNote);
        int patchCallsBeforeCancel = states.PatchCalls;
        Assert.True(viewModel.CancelTimelineNoteEditCommand.CanExecute(null));
        viewModel.CancelTimelineNoteEditCommand.Execute(null);

        Assert.Equal("已保存备注", viewModel.SelectedTimelineNote);
        Assert.Equal(patchCallsBeforeCancel, states.PatchCalls);
        Assert.Contains("已撤销", viewModel.TimelineEditorStatus);
        Assert.False(viewModel.CancelTimelineNoteEditCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimelineStarFailureRestoresOriginalFavorite(bool initiallyStarred)
    {
        FeedEntry entry = CreateFeedEntry(0);
        var states = new StubEntryStateRepository
        {
            ThrowOnPatch = true
        };
        var favorites = new StubFavoriteRepository();
        if (initiallyStarred)
        {
            favorites.SeedFavorite(entry.Id, "原备注");
        }
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            entryStates: states,
            favorites: favorites);
        await viewModel.InitializeAsync(CancellationToken.None);
        FeedTimelineItem item = Assert.Single(viewModel.TimelineEntries);

        await viewModel.ToggleTimelineStarCommand.ExecuteAsync(item);

        Assert.Equal(initiallyStarred, Assert.Single(viewModel.TimelineEntries).IsStarred);
        Assert.Equal(initiallyStarred, favorites.GetFavorite(entry.Id) is not null);
        Assert.Contains("保存失败", viewModel.TimelineEditorStatus);
    }

    [Fact]
    public async Task TimelineNoteFailureRestoresFavoriteWhenStateWriteFails()
    {
        FeedEntry entry = CreateFeedEntry(0);
        var states = new StubEntryStateRepository
        {
            ThrowOnPatch = true
        };
        var favorites = new StubFavoriteRepository();
        favorites.SeedFavorite(entry.Id, "原备注");
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]),
            entryStates: states,
            favorites: favorites);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedTimelineNote = "新备注";

        await viewModel.SaveTimelineNoteCommand.ExecuteAsync();

        Assert.Equal("原备注", favorites.GetFavorite(entry.Id)?.Note);
        Assert.Equal("原备注", Assert.Single(viewModel.TimelineEntries).Note);
        Assert.Contains("保存失败", viewModel.TimelineEditorStatus);
    }

    [Fact]
    public async Task TimelineEditorWritesStayBoundToEntryWhenSelectionChanges()
    {
        FeedEntry firstEntry = CreateFeedEntry(0);
        FeedEntry secondEntry = CreateFeedEntry(1);
        var states = new StubEntryStateRepository();
        var favorites = new StubFavoriteRepository();
        TagItem firstTag = favorites.SeedTag("第一条", "#4B6B88");
        TagItem secondTag = favorites.SeedTag("第二条", "#4B6B88");
        favorites.SeedFavorite(firstEntry.Id, "第一条备注", firstTag);
        favorites.SeedFavorite(secondEntry.Id, "第二条备注", secondTag);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([firstEntry, secondEntry]),
            entryStates: states,
            favorites: favorites);
        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.SelectedTimelineEditorLoad;
        FeedTimelineItem firstItem = Assert.Single(
            viewModel.TimelineEntries,
            item => item.Entry.Id == firstEntry.Id);
        FeedTimelineItem secondItem = Assert.Single(
            viewModel.TimelineEntries,
            item => item.Entry.Id == secondEntry.Id);

        TaskCompletionSource favoriteRelease = favorites.BlockNextFavoriteUpsert();
        viewModel.SelectedTimelineNote = "第一条更新备注";
        Task save = viewModel.SaveTimelineNoteCommand.ExecuteAsync();
        await favorites.FavoriteUpsertStarted;
        viewModel.SelectedTimelineEntry = secondItem;
        await viewModel.SelectedTimelineEditorLoad;
        favoriteRelease.SetResult();
        await save;

        Assert.Equal("第一条更新备注", favorites.GetFavorite(firstEntry.Id)?.Note);
        Assert.Equal("第一条更新备注", states.States[firstEntry.Id].Note);
        Assert.Equal(secondEntry.Id, viewModel.SelectedTimelineEntry?.Entry.Id);
        Assert.Equal("第二条备注", viewModel.SelectedTimelineNote);

        viewModel.SelectedTimelineEntry = firstItem;
        await viewModel.SelectedTimelineEditorLoad;
        TaskCompletionSource tagRelease = favorites.BlockNextTagUpsert();
        viewModel.TimelineTagInput = "新增标签";
        Task addTag = viewModel.AddTimelineTagCommand.ExecuteAsync();
        await favorites.TagUpsertStarted;
        viewModel.SelectedTimelineEntry = secondItem;
        await viewModel.SelectedTimelineEditorLoad;
        tagRelease.SetResult();
        await addTag;

        Assert.Equal(
            ["第一条", "新增标签"],
            favorites.GetEntityTags(firstEntry.Id).Select(tag => tag.Name).Order().ToArray());
        Assert.Equal(
            ["第二条"],
            favorites.GetEntityTags(secondEntry.Id).Select(tag => tag.Name).ToArray());
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
    public async Task TimelinePrivateFiltersFlowIntoQueryAndClearTogether()
    {
        var entries = new StubFeedEntryRepository([CreateFeedEntry(0)]);
        var favorites = new StubFavoriteRepository();
        TagItem tag = favorites.SeedTag("精读", "#4B6B88");
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries,
            favorites: favorites);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedTimelineReadFilter = Assert.Single(
            viewModel.TimelineReadFilters,
            option => option.Value == FeedEntryReadFilter.Unread);
        viewModel.TimelineFavoritesOnly = true;
        viewModel.SelectedTimelineTag = Assert.Single(
            viewModel.TimelineTags,
            option => option.Id == tag.Id);

        await viewModel.ApplyTimelineFiltersCommand.ExecuteAsync();

        FeedEntryQuery filtered = entries.Queries[^1];
        Assert.Equal(FeedEntryReadFilter.Unread, filtered.ReadFilter);
        Assert.True(filtered.FavoritesOnly);
        Assert.Equal(tag.Id, filtered.TagId);
        Assert.Equal("default", filtered.LocalProfile);

        await viewModel.ClearTimelineFiltersCommand.ExecuteAsync();

        FeedEntryQuery cleared = entries.Queries[^1];
        Assert.Equal(FeedEntryReadFilter.All, cleared.ReadFilter);
        Assert.False(cleared.FavoritesOnly);
        Assert.Null(cleared.TagId);
        Assert.Equal(FeedEntryReadFilter.All, viewModel.SelectedTimelineReadFilter?.Value);
        Assert.False(viewModel.TimelineFavoritesOnly);
        Assert.Null(viewModel.SelectedTimelineTag?.Id);
    }

    [Fact]
    public async Task NewTimelineQueryCancelsOldRequestAndKeepsLatestResults()
    {
        var entries = new StubFeedEntryRepository([CreateFeedEntry(0)]);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: entries);
        await viewModel.InitializeAsync(CancellationToken.None);
        entries.DelayNextQuery();
        viewModel.TimelineKeyword = "stale query";

        Task staleQuery =
            viewModel.ApplyTimelineFiltersCommand.ExecuteAsync();
        await entries.DelayedQueryStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        Task latestQuery =
            viewModel.ClearTimelineFiltersCommand.ExecuteAsync();

        await entries.DelayedQueryCancelled.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        await Task.WhenAll(staleQuery, latestQuery).WaitAsync(
            TimeSpan.FromSeconds(1));

        Assert.Equal(string.Empty, viewModel.TimelineKeyword);
        Assert.Single(viewModel.TimelineEntries);
        Assert.Null(entries.Queries[^1].SearchText);
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
        Assert.Equal(
            [0, 50],
            entries.Queries
                .Where(query => query.ViewKind is null)
                .Select(query => query.Offset)
                .ToArray());
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
        Assert.Single(entries.Queries, query => query.ViewKind is null);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"首屏加载耗时 {stopwatch.Elapsed.TotalMilliseconds:F0} ms。");
    }

    [Fact]
    public async Task EntityNavigationOpensFeedEntryInTheReader()
    {
        FeedEntry entry = CreateFeedEntry(42);
        using NewsCenterViewModel viewModel = CreateViewModel(
            CreateSnapshot(),
            feedEntries: new([entry]));

        await viewModel.OpenEntityAsync(
            "feed_entry",
            entry.Id,
            CancellationToken.None);

        Assert.Equal(0, viewModel.SelectedSectionIndex);
        Assert.Equal(entry.Id, viewModel.SelectedTimelineEntry?.Entry.Id);
        Assert.Equal(entry.Id, viewModel.SelectedFeedArticle?.Id);
        Assert.Contains("统一搜索", viewModel.TimelineStatus);
    }

    private static NewsCenterViewModel CreateViewModel(
        NewsCenterSnapshot snapshot,
        StubDesktopFileDialogService? dialogs = null,
        StubFeedEntryRepository? feedEntries = null,
        StubFeedCatalogSyncService? catalogSync = null,
        StubFeedCatalogRepository? catalogRepository = null,
        StubEntryStateRepository? entryStates = null,
        StubFavoriteRepository? favorites = null,
        StubFeedFullTextQueueService? fullText = null,
        StubFeedAiSummaryService? summaries = null,
        StubFeedAiTranslationService? translations = null,
        IFeedAudioPlaybackService? audioPlayback = null,
        IFeedMediaDeliveryService? mediaDelivery = null,
        IAppNavigationService? navigation = null,
        IFeedVideoDeliveryPlanningService? videoPlanner = null,
        IFeedSmartViewRepository? smartViews = null,
        IFeedSmartViewSyncService? smartViewSync = null,
        TimeProvider? timeProvider = null) =>
        new(
            new StubNewsCenterService(snapshot),
            new StubAiReportService(null),
            new StubNewsRepository(),
            dialogs ?? new StubDesktopFileDialogService(),
            feedEntries ?? new StubFeedEntryRepository([]),
            catalogRepository ?? new StubFeedCatalogRepository(CreateCatalog()),
            catalogSync ?? new StubFeedCatalogSyncService(),
            entryStates ?? new StubEntryStateRepository(),
            favorites ?? new StubFavoriteRepository(),
            fullText ?? new StubFeedFullTextQueueService(),
            summaries ?? new StubFeedAiSummaryService(),
            translations ?? new StubFeedAiTranslationService(),
            audioPlayback,
            mediaDelivery,
            audioPlayback is null && videoPlanner is null
                ? null
                : new MediaJobInbox(),
            navigation,
            videoPlanner,
            smartViews,
            smartViewSync,
            timeProvider);

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

    private static FeedSmartViewSnapshot CreateSmartViewSnapshot() => new(
        9,
        FeedSmartViewScope.Active,
        TimelineNow.AddMinutes(-10),
        TimelineNow.AddMinutes(-5),
        [
            new(
                SmartViewId,
                3,
                "近期未读视频",
                20,
                true,
                new(
                    FeedId,
                    CategoryId,
                    EntryViewKind.Video,
                    FeedEntryReadFilter.Unread,
                    true,
                    "release",
                    30))
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

    private static FeedFullTextContent CreateFullTextContent(FeedEntry entry, string body) => new(
        entry.Id,
        new(
            entry.NormalizedUrl!,
            entry.NormalizedUrl!,
            entry.Title,
            entry.Author,
            entry.PublishedAt,
            [
                new(
                    ArticleContentBlockKind.Paragraph,
                    body,
                    null,
                    null,
                    [])
            ],
            [],
            "readability-v1"),
        $"{entry.Id}-extracted",
        TimelineNow.AddMinutes(5));

    private sealed class StubFeedFullTextQueueService : IFeedFullTextQueueService
    {
        public Dictionary<string, FeedFullTextContent?> Contents { get; } =
            new(StringComparer.Ordinal);
        public string? DelayedEntryId { get; init; }
        public TaskCompletionSource DelayedRequestCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FeedFullTextContent?> FetchOnOpenAsync(
            string entryId,
            CancellationToken cancellationToken)
        {
            if (string.Equals(entryId, DelayedEntryId, StringComparison.Ordinal))
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    DelayedRequestCancelled.TrySetResult();
                    throw;
                }
            }
            return Contents.GetValueOrDefault(entryId);
        }

        public Task<int> ProcessBackgroundBatchAsync(CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class StubFeedAiSummaryService : IFeedAiSummaryService
    {
        public List<FeedAiSummaryInput> SingleInputs { get; } = [];
        public List<IReadOnlyList<FeedAiSummaryInput>> BatchInputs { get; } = [];
        public string? DelayedEntryId { get; init; }
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RequestCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FeedAiResult> SummarizeAsync(
            FeedAiSummaryInput input,
            CancellationToken cancellationToken)
        {
            SingleInputs.Add(input);
            if (string.Equals(input.EntryId, DelayedEntryId, StringComparison.Ordinal))
            {
                RequestStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    RequestCancelled.TrySetResult();
                    throw;
                }
            }
            return CreateResult(input);
        }

        public Task<IReadOnlyList<FeedAiSummaryBatchItem>> SummarizeBatchAsync(
            IReadOnlyList<FeedAiSummaryInput> inputs,
            CancellationToken cancellationToken)
        {
            BatchInputs.Add(inputs);
            return Task.FromResult<IReadOnlyList<FeedAiSummaryBatchItem>>(
                inputs.Select(input => new FeedAiSummaryBatchItem(
                    input.EntryId,
                    CreateResult(input),
                    null)).ToArray());
        }

        private static FeedAiResult CreateResult(FeedAiSummaryInput input) =>
            new(
                $"summary-{input.EntryId}",
                new(
                    input.EntryId,
                    input.ContentHash,
                    FeedAiTaskType.Summary,
                    "und",
                    FeedAiSummaryOptions.Default.Model,
                    FeedAiSummaryOptions.Default.PromptVersion),
                input.Title,
                "测试摘要",
                1,
                10,
                5,
                15,
                100,
                null,
                TimelineNow,
                TimelineNow);
    }

    private sealed class StubFeedAiTranslationService : IFeedAiTranslationService
    {
        public List<FeedAiTranslationInput> Inputs { get; } = [];
        public string? DelayedEntryId { get; init; }
        public AppError? Error { get; init; }
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RequestCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FeedAiTranslationResult> TranslateAsync(
            FeedAiTranslationInput input,
            CancellationToken cancellationToken)
        {
            Inputs.Add(input);
            if (string.Equals(input.EntryId, DelayedEntryId, StringComparison.Ordinal))
            {
                RequestStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    RequestCancelled.TrySetResult();
                    throw;
                }
            }
            if (Error is not null) throw new AppException(Error);

            DateTimeOffset now = TimelineNow;
            var cache = new FeedAiResult(
                $"translation-{input.EntryId}",
                new(
                    input.EntryId,
                    input.ContentHash,
                    FeedAiTaskType.Translation,
                    input.TargetLanguage,
                    FeedAiTranslationOptions.Default.Model,
                    FeedAiTranslationOptions.Default.PromptVersion),
                input.Title,
                "{}",
                1,
                10,
                5,
                15,
                100,
                null,
                now,
                now);
            return new(
                cache,
                input.Blocks
                    .Select(block => new FeedAiTranslatedBlock(
                        block.Sequence,
                        block.Kind,
                        block.Text,
                        $"译：{block.Text}",
                        block.ResourceUrl,
                        block.HeadingLevel,
                        block.Links))
                    .ToArray());
        }
    }

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

        public Task<ContentSearchPage> SearchContentAsync(
            ContentSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ContentSearchPage([], false));

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
        private int _delayNextQuery;

        public List<FeedEntryQuery> Queries { get; } = [];
        public TaskCompletionSource DelayedQueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DelayedQueryCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void DelayNextQuery() =>
            Interlocked.Exchange(ref _delayNextQuery, 1);

        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken) =>
            Task.FromResult(entries.FirstOrDefault(entry => entry.Id == entryId));

        public async Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            if (Interlocked.Exchange(ref _delayNextQuery, 0) == 1)
            {
                DelayedQueryStarted.TrySetResult();
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    DelayedQueryCancelled.TrySetResult();
                    throw;
                }
            }

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
            if (query.ViewKind is EntryViewKind viewKind)
            {
                filtered = filtered.Where(entry =>
                    EntryViewClassifier.Classify(
                        null,
                        entry.Enclosures
                            .Select(enclosure => FeedAttachmentClassifier.Classify(
                                enclosure,
                                entry.NormalizedUrl))
                            .ToArray(),
                        null) == viewKind);
            }

            FeedEntry[] ordered = filtered
                .OrderByDescending(EntryTime)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToArray();
            FeedEntry[] page = ordered.Skip(query.Offset).Take(query.Limit).ToArray();
            return new FeedEntryPage(
                page,
                query.Offset,
                ordered.Length > query.Offset + page.Length,
                query.Offset + page.Length);
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

    private sealed class StubEntryStateRepository : IEntryStateRepository
    {
        public Dictionary<string, EntryState> States { get; } = new(StringComparer.Ordinal);
        public int PatchCalls { get; private set; }
        public bool ThrowOnPatch { get; init; }

        public Task<IReadOnlyDictionary<string, EntryState>> GetAsync(
            IReadOnlyCollection<string> entryIds,
            string localProfile,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, EntryState> result = entryIds
                .Where(States.ContainsKey)
                .Select(id => States[id])
                .Where(state => state.LocalProfile == localProfile)
                .ToDictionary(state => state.EntryId, StringComparer.Ordinal);
            return Task.FromResult(result);
        }

        public Task<EntryState> PatchAsync(
            string entryId,
            string localProfile,
            EntryStatePatch patch,
            CancellationToken cancellationToken)
        {
            PatchCalls++;
            if (ThrowOnPatch)
            {
                throw new InvalidOperationException("Simulated entry state write failure.");
            }
            EntryState current = States.GetValueOrDefault(entryId)
                ?? new(
                    entryId,
                    localProfile,
                    false,
                    false,
                    false,
                    0,
                    string.Empty,
                    TimelineNow);
            EntryState updated = current with
            {
                IsRead = patch.IsRead ?? current.IsRead,
                IsStarred = patch.IsStarred ?? current.IsStarred,
                IsHidden = patch.IsHidden ?? current.IsHidden,
                Progress = patch.Progress ?? current.Progress,
                Note = patch.Note ?? current.Note,
                UpdatedAt = TimelineNow
            };
            States[entryId] = updated;
            return Task.FromResult(updated);
        }
    }

    private sealed class StubFavoriteRepository : IFavoriteRepository
    {
        private const string EntityType = "feed_entry";
        private readonly Dictionary<string, FavoriteItem> _favorites = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _entityTags = new(StringComparer.Ordinal);
        private TaskCompletionSource _favoriteUpsertStarted = CompletedSignal();
        private TaskCompletionSource? _favoriteUpsertRelease;
        private TaskCompletionSource _tagUpsertStarted = CompletedSignal();
        private TaskCompletionSource? _tagUpsertRelease;

        public Dictionary<string, TagItem> Tags { get; } = new(StringComparer.Ordinal);
        public bool ThrowOnFavoriteUpsert { get; set; }
        public Task FavoriteUpsertStarted => _favoriteUpsertStarted.Task;
        public Task TagUpsertStarted => _tagUpsertStarted.Task;

        public TaskCompletionSource BlockNextFavoriteUpsert()
        {
            _favoriteUpsertStarted = NewSignal();
            _favoriteUpsertRelease = NewSignal();
            return _favoriteUpsertRelease;
        }

        public TaskCompletionSource BlockNextTagUpsert()
        {
            _tagUpsertStarted = NewSignal();
            _tagUpsertRelease = NewSignal();
            return _tagUpsertRelease;
        }

        public FavoriteItem? GetFavorite(string entityId) =>
            _favorites.GetValueOrDefault(entityId);

        public TagItem[] GetEntityTags(string entityId) =>
            _entityTags.GetValueOrDefault(entityId)?
                .Select(id => Tags[id])
                .OrderBy(tag => tag.Name, StringComparer.Ordinal)
                .ToArray()
            ?? [];

        public TagItem SeedTag(string name, string color)
        {
            var tag = new TagItem($"tag-{Tags.Count + 1}", name, color, TimelineNow);
            Tags[tag.Id] = tag;
            return tag;
        }

        public void SeedFavorite(string entityId, string note, params TagItem[] tags)
        {
            _favorites[entityId] = new(
                $"favorite-{entityId}",
                EntityType,
                entityId,
                note,
                TimelineNow);
            _entityTags[entityId] = tags.Select(tag => tag.Id).ToHashSet(StringComparer.Ordinal);
        }

        public Task<int> GetCountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_favorites.Count);

        public Task<FavoriteItem?> GetAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                entityType == EntityType ? _favorites.GetValueOrDefault(entityId) : null);

        public async Task<FavoriteItem> UpsertAsync(
            string entityType,
            string entityId,
            string note,
            CancellationToken cancellationToken)
        {
            Assert.Equal(EntityType, entityType);
            _favoriteUpsertStarted.TrySetResult();
            if (_favoriteUpsertRelease is not null)
            {
                await _favoriteUpsertRelease.Task.WaitAsync(cancellationToken);
                _favoriteUpsertRelease = null;
            }
            if (ThrowOnFavoriteUpsert)
            {
                throw new InvalidOperationException("Simulated favorite write failure.");
            }
            FavoriteItem favorite = _favorites.GetValueOrDefault(entityId) is { } current
                ? current with { Note = note }
                : new($"favorite-{entityId}", entityType, entityId, note, TimelineNow);
            _favorites[entityId] = favorite;
            return favorite;
        }

        public Task<bool> RemoveAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(EntityType, entityType);
            return Task.FromResult(_favorites.Remove(entityId));
        }

        public Task<IReadOnlyDictionary<string, FavoriteItem>> GetForEntitiesAsync(
            string entityType,
            IReadOnlyCollection<string> entityIds,
            CancellationToken cancellationToken)
        {
            Assert.Equal(EntityType, entityType);
            IReadOnlyDictionary<string, FavoriteItem> result = entityIds
                .Where(_favorites.ContainsKey)
                .ToDictionary(id => id, id => _favorites[id], StringComparer.Ordinal);
            return Task.FromResult(result);
        }

        public async Task<TagItem> UpsertTagAsync(
            string name,
            string color,
            CancellationToken cancellationToken)
        {
            _tagUpsertStarted.TrySetResult();
            if (_tagUpsertRelease is not null)
            {
                await _tagUpsertRelease.Task.WaitAsync(cancellationToken);
                _tagUpsertRelease = null;
            }
            string normalized = name.Normalize().Trim();
            TagItem? existing = Tags.Values.FirstOrDefault(
                tag => string.Equals(tag.Name, normalized, StringComparison.OrdinalIgnoreCase));
            TagItem tag = existing is null
                ? SeedTag(normalized, color)
                : existing with { Name = normalized, Color = color };
            Tags[tag.Id] = tag;
            return tag;
        }

        public async Task<TagItem> AddTagAsync(
            string entityType,
            string entityId,
            string name,
            string color,
            CancellationToken cancellationToken)
        {
            Assert.Equal(EntityType, entityType);
            TagItem tag = await UpsertTagAsync(name, color, cancellationToken);
            if (!_entityTags.TryGetValue(entityId, out HashSet<string>? tagIds))
            {
                tagIds = new(StringComparer.Ordinal);
                _entityTags[entityId] = tagIds;
            }
            tagIds.Add(tag.Id);
            return tag;
        }

        public Task<IReadOnlyList<TagItem>> GetTagsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagItem>>(Tags.Values.ToArray());

        public Task<IReadOnlyList<TagItem>> GetTagsForEntityAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(EntityType, entityType);
            return Task.FromResult<IReadOnlyList<TagItem>>(GetEntityTags(entityId));
        }

        public Task SetTagsAsync(
            string entityType,
            string entityId,
            IReadOnlyCollection<string> tagIds,
            CancellationToken cancellationToken)
        {
            Assert.Equal(EntityType, entityType);
            Assert.All(tagIds, id => Assert.True(Tags.ContainsKey(id)));
            _entityTags[entityId] = tagIds.ToHashSet(StringComparer.Ordinal);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteTagAsync(string tagId, CancellationToken cancellationToken) =>
            Task.FromResult(Tags.Remove(tagId));

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource CompletedSignal()
        {
            TaskCompletionSource signal = NewSignal();
            signal.SetResult();
            return signal;
        }
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

    private sealed class StubFeedSmartViewRepository(
        FeedSmartViewSnapshot snapshot)
        : IFeedSmartViewRepository
    {
        public int GetCount { get; private set; }
        public FeedSmartViewSnapshot Snapshot { get; set; } = snapshot;
        public bool ThrowOnGet { get; set; }

        public Task<FeedSmartViewSnapshot> GetAsync(
            CancellationToken cancellationToken)
        {
            GetCount++;
            if (ThrowOnGet)
            {
                throw new InvalidOperationException(
                    "Simulated smart-view cache failure.");
            }
            return Task.FromResult(Snapshot);
        }

        public Task ReplaceAsync(
            FeedSmartViewSnapshot value,
            CancellationToken cancellationToken)
        {
            Snapshot = value;
            return Task.CompletedTask;
        }

        public Task<bool> MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class StubFeedSmartViewSyncService(Action onSync)
        : IFeedSmartViewSyncService
    {
        public int Count { get; private set; }
        public bool ThrowOnSync { get; init; }

        public Task<FeedSmartViewSyncResult> SyncAsync(
            CancellationToken cancellationToken)
        {
            Count++;
            if (ThrowOnSync)
            {
                throw new InvalidOperationException(
                    "Simulated smart-view sync failure.");
            }
            onSync();
            return Task.FromResult(new FeedSmartViewSyncResult(
                FeedSmartViewSyncOutcome.Updated,
                10,
                TimelineNow));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubFeedAudioPlaybackService :
        IFeedAudioPlaybackService
    {
        public event EventHandler<FeedAudioPlaybackChangedEventArgs>? Changed;
        public FeedAudioPlaybackSnapshot Snapshot { get; private set; } =
            FeedAudioPlaybackSnapshot.Idle;

        public void Play(FeedAudioPlaybackRequest request)
        {
            Snapshot = new(
                request.SourceUrl,
                FeedAudioPlaybackStatus.Playing,
                TimeSpan.Zero,
                null);
            Changed?.Invoke(
                this,
                new FeedAudioPlaybackChangedEventArgs(Snapshot));
        }

        public void Pause()
        {
        }

        public void Seek(TimeSpan position)
        {
        }

        public void StopPlayback()
        {
            Snapshot = FeedAudioPlaybackSnapshot.Idle;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubFeedMediaDeliveryService :
        IFeedMediaDeliveryService
    {
        public Task<FeedMediaDeliveryRegistration> DeliverAsync(
            FeedEntry entry,
            FeedEnclosure enclosure,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubAppNavigationService :
        IAppNavigationService
    {
        public Task NavigateAsync(
            AppNavigationRequest request,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubFeedVideoDeliveryPlanningService :
        IFeedVideoDeliveryPlanningService
    {
        public Task<FeedVideoDeliveryPlan> PlanAsync(
            FeedEntry entry,
            FeedEnclosure enclosure,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
