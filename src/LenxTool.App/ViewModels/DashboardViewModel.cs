using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using LenxTool.App.Controls;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record NewsPreview(
    string Time,
    string Source,
    string Title,
    string Summary,
    string? Url = null,
    string? ContentHash = null);

public sealed record BriefingSectionPreview(string Title, IReadOnlyList<string> Items);

public sealed record TrendPreview(int Rank, string Platform, string Title, string Heat);

public sealed record TaskPreview(string Name, string Status, double Progress, string Detail);

public sealed record QuickAction(string Label, string Description, string PageId, string IconData);

public sealed class DashboardViewModel : PageViewModel
{
    public const string CompatibilityFeedUrl = FeedCompatibilitySeed.Url;
    private const int NewsLimit = 6;
    private const int BriefingSectionLimit = 5;
    private const int BriefingItemsPerSection = 3;
    private const int TrendLimit = 6;
    private const int TaskLimit = 4;

    private readonly IFeedEntryRepository _feedEntries;
    private readonly IFeedCatalogRepository _feedCatalog;
    private readonly INewsRepository _newsRepository;
    private readonly IMediaJobRepository _jobs;
    private readonly IFavoriteRepository _favorites;
    private string _dataStatus = "正在读取本地首页数据……";
    private string _newsStatus = "正在读取本地 Feed……";
    private string _trendStatus = "正在读取热点缓存……";
    private string _briefingTitle = "正在读取每日早报……";
    private string _briefingMeta = "本地缓存";
    private string _briefingEmptyText = string.Empty;
    private int _favoriteCount;

    public DashboardViewModel(
        IFeedEntryRepository feedEntries,
        IFeedCatalogRepository feedCatalog,
        INewsRepository newsRepository,
        IMediaJobRepository jobs,
        IFavoriteRepository favorites)
        : base("今天，从重要的开始", "本地缓存与共享 Feed 的实时概览")
    {
        _feedEntries = feedEntries;
        _feedCatalog = feedCatalog;
        _newsRepository = newsRepository;
        _jobs = jobs;
        _favorites = favorites;
    }

    public ObservableCollection<NewsPreview> News { get; } = [];
    public ObservableCollection<NewsPreview> LegacyNews { get; } = [];
    public ObservableCollection<BriefingSectionPreview> BriefingSections { get; } = [];
    public ObservableCollection<TrendPreview> Trends { get; } = [];
    public ObservableCollection<TaskPreview> RecentTasks { get; } = [];

    public IReadOnlyList<QuickAction> QuickActions { get; } =
    [
        new("生成字幕", "导入音视频并创建批量任务", "media", "M4,3 L20,3 20,15 13,15 8,20 8,15 4,15 Z"),
        new("整理 JSON", "格式化、校验、排序或 Diff", "tools", "M6,3 L18,3 18,21 6,21 Z M9,8 L15,8 M9,12 L15,12 M9,16 L13,16"),
        new("全局搜索", "搜索早报、热点、报告与收藏", "history", "M10,4 A6,6 0 1 0 10,16 A6,6 0 1 0 10,4 M14.5,14.5 L20,20")
    ];

    public string DataStatus
    {
        get => _dataStatus;
        private set => SetProperty(ref _dataStatus, value);
    }

    public string NewsStatus
    {
        get => _newsStatus;
        private set => SetProperty(ref _newsStatus, value);
    }

    public string TrendStatus
    {
        get => _trendStatus;
        private set => SetProperty(ref _trendStatus, value);
    }

    public string BriefingTitle
    {
        get => _briefingTitle;
        private set => SetProperty(ref _briefingTitle, value);
    }

    public string BriefingMeta
    {
        get => _briefingMeta;
        private set => SetProperty(ref _briefingMeta, value);
    }

    public string BriefingEmptyText
    {
        get => _briefingEmptyText;
        private set => SetProperty(ref _briefingEmptyText, value);
    }

    public int FavoriteCount
    {
        get => _favoriteCount;
        private set
        {
            if (SetProperty(ref _favoriteCount, value))
                OnPropertyChanged(nameof(FavoriteSummary));
        }
    }

