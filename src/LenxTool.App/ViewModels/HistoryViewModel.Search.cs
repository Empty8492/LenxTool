using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class HistoryViewModel
{
    private const int SearchPageSize = 50;
    private FeedCatalogSnapshot? _searchCatalog;
    private HistorySearchTypeOption _selectedSearchType = null!;
    private HistorySearchFilterOption _selectedSearchCategory = null!;
    private HistorySearchFilterOption _selectedSearchFeed = null!;
    private HistorySearchFilterOption _selectedSearchTag = null!;
    private DateTime? _searchPublishedFrom;
    private DateTime? _searchPublishedBefore;
    private bool _searchFavoritesOnly;
    private bool _hasMoreSearchResults;
    private bool _isSearchBusy;
    private int _searchOffset;
    private int _selectedHistoryTabIndex;

    public ObservableCollection<HistorySearchTypeOption>
        SearchTypeOptions
    { get; } = [];
    public ObservableCollection<HistorySearchFilterOption>
        SearchCategories
    { get; } = [];
    public ObservableCollection<HistorySearchFilterOption>
        SearchFeeds
    { get; } = [];
    public ObservableCollection<HistorySearchFilterOption>
        SearchTags
    { get; } = [];

    public AsyncRelayCommand LoadMoreSearchResultsCommand { get; private set; } =
        null!;
    public RelayCommand ClearSearchFiltersCommand { get; private set; } = null!;

    public HistorySearchTypeOption SelectedSearchType
    {
        get => _selectedSearchType;
        set
        {
            HistorySearchTypeOption normalized =
                value ?? SearchTypeOptions[0];
            if (!SetProperty(ref _selectedSearchType, normalized))
            {
                return;
            }
            if (normalized.Value is not null
                and not ContentSearchResultType.FeedEntry)
            {
                SelectedSearchCategory = SearchCategories[0];
            }
            InvalidateSearchPaging();
        }
    }

    public HistorySearchFilterOption SelectedSearchCategory
    {
        get => _selectedSearchCategory;
        set
        {
            HistorySearchFilterOption normalized =
                value ?? SearchCategories[0];
            if (!SetProperty(ref _selectedSearchCategory, normalized))
            {
                return;
            }
            RebuildSearchFeeds();
            InvalidateSearchPaging();
        }
    }

    public HistorySearchFilterOption SelectedSearchFeed
    {
        get => _selectedSearchFeed;
        set
        {
            if (SetProperty(
                    ref _selectedSearchFeed,
                    value ?? SearchFeeds[0]))
            {
                InvalidateSearchPaging();
            }
        }
    }

    public HistorySearchFilterOption SelectedSearchTag
    {
        get => _selectedSearchTag;
        set
        {
            if (SetProperty(
                    ref _selectedSearchTag,
                    value ?? SearchTags[0]))
            {
                InvalidateSearchPaging();
            }
        }
    }

    public DateTime? SearchPublishedFrom
    {
        get => _searchPublishedFrom;
        set
        {
            if (SetProperty(ref _searchPublishedFrom, value?.Date))
            {
                InvalidateSearchPaging();
            }
        }
    }

    public DateTime? SearchPublishedBefore
    {
        get => _searchPublishedBefore;
        set
        {
            if (SetProperty(ref _searchPublishedBefore, value?.Date))
            {
                InvalidateSearchPaging();
            }
        }
    }

    public bool SearchFavoritesOnly
    {
        get => _searchFavoritesOnly;
        set
        {
            if (SetProperty(ref _searchFavoritesOnly, value))
            {
                InvalidateSearchPaging();
            }
        }
    }

    public bool HasMoreSearchResults
    {
        get => _hasMoreSearchResults;
        private set
        {
            if (SetProperty(ref _hasMoreSearchResults, value))
            {
                LoadMoreSearchResultsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public int SelectedHistoryTabIndex
    {
        get => _selectedHistoryTabIndex;
        set => SetProperty(
            ref _selectedHistoryTabIndex,
            Math.Clamp(value, 0, 1));
    }

    public string OpenSearchResultLabel =>
        SelectedSearchResult?.Type switch
        {
            ContentSearchResultType.FeedEntry =>
                "在资讯阅读器中打开",
            ContentSearchResultType.Subtitle =>
                "查看字幕任务",
            _ when SelectedSearchResult?.Url is not null =>
                "打开来源链接",
            _ => "无可打开内容"
        };

    private void ConfigureSearch()
    {
        SearchTypeOptions.Add(new(null, "全部类型"));
        foreach (ContentSearchResultType type
                 in Enum.GetValues<ContentSearchResultType>())
        {
            SearchTypeOptions.Add(new(type, SearchTypeLabel(type)));
        }
        SearchCategories.Add(new(null, "全部分类"));
        SearchFeeds.Add(new(null, "全部 Feed"));
        SearchTags.Add(new(null, "全部标签"));
        _selectedSearchType = SearchTypeOptions[0];
        _selectedSearchCategory = SearchCategories[0];
        _selectedSearchFeed = SearchFeeds[0];
        _selectedSearchTag = SearchTags[0];
        LoadMoreSearchResultsCommand = new(
            LoadMoreSearchResultsAsync,
            () => CanStartSearch() && HasMoreSearchResults);
        ClearSearchFiltersCommand = new(
            ClearSearchFilters,
            () => !_isSearchBusy);
    }

    private async Task InitializeSearchFiltersAsync(
        CancellationToken cancellationToken)
    {
        if (_searchCatalogRepository is not null)
        {
            _searchCatalog = await _searchCatalogRepository
                .GetCatalogAsync(
                    FeedCatalogScope.Active,
                    cancellationToken);
        }
        IReadOnlyList<TagItem> tags =
            await _favorites.GetTagsAsync(cancellationToken);
        string? selectedCategoryId = SelectedSearchCategory.Id;
        string? selectedTagId = SelectedSearchTag.Id;
        SearchCategories.Clear();
        SearchCategories.Add(new(null, "全部分类"));
        foreach (FeedCategory category in
                 _searchCatalog?.Categories
                     .Where(item => item.IsEnabled)
                     .OrderBy(item => item.SortOrder)
                     .ThenBy(item => item.Name, StringComparer.CurrentCulture)
                 ?? Enumerable.Empty<FeedCategory>())
        {
            SearchCategories.Add(new(category.Id, category.Name));
        }
        SelectedSearchCategory = SearchCategories.FirstOrDefault(
                item => string.Equals(
                    item.Id,
                    selectedCategoryId,
                    StringComparison.Ordinal))
            ?? SearchCategories[0];
        SearchTags.Clear();
        SearchTags.Add(new(null, "全部标签"));
        foreach (TagItem tag in tags)
        {
            SearchTags.Add(new(tag.Id, tag.Name));
        }
        SelectedSearchTag = SearchTags.FirstOrDefault(
                item => string.Equals(
                    item.Id,
                    selectedTagId,
                    StringComparison.Ordinal))
            ?? SearchTags[0];
    }

    private void RebuildSearchFeeds()
    {
        string? selectedFeedId = _selectedSearchFeed?.Id;
        SearchFeeds.Clear();
        SearchFeeds.Add(new(null, "全部 Feed"));
        foreach (FeedCatalogItem feed in
                 _searchCatalog?.Feeds
                     .Where(item =>
                         item.IsEnabled
                         && (SelectedSearchCategory.Id is null
                             || string.Equals(
                                 item.CategoryId,
                                 SelectedSearchCategory.Id,
                                 StringComparison.Ordinal)))
                     .OrderBy(item => item.SortOrder)
                     .ThenBy(
                         item => item.DisplayName,
                         StringComparer.CurrentCulture)
                 ?? Enumerable.Empty<FeedCatalogItem>())
        {
            SearchFeeds.Add(new(
                feed.Id,
                feed.DisplayName,
                feed.CategoryId));
        }
        _selectedSearchFeed = SearchFeeds.FirstOrDefault(
                item => string.Equals(
                    item.Id,
                    selectedFeedId,
                    StringComparison.Ordinal))
            ?? SearchFeeds[0];
        OnPropertyChanged(nameof(SelectedSearchFeed));
    }

    private async Task SearchPageAsync(
        bool reset,
        CancellationToken cancellationToken)
    {
        SetSearchBusy(true);
        try
        {
            int offset = reset ? 0 : _searchOffset;
            ContentSearchQuery query = CreateSearchQuery(offset);
            ContentSearchPage page = await _news.SearchContentAsync(
                query,
                cancellationToken);
            if (reset)
            {
                SearchResults.Clear();
                SelectedSearchResult = null;
            }
            HashSet<string> identities = SearchResults
                .Select(NormalizeSearchIdentity)
                .ToHashSet(StringComparer.Ordinal);
            foreach (ContentSearchResult result in page.Items)
            {
                if (identities.Add(NormalizeSearchIdentity(result)))
                {
                    SearchResults.Add(result);
                }
            }
            _searchOffset = checked(offset + page.Items.Count);
            HasMoreSearchResults = page.HasMore;
            if (reset)
            {
                SelectedSearchResult = SearchResults.FirstOrDefault();
            }
            SearchStatus = SearchResults.Count == 0
                ? "没有找到相关内容；请调整关键词或筛选条件。"
                : HasMoreSearchResults
                    ? $"已显示 {SearchResults.Count} 条相关内容，可继续加载。"
                    : $"找到 {SearchResults.Count} 条相关内容。";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SearchStatus = "本地搜索暂时失败；现有结果未被删除。";
        }
        finally
        {
            SetSearchBusy(false);
        }
    }

    private Task LoadMoreSearchResultsAsync(
        CancellationToken cancellationToken) =>
        SearchPageAsync(reset: false, cancellationToken);

    private ContentSearchQuery CreateSearchQuery(int offset)
    {
        DateTimeOffset? from = SearchPublishedFrom is { } fromDate
            ? ToLocalDateBoundary(fromDate)
            : null;
        DateTimeOffset? before = SearchPublishedBefore is { } beforeDate
            ? ToLocalDateBoundary(beforeDate.AddDays(1))
            : null;
        return new(
            SearchQuery.Trim(),
            SelectedSearchType.Value,
            from,
            before,
            SelectedSearchFeed.Id,
            SelectedSearchCategory.Id,
            SelectedSearchTag.Id,
            SearchFavoritesOnly,
            offset,
            SearchPageSize);
    }

    private void ClearSearchFilters()
    {
        SelectedSearchType = SearchTypeOptions[0];
        SelectedSearchCategory = SearchCategories[0];
        SelectedSearchTag = SearchTags[0];
        SearchPublishedFrom = null;
        SearchPublishedBefore = null;
        SearchFavoritesOnly = false;
        SearchStatus = "筛选已清除；输入关键词后重新搜索。";
    }

    private bool CanStartSearch() =>
        !_isSearchBusy
        && !string.IsNullOrWhiteSpace(SearchQuery)
        && (SearchPublishedFrom is null
            || SearchPublishedBefore is null
            || SearchPublishedFrom.Value.Date
                <= SearchPublishedBefore.Value.Date);

    private void SetSearchBusy(bool value)
    {
        if (_isSearchBusy == value)
        {
            return;
        }
        _isSearchBusy = value;
        NotifySearchCommands();
    }

    private void NotifySearchCommands()
    {
        SearchCommand.NotifyCanExecuteChanged();
        LoadMoreSearchResultsCommand.NotifyCanExecuteChanged();
        ClearSearchFiltersCommand.NotifyCanExecuteChanged();
    }

    private void InvalidateSearchPaging()
    {
        _searchOffset = 0;
        HasMoreSearchResults = false;
        NotifySearchCommands();
    }

    private static DateTimeOffset ToLocalDateBoundary(DateTime value)
    {
        DateTime unspecified = DateTime.SpecifyKind(
            value.Date,
            DateTimeKind.Unspecified);
        return new(
            unspecified,
            TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }

    private static string SearchTypeLabel(
        ContentSearchResultType value) => value switch
        {
            ContentSearchResultType.News => "早报",
            ContentSearchResultType.Trend => "热点",
            ContentSearchResultType.AiReport => "AI 报告",
            ContentSearchResultType.FeedEntry => "订阅条目",
            ContentSearchResultType.Subtitle => "字幕",
            ContentSearchResultType.Tag => "标签",
            ContentSearchResultType.Favorite => "收藏",
            _ => value.ToString()
        };
}
