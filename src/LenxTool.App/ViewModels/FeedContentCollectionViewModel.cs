using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record FeedContentItem(FeedTimelineItem Timeline)
{
    public FeedEntry Entry => Timeline.Entry;
    public string FeedName => Timeline.FeedName;
    public string CategoryName => Timeline.CategoryName;
    public string Title => Entry.Title;
    public string Summary => Timeline.Summary;
    public DateTimeOffset DisplayTime => Timeline.DisplayTime;
    public bool IsStarred => Timeline.IsStarred;
    public string? SafeOriginalUrl => TryGetSafeHttpUrl(Entry.NormalizedUrl);
    public string? PrimaryImageUrl => FindPrimaryImageUrl(Entry);

    private static string? FindPrimaryImageUrl(FeedEntry entry)
    {
        foreach (FeedEnclosure enclosure in entry.Enclosures)
        {
            FeedAttachmentClassification attachment =
                FeedAttachmentClassifier.Classify(enclosure, entry.NormalizedUrl);
            if (attachment.UrlStatus == FeedAttachmentUrlStatus.Allowed
                && attachment.IsTypeVerified
                && attachment.Kind == FeedAttachmentKind.Image)
            {
                return attachment.SafeUrl;
            }
        }
        return null;
    }

    private static string? TryGetSafeHttpUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        return uri.AbsoluteUri;
    }
}

public sealed record FeedContentRow(IReadOnlyList<FeedContentItem> Items);