    public string FavoriteSummary => $"收藏 {FavoriteCount} 条";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Task<FeedEntryPage> feedTask = LoadFeedEntriesAsync(cancellationToken);
        Task<FeedCatalogSnapshot?> catalogTask = LoadCatalogAsync(cancellationToken);
        Task<IReadOnlyList<NewsArticle>> legacyTask = LoadLegacyNewsAsync(cancellationToken);
        Task<IReadOnlyList<TrendItem>> trendsTask = LoadTrendsAsync(cancellationToken);
        Task<IReadOnlyList<MediaJob>> jobsTask = LoadJobsAsync(cancellationToken);
        Task<int> favoriteTask = LoadFavoriteCountAsync(cancellationToken);

        FeedEntryPage feedPage = await feedTask.ConfigureAwait(true);
        FeedCatalogSnapshot? catalog = await catalogTask.ConfigureAwait(true);
        IReadOnlyList<NewsArticle> legacy = await legacyTask.ConfigureAwait(true);
        IReadOnlyList<TrendItem> trends = await trendsTask.ConfigureAwait(true);
        IReadOnlyList<MediaJob> jobs = await jobsTask.ConfigureAwait(true);
        FavoriteCount = await favoriteTask.ConfigureAwait(true);
        UpdateBriefingOverview(legacy);

        string?[] feedIds = feedPage.Items.Select(item => item.FeedId).Distinct().ToArray();
        Dictionary<string, string> feedNames = (catalog?.Feeds ?? [])
            .Where(feed => feedIds.Contains(feed.Id, StringComparer.Ordinal))
            .ToDictionary(feed => feed.Id, feed => feed.DisplayName, StringComparer.Ordinal);

        News.Clear();
        foreach (FeedEntry entry in feedPage.Items)
        {
            DateTimeOffset displayTime = entry.PublishedAt ?? entry.UpdatedAt ?? entry.FetchedAt;
            News.Add(new(
                FormatTime(displayTime),
                feedNames.GetValueOrDefault(entry.FeedId, $"Feed · {entry.FeedId[..Math.Min(8, entry.FeedId.Length)]}"),
                entry.Title,
                string.IsNullOrWhiteSpace(entry.Summary) ? entry.SanitizedContent : entry.Summary,
                entry.NormalizedUrl,
                entry.ContentHash));
        }

        LegacyNews.Clear();
        HashSet<string> knownIdentities = News
            .SelectMany(item => new[] { item.ContentHash, NormalizeUrl(item.Url) })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (NewsArticle article in legacy.OrderByDescending(item => item.PublishedDate).ThenByDescending(item => item.FetchedAt))
        {
            string identity = NormalizeUrl(article.Url) ?? article.ContentHash;
            if (!knownIdentities.Add(identity)) continue;
            NewsPreview preview = new(
                article.PublishedDate.ToString("MM-dd", CultureInfo.InvariantCulture),
                article.Source,
                article.Title,
                string.IsNullOrWhiteSpace(article.Summary) ? article.Content : article.Summary,
                article.Url,
                article.ContentHash);
            LegacyNews.Add(preview);
            News.Add(preview);
            if (News.Count >= NewsLimit) break;
        }

        Trends.Clear();
        foreach (TrendItem trend in trends
                     .OrderBy(item => item.Rank)
                     .ThenByDescending(item => item.CapturedAt)
                     .Take(TrendLimit))
        {
            Trends.Add(new(trend.Rank, trend.Platform, trend.Title, trend.Heat));
        }

        RecentTasks.Clear();
        foreach (MediaJob job in jobs.Take(TaskLimit))
        {
            RecentTasks.Add(new(
                Path.GetFileName(job.InputPath),
                FormatJobStatus(job.Status),
                Math.Clamp(job.Progress * 100, 0, 100),
                $"{job.Engine} · {FormatTime(job.UpdatedAt)}"));
        }

