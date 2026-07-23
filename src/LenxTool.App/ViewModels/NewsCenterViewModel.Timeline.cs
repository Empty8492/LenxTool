using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class NewsCenterViewModel
{
    private const int TimelinePageSize = 50;
    private const int MaximumTimelineKeywordLength = 200;
    private readonly IFeedEntryRepository _feedEntryRepository;
    private readonly IFeedCatalogRepository _feedCatalogRepository;
    private readonly IFeedCatalogSyncService _feedCatalogSync;
    private readonly SynchronizationContext? _timelineSynchronizationContext;
    private FeedCatalogSnapshot? _timelineCatalog;
    private FeedTimelineFilterOption? _selectedTimelineCategory;
    private FeedTimelineFilterOption? _selectedTimelineFeed;
    private FeedTimelineItem? _selectedTimelineEntry;
    private NewsArticle? _selectedFeedArticle;
    private DateTime? _selectedTimelineDate;
    private DateTimeOffset? _lastTimelineRefreshAt;
    private DateTimeOffset? _catalogLastSynchronizedAt;
    private string _timelineKeyword = string.Empty;
    private string _timelineStatus = "正在读取本地 Feed 缓存…";
    private bool _hasMoreTimelineEntries;
    private bool _timelineDisposed;
    private int _timelineCatalogGeneration;
    private int _timelineQueryGeneration;
    private int _timelineNextOffset;

    public ObservableCollection<FeedTimelineItem> TimelineEntries { get; } = [];
    public ObservableCollection<FeedTimelineFilterOption> TimelineCategories { get; } = [];
    public ObservableCollection<FeedTimelineFilterOption> TimelineFeeds { get; } = [];
    public AsyncRelayCommand ApplyTimelineFiltersCommand { get; private set; } = null!;
    public AsyncRelayCommand LoadMoreTimelineCommand { get; private set; } = null!;
    public AsyncRelayCommand ClearTimelineFiltersCommand { get; private set; } = null!;

    public FeedTimelineFilterOption? SelectedTimelineCategory
    {
        get => _selectedTimelineCategory;
        set
        {
            if (ReferenceEquals(_selectedTimelineCategory, value)) return;
            _selectedTimelineCategory = value;
            OnPropertyChanged();
            RebuildTimelineFeedChoices();
        }
    }

    public FeedTimelineFilterOption? SelectedTimelineFeed
    {
        get => _selectedTimelineFeed;
        set
        {
            if (ReferenceEquals(_selectedTimelineFeed, value)) return;
            _selectedTimelineFeed = value;
            OnPropertyChanged();
        }
    }

    public FeedTimelineItem? SelectedTimelineEntry
    {
        get => _selectedTimelineEntry;
        set
        {
            if (SetProperty(ref _selectedTimelineEntry, value))
            {
                SelectedFeedArticle = value is null ? null : CreateReaderArticle(value);
            }
        }
    }

    public NewsArticle? SelectedFeedArticle
    {
        get => _selectedFeedArticle;
        private set => SetProperty(ref _selectedFeedArticle, value);
    }

    public DateTime? SelectedTimelineDate
    {
        get => _selectedTimelineDate;
        set => SetProperty(ref _selectedTimelineDate, value?.Date);
    }

    public string TimelineKeyword
    {
        get => _timelineKeyword;
        set
        {
            string normalized = value ?? string.Empty;
            if (normalized.Length > MaximumTimelineKeywordLength)
            {
                normalized = normalized[..MaximumTimelineKeywordLength];
            }
            SetProperty(ref _timelineKeyword, normalized);
        }
    }

    public string TimelineStatus
    {
        get => _timelineStatus;
        private set => SetProperty(ref _timelineStatus, value);
    }

    public bool HasMoreTimelineEntries
    {
        get => _hasMoreTimelineEntries;
        private set
        {
            if (SetProperty(ref _hasMoreTimelineEntries, value))
            {
                LoadMoreTimelineCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(TimelineEntrySummary));
            }
        }
    }

    public string TimelineEntrySummary =>
        HasMoreTimelineEntries
            ? $"已加载 {TimelineEntries.Count} 条 · 向下滚动继续"
            : $"已加载 {TimelineEntries.Count} 条 · 已到末尾";

    private void ConfigureTimeline()
    {
        ApplyTimelineFiltersCommand = new(ApplyTimelineFiltersAsync);
        LoadMoreTimelineCommand = new(
            LoadMoreTimelineAsync,
            () => HasMoreTimelineEntries);
        ClearTimelineFiltersCommand = new(ClearTimelineFiltersAsync);
        _feedCatalogSync.StatusChanged += OnTimelineCatalogSyncStatusChanged;
    }

    private async Task InitializeTimelineAsync(CancellationToken cancellationToken)
    {
        await ReloadTimelineCatalogAsync(preserveSelection: false, cancellationToken);
    }

    private async Task ReloadTimelineCatalogAsync(
        bool preserveSelection,
        CancellationToken cancellationToken)
    {
        int catalogGeneration = Interlocked.Increment(ref _timelineCatalogGeneration);
        string? selectedCategoryId = preserveSelection ? SelectedTimelineCategory?.Id : null;
        string? selectedFeedId = preserveSelection ? SelectedTimelineFeed?.Id : null;
        FeedCatalogSnapshot? catalog = await _feedCatalogRepository
            .GetCatalogAsync(FeedCatalogScope.Active, cancellationToken);
        if (catalog is null)
        {
            FeedCatalogState state = await _feedCatalogRepository.GetStateAsync(cancellationToken);
            catalog = new(state, [], []);
        }
        if (_timelineDisposed
            || catalogGeneration != Volatile.Read(ref _timelineCatalogGeneration))
        {
            return;
        }

        _timelineCatalog = catalog;
        _catalogLastSynchronizedAt = catalog.State.LastSyncedAt;
        RebuildTimelineCategoryChoices();
        if (selectedCategoryId is not null)
        {
            SelectedTimelineCategory = TimelineCategories.FirstOrDefault(
                option => string.Equals(option.Id, selectedCategoryId, StringComparison.Ordinal))
                ?? TimelineCategories[0];
        }
        if (selectedFeedId is not null)
        {
            SelectedTimelineFeed = TimelineFeeds.FirstOrDefault(
                option => string.Equals(option.Id, selectedFeedId, StringComparison.Ordinal))
                ?? TimelineFeeds[0];
        }
        await ApplyTimelineFiltersAsync(cancellationToken);
    }

    private async Task ApplyTimelineFiltersAsync(CancellationToken cancellationToken)
    {
        int generation = Interlocked.Increment(ref _timelineQueryGeneration);
        LoadMoreTimelineCommand.Cancel();
        TimelineStatus = "正在筛选本地 Feed 缓存…";
        FeedEntryPage page = await _feedEntryRepository
            .QueryAsync(CreateTimelineQuery(0), cancellationToken);
        if (_timelineDisposed
            || generation != Volatile.Read(ref _timelineQueryGeneration))
        {
            return;
        }

        TimelineEntries.Clear();
        AppendTimelinePage(page);
        SelectedTimelineEntry = TimelineEntries.FirstOrDefault();
        OnPropertyChanged(nameof(TimelineEntrySummary));
    }

    private async Task LoadMoreTimelineAsync(CancellationToken cancellationToken)
    {
        int generation = Volatile.Read(ref _timelineQueryGeneration);
        int expectedVisibleCount = TimelineEntries.Count;
        int expectedOffset = _timelineNextOffset;
        FeedEntryPage page = await _feedEntryRepository
            .QueryAsync(CreateTimelineQuery(expectedOffset), cancellationToken);
        if (_timelineDisposed
            || generation != Volatile.Read(ref _timelineQueryGeneration)
            || expectedVisibleCount != TimelineEntries.Count
            || expectedOffset != _timelineNextOffset)
        {
            return;
        }

        AppendTimelinePage(page);
        OnPropertyChanged(nameof(TimelineEntrySummary));
    }

    private async Task ClearTimelineFiltersAsync(CancellationToken cancellationToken)
    {
        SelectedTimelineCategory = TimelineCategories.FirstOrDefault();
        SelectedTimelineFeed = TimelineFeeds.FirstOrDefault();
        SelectedTimelineDate = null;
        TimelineKeyword = string.Empty;
        await ApplyTimelineFiltersAsync(cancellationToken);
    }

    private FeedEntryQuery CreateTimelineQuery(int offset)
    {
        DateTimeOffset? publishedFrom = SelectedTimelineDate is null
            ? null
            : ToTimelineBoundary(SelectedTimelineDate.Value);
        DateTimeOffset? publishedBefore = SelectedTimelineDate is null
            ? null
            : ToTimelineBoundary(SelectedTimelineDate.Value.AddDays(1));
        string? searchText = string.IsNullOrWhiteSpace(TimelineKeyword)
            ? null
            : TimelineKeyword.Trim();
        return new(
            searchText,
            SelectedTimelineFeed?.Id,
            SelectedTimelineCategory?.Id,
            publishedFrom,
            publishedBefore,
            FeedEntryReadFilter.All,
            offset,
            TimelinePageSize);
    }

    private void AppendTimelinePage(FeedEntryPage page)
    {
        _timelineNextOffset = checked(page.Offset + page.Items.Count);
        HashSet<string> existingIds = TimelineEntries
            .Select(item => item.Entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (FeedEntry entry in page.Items)
        {
            if (existingIds.Add(entry.Id))
            {
                TimelineEntries.Add(CreateTimelineItem(entry));
            }

            if (_lastTimelineRefreshAt is null || entry.FetchedAt > _lastTimelineRefreshAt)
            {
                _lastTimelineRefreshAt = entry.FetchedAt;
            }
        }

        HasMoreTimelineEntries = page.HasMore;
        UpdateTimelineStatus(_feedCatalogSync.Current);
    }

    private FeedTimelineItem CreateTimelineItem(FeedEntry entry)
    {
        FeedCatalogItem? feed = _timelineCatalog?.Feeds.FirstOrDefault(
            item => string.Equals(item.Id, entry.FeedId, StringComparison.Ordinal));
        FeedCategory? category = feed?.CategoryId is null
            ? null
            : _timelineCatalog?.Categories.FirstOrDefault(
                item => string.Equals(item.Id, feed.CategoryId, StringComparison.Ordinal));
        return new(
            entry,
            feed?.DisplayName ?? "已移除 Feed",
            category?.Name ?? "未分类");
    }

    private NewsArticle CreateReaderArticle(FeedTimelineItem item)
    {
        FeedCatalogItem? feed = _timelineCatalog?.Feeds.FirstOrDefault(
            value => string.Equals(value.Id, item.Entry.FeedId, StringComparison.Ordinal));
        string url = item.Entry.NormalizedUrl
            ?? feed?.SiteUrl
            ?? feed?.OriginalUrl
            ?? string.Empty;
        DateTimeOffset displayTime = item.DisplayTime.ToLocalTime();
        string content = string.IsNullOrWhiteSpace(item.Entry.SanitizedContent)
            ? item.Entry.Summary
            : item.Entry.SanitizedContent;
        return new(
            item.Entry.Id,
            DateOnly.FromDateTime(displayTime.DateTime),
            item.FeedName,
            item.Entry.Title,
            item.Entry.Summary,
            content,
            url,
            item.Entry.ContentHash,
            item.Entry.FetchedAt)
        {
            RichContent = content
        };
    }

    private void RebuildTimelineCategoryChoices()
    {
        TimelineCategories.Clear();
        TimelineCategories.Add(new(null, "全部分类"));
        if (_timelineCatalog is not null)
        {
            foreach (FeedCategory category in _timelineCatalog.Categories
                         .OrderBy(item => item.SortOrder)
                         .ThenBy(item => item.Name, StringComparer.CurrentCulture)
                         .ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                TimelineCategories.Add(new(category.Id, category.Name));
            }
        }
        SelectedTimelineCategory = TimelineCategories[0];
    }

    private void RebuildTimelineFeedChoices()
    {
        string? categoryId = SelectedTimelineCategory?.Id;
        TimelineFeeds.Clear();
        TimelineFeeds.Add(new(null, "全部 Feed", categoryId));
        if (_timelineCatalog is not null)
        {
            foreach (FeedCatalogItem feed in _timelineCatalog.Feeds
                         .Where(item => categoryId is null || item.CategoryId == categoryId)
                         .OrderBy(item => item.SortOrder)
                         .ThenBy(item => item.DisplayName, StringComparer.CurrentCulture)
                         .ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                TimelineFeeds.Add(new(feed.Id, feed.DisplayName, feed.CategoryId));
            }
        }
        SelectedTimelineFeed = TimelineFeeds[0];
    }

    private void OnTimelineCatalogSyncStatusChanged(
        object? sender,
        FeedCatalogSyncStatusChangedEventArgs eventArgs)
    {
        if (_timelineDisposed) return;
        if (_timelineSynchronizationContext is not null
            && SynchronizationContext.Current != _timelineSynchronizationContext)
        {
            _timelineSynchronizationContext.Post(
                static state =>
                {
                    var (viewModel, status) =
                        ((NewsCenterViewModel, FeedCatalogSyncStatus))state!;
                    viewModel.StartTimelineCatalogSyncRefresh(status);
                },
                (this, eventArgs.Status));
            return;
        }

        StartTimelineCatalogSyncRefresh(eventArgs.Status);
    }

    private void StartTimelineCatalogSyncRefresh(FeedCatalogSyncStatus status) =>
        _ = RefreshTimelineCatalogFromSyncAsync(status);

    private async Task RefreshTimelineCatalogFromSyncAsync(FeedCatalogSyncStatus status)
    {
        try
        {
            if (_timelineDisposed) return;
            UpdateTimelineStatus(status);
            long loadedVersion = _timelineCatalog?.State.Version ?? 0;
            if (!status.IsSynchronizing && status.Version > loadedVersion)
            {
                await ReloadTimelineCatalogAsync(
                    preserveSelection: true,
                    CancellationToken.None);
            }
        }
        catch (Exception) when (!_timelineDisposed)
        {
            TimelineStatus = "目录已更新，但重新读取本地缓存失败。";
        }
    }

    private void UpdateTimelineStatus(FeedCatalogSyncStatus sync)
    {
        string cacheKind = sync.Error is not null || sync.IsStale
            ? "离线缓存"
            : "本地缓存";
        string refresh = _lastTimelineRefreshAt is null
            ? "暂无本地 Feed 条目"
            : $"最后抓取 {_lastTimelineRefreshAt.Value.ToLocalTime():MM-dd HH:mm}";
        DateTimeOffset? synchronizedAt = sync.LastSynchronizedAt ?? _catalogLastSynchronizedAt;
        string catalog = synchronizedAt is null
            ? "目录尚未同步"
            : $"目录同步 {synchronizedAt.Value.ToLocalTime():MM-dd HH:mm}";
        TimelineStatus = $"{cacheKind} · {refresh} · {catalog}";
    }

    private void DisposeTimeline()
    {
        _timelineDisposed = true;
        Interlocked.Increment(ref _timelineCatalogGeneration);
        Interlocked.Increment(ref _timelineQueryGeneration);
        _feedCatalogSync.StatusChanged -= OnTimelineCatalogSyncStatusChanged;
        ApplyTimelineFiltersCommand.Dispose();
        LoadMoreTimelineCommand.Dispose();
        ClearTimelineFiltersCommand.Dispose();
    }

    private static DateTimeOffset ToTimelineBoundary(DateTime value)
    {
        DateTime local = DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
}