public sealed class FeedContentCollectionViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 50;
    private const string FavoriteEntityType = "feed_entry";
    private const string LocalProfile = "default";
    private readonly IFeedEntryRepository _entries;
    private readonly IFeedCatalogRepository _catalogRepository;
    private readonly IEntryStateRepository _states;
    private readonly IFavoriteRepository _favorites;
    private readonly Action<string> _openUri;
    private FeedCatalogSnapshot? _catalog;
    private FeedTimelineFilterOption? _selectedCategory;
    private FeedTimelineFilterOption? _selectedFeed;
    private FeedContentItem? _selectedItem;
    private DateTime? _selectedDate;
    private bool _favoritesOnly;
    private bool _hasMore;
    private int _nextOffset;
    private string _status;
    private int _queryGeneration;
    private CancellationTokenSource? _queryCancellation;
    private FeedContentFilterSnapshot? _appliedFilters;
    private bool _disposed;

    public FeedContentCollectionViewModel(
        EntryViewKind viewKind,
        string title,
        IFeedEntryRepository entries,
        IFeedCatalogRepository catalogRepository,
        IEntryStateRepository states,
        IFavoriteRepository favorites,
        Action<string> openUri)
    {
        if (!Enum.IsDefined(viewKind))
        {
            throw new ArgumentOutOfRangeException(nameof(viewKind));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(catalogRepository);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(favorites);
        ArgumentNullException.ThrowIfNull(openUri);
        ViewKind = viewKind;
        Title = title;
        _entries = entries;
        _catalogRepository = catalogRepository;
        _states = states;
        _favorites = favorites;
        _openUri = openUri;
        _status = $"正在读取本地{title}缓存…";
        ApplyFiltersCommand = new(ReloadAsync);
        LoadMoreCommand = new(LoadMoreAsync, () => HasMore);
        ClearFiltersCommand = new(ClearFiltersAsync);
        OpenItemCommand = new(
            OpenItem,
            item => item?.SafeOriginalUrl is not null);
    }

    public EntryViewKind ViewKind { get; }
    public string Title { get; }
    public ObservableCollection<FeedContentItem> Items { get; } = [];
    public ObservableCollection<FeedContentRow> Rows { get; } = [];
    public ObservableCollection<FeedTimelineFilterOption> Categories { get; } = [];
    public ObservableCollection<FeedTimelineFilterOption> Feeds { get; } = [];
    public AsyncRelayCommand ApplyFiltersCommand { get; }
    public AsyncRelayCommand LoadMoreCommand { get; }
    public AsyncRelayCommand ClearFiltersCommand { get; }
    public RelayCommand<FeedContentItem> OpenItemCommand { get; }

    public FeedTimelineFilterOption? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value)) return;
            RebuildFeedChoices();
        }
    }

    public FeedTimelineFilterOption? SelectedFeed
    {
        get => _selectedFeed;
        set => SetProperty(ref _selectedFeed, value);
    }

    public FeedContentItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OpenItemCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set => SetProperty(ref _selectedDate, value?.Date);
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set => SetProperty(ref _favoritesOnly, value);
    }

    public bool HasMore
    {
        get => _hasMore;
        private set
        {
            if (SetProperty(ref _hasMore, value))
            {
                LoadMoreCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public int NextOffset
    {
        get => _nextOffset;
        private set => SetProperty(ref _nextOffset, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await RefreshCatalogAsync(preserveFilters: false, cancellationToken);
    }

    internal void ReportLoadFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Status = $"{Title}流加载失败；时间线仍可使用，重新进入此页可重试。";
    }

    public async Task RefreshCatalogAsync(
        bool preserveFilters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? selectedCategoryId = preserveFilters ? SelectedCategory?.Id : null;
        string? selectedFeedId = preserveFilters ? SelectedFeed?.Id : null;
        _catalog = await _catalogRepository.GetCatalogAsync(
            FeedCatalogScope.Active,
            cancellationToken);
        RebuildCategoryChoices();
        if (selectedCategoryId is not null)
        {
            SelectedCategory = Categories.FirstOrDefault(
                option => string.Equals(option.Id, selectedCategoryId, StringComparison.Ordinal))
                ?? Categories[0];
        }
        if (selectedFeedId is not null)
        {
            SelectedFeed = Feeds.FirstOrDefault(
                option => string.Equals(option.Id, selectedFeedId, StringComparison.Ordinal))
                ?? Feeds[0];
        }
        await ReloadAsync(cancellationToken);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LoadMoreCommand.Cancel();
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        CancellationToken queryToken = _queryCancellation.Token;
        int generation = Interlocked.Increment(ref _queryGeneration);
        FeedContentFilterSnapshot appliedFilters = CaptureFilters();
        _appliedFilters = appliedFilters;
        Status = $"正在筛选本地{Title}缓存…";
        try
        {
            FeedEntryPage page = await _entries.QueryAsync(
                CreateQuery(offset: 0, appliedFilters),
                queryToken);
            if (!IsCurrent(generation)) return;
            Items.Clear();
            Rows.Clear();
            await AppendPageAsync(page, generation, queryToken);
            if (!IsCurrent(generation)) return;
            SelectedItem = Items.FirstOrDefault();
        }
        catch (OperationCanceledException) when (queryToken.IsCancellationRequested)
        {
        }
    }

    private async Task LoadMoreAsync(CancellationToken cancellationToken)
    {
        if (!HasMore) return;
        FeedContentFilterSnapshot? appliedFilters = _appliedFilters;
        if (appliedFilters is null) return;
        int generation = Volatile.Read(ref _queryGeneration);
        int expectedCount = Items.Count;
        int expectedOffset = NextOffset;
        FeedEntryPage page = await _entries.QueryAsync(
            CreateQuery(expectedOffset, appliedFilters),
            cancellationToken);
        if (!IsCurrent(generation)
            || expectedCount != Items.Count
            || expectedOffset != NextOffset)
        {
            return;
        }
        await AppendPageAsync(page, generation, cancellationToken);
    }

    private async Task ClearFiltersAsync(CancellationToken cancellationToken)
    {
        SelectedCategory = Categories.FirstOrDefault();
        SelectedFeed = Feeds.FirstOrDefault();
        SelectedDate = null;
        FavoritesOnly = false;
        await ReloadAsync(cancellationToken);
    }

    private async Task AppendPageAsync(
        FeedEntryPage page,
        int expectedGeneration,
        CancellationToken cancellationToken)
    {
        string[] entryIds = page.Items.Select(entry => entry.Id).ToArray();
        Task<IReadOnlyDictionary<string, EntryState>> stateTask =
            entryIds.Length == 0
                ? Task.FromResult<IReadOnlyDictionary<string, EntryState>>(
                    new Dictionary<string, EntryState>())
                : _states.GetAsync(entryIds, LocalProfile, cancellationToken);
        Task<IReadOnlyDictionary<string, FavoriteItem>> favoriteTask =
            entryIds.Length == 0
                ? Task.FromResult<IReadOnlyDictionary<string, FavoriteItem>>(
                    new Dictionary<string, FavoriteItem>())
                : _favorites.GetForEntitiesAsync(
                    FavoriteEntityType,
                    entryIds,
                    cancellationToken);
        await Task.WhenAll(stateTask, favoriteTask);
        if (!IsCurrent(expectedGeneration)) return;

        IReadOnlyDictionary<string, EntryState> states = await stateTask;
        IReadOnlyDictionary<string, FavoriteItem> favorites = await favoriteTask;
        HashSet<string> existingIds = Items
            .Select(item => item.Entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        int previousItemCount = Items.Count;
        foreach (FeedEntry entry in page.Items)
        {
            if (!existingIds.Add(entry.Id)) continue;
            Items.Add(new(CreateTimelineItem(
                entry,
                states.GetValueOrDefault(entry.Id),
                favorites.GetValueOrDefault(entry.Id))));
        }
        NextOffset = page.NextOffset
            ?? checked(page.Offset + page.Items.Count);
        HasMore = page.HasMore;
        UpdateRows(previousItemCount);
        Status = HasMore
            ? $"已加载 {Items.Count} 条{Title} · 向下滚动继续"
            : Items.Count == 0
                ? $"当前筛选下没有{Title}"
                : $"已加载 {Items.Count} 条{Title} · 已到末尾";
    }

    private FeedContentFilterSnapshot CaptureFilters() => new(
        SelectedFeed?.Id,
        SelectedCategory?.Id,
        SelectedDate,
        FavoritesOnly);

    private FeedEntryQuery CreateQuery(
        int offset,
        FeedContentFilterSnapshot filters)
    {
        DateTimeOffset? publishedFrom = filters.PublishedDate is null
            ? null
            : ToBoundary(filters.PublishedDate.Value);
        DateTimeOffset? publishedBefore = filters.PublishedDate is null
            ? null
            : ToBoundary(filters.PublishedDate.Value.AddDays(1));
        return new(
            SearchText: null,
            FeedId: filters.FeedId,
            CategoryId: filters.CategoryId,
            PublishedFrom: publishedFrom,
            PublishedBefore: publishedBefore,
            ReadFilter: FeedEntryReadFilter.All,
            Offset: offset,
            Limit: PageSize,
            ActiveOnly: true,
            FavoritesOnly: filters.FavoritesOnly,
            LocalProfile: LocalProfile,
            ViewKind: ViewKind);
    }

    private FeedTimelineItem CreateTimelineItem(
        FeedEntry entry,
        EntryState? state,
        FavoriteItem? favorite)
    {
        FeedCatalogItem? feed = _catalog?.Feeds.FirstOrDefault(
            item => string.Equals(item.Id, entry.FeedId, StringComparison.Ordinal));
        FeedCategory? category = feed?.CategoryId is null
            ? null
            : _catalog?.Categories.FirstOrDefault(
                item => string.Equals(item.Id, feed.CategoryId, StringComparison.Ordinal));
        return new(
            entry,
            feed?.DisplayName ?? "已移除 Feed",
            category?.Name ?? "未分类",
            state,
            favorite);
    }

    private void RebuildCategoryChoices()
    {
        Categories.Clear();
        Categories.Add(new(null, "全部分类"));
        if (_catalog is not null)
        {
            foreach (FeedCategory category in _catalog.Categories
                         .Where(item => item.IsEnabled)
                         .OrderBy(item => item.SortOrder)
                         .ThenBy(item => item.Name, StringComparer.CurrentCulture))
            {
                Categories.Add(new(category.Id, category.Name));
            }
        }
        SelectedCategory = Categories[0];
    }

    private void RebuildFeedChoices()
    {
        string? categoryId = SelectedCategory?.Id;
        Feeds.Clear();
        Feeds.Add(new(null, "全部 Feed", categoryId));
        if (_catalog is not null)
        {
            foreach (FeedCatalogItem feed in _catalog.Feeds
                         .Where(item => item.IsEnabled
                             && (categoryId is null || item.CategoryId == categoryId))
                         .OrderBy(item => item.SortOrder)
                         .ThenBy(item => item.DisplayName, StringComparer.CurrentCulture))
            {
                Feeds.Add(new(feed.Id, feed.DisplayName, feed.CategoryId));
            }
        }
        SelectedFeed = Feeds[0];
    }

    private void UpdateRows(int previousItemCount)
    {
        int firstChangedRow = previousItemCount / 3;
        while (Rows.Count > firstChangedRow)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
        for (int index = firstChangedRow * 3; index < Items.Count; index += 3)
        {
            Rows.Add(new(Items.Skip(index).Take(3).ToArray()));
        }
    }

    private void OpenItem(FeedContentItem? item)
    {
        if (item?.SafeOriginalUrl is string url)
        {
            _openUri(url);
        }
    }

    private bool IsCurrent(int generation) =>
        !_disposed && generation == Volatile.Read(ref _queryGeneration);

    private static DateTimeOffset ToBoundary(DateTime value)
    {
        DateTime local = DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(
            local,
            TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _queryGeneration);
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        ApplyFiltersCommand.Dispose();
        LoadMoreCommand.Dispose();
        ClearFiltersCommand.Dispose();
    }

    private sealed record FeedContentFilterSnapshot(
        string? FeedId,
        string? CategoryId,
        DateTime? PublishedDate,
        bool FavoritesOnly);
}