        NewsStatus = News.Count == 0
            ? $"暂无本地资讯；可管理兼容 Feed：{CompatibilityFeedUrl}"
            : $"已加载 {News.Count} 条本地资讯（Feed {feedPage.Items.Count}，旧早报兼容 {LegacyNews.Count}）";
        DateTimeOffset? latestTrend = trends.Select(item => (DateTimeOffset?)item.CapturedAt).Max();
        TrendStatus = latestTrend is null
            ? "暂无本地热点缓存"
            : $"跨平台趋势 · 最近更新 {FormatTime(latestTrend.Value)}";
        DataStatus = $"本地数据 · Feed {feedPage.Items.Count} 条 · 热点 {Trends.Count} 条 · 任务 {RecentTasks.Count} 条 · {FavoriteSummary}";
    }

    private void UpdateBriefingOverview(IReadOnlyList<NewsArticle> articles)
    {
        BriefingSections.Clear();
        NewsArticle? latest = articles
            .OrderByDescending(article => article.PublishedDate)
            .ThenByDescending(article => article.FetchedAt)
            .FirstOrDefault();
        if (latest is null)
        {
            BriefingTitle = "暂无每日早报";
            BriefingMeta = "本地缓存";
            BriefingEmptyText = "刷新每日早报后，这里会显示最新一期的概览。";
            return;
        }

        BriefingTitle = latest.Title;
        BriefingMeta = $"{latest.Source} · {latest.PublishedDate:MM-dd}";
        string content = string.IsNullOrWhiteSpace(latest.RichContent)
            ? latest.Content
            : latest.RichContent;
        RichArticleDocument document = RichArticleFormatter.Parse(content, latest.Url);
        int overviewIndex = document.Blocks
            .ToList()
            .FindIndex(block =>
                block.Kind == RichArticleBlockKind.Heading
                && string.Equals(block.Text.Trim(), "概览", StringComparison.Ordinal));

        List<string>? currentItems = null;
        if (overviewIndex >= 0)
        {
            foreach (RichArticleBlock block in document.Blocks.Skip(overviewIndex + 1))
            {
                if (block.Kind == RichArticleBlockKind.Heading)
                    break;

                if (block.Kind == RichArticleBlockKind.Subheading)
                {
                    if (BriefingSections.Count >= BriefingSectionLimit)
                        break;
                    currentItems = [];
                    BriefingSections.Add(new(block.Text, currentItems));
                    continue;
                }

                if (block.Kind != RichArticleBlockKind.Bullet
                    || currentItems is null
                    || currentItems.Count >= BriefingItemsPerSection)
                {
                    continue;
                }

                currentItems.Add(block.Text);
            }
        }

        if (BriefingSections.Count == 0 && !string.IsNullOrWhiteSpace(latest.Summary))
        {
            BriefingSections.Add(new(
                "今日摘要",
                [Truncate(latest.Summary.Trim(), 180)]));
        }

        BriefingEmptyText = BriefingSections.Count > 0
            ? string.Empty
            : "本期早报暂未提供结构化概览，可进入每日早报查看全文。";
    }

    private async Task<FeedEntryPage> LoadFeedEntriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _feedEntries.QueryAsync(
                new(null, null, null, null, null, FeedEntryReadFilter.All, 0, NewsLimit, ActiveOnly: true),
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new([], 0, false);
        }
    }

    private async Task<FeedCatalogSnapshot?> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _feedCatalog.GetCatalogAsync(FeedCatalogScope.Active, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<NewsArticle>> LoadLegacyNewsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _newsRepository.GetLatestAsync(NewsLimit, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<TrendItem>> LoadTrendsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _newsRepository.GetLatestTrendsAsync(TrendLimit, null, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<MediaJob>> LoadJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _jobs.GetRecentAsync(TaskLimit, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<int> LoadFavoriteCountAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _favorites.GetCountAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string? NormalizeUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path | UriComponents.Query, UriFormat.UriEscaped)
                .TrimEnd('/')
            : null;

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength
            ? value
            : value[..(maximumLength - 1)] + "…";

    private static string FormatJobStatus(MediaJobStatus status) => status switch
    {
        MediaJobStatus.Queued => "等待开始",
        MediaJobStatus.Running => "进行中",
        MediaJobStatus.Completed => "已完成",
        MediaJobStatus.Failed => "失败",
        MediaJobStatus.Cancelled => "已取消",
        _ => "未知状态"
    };
}
