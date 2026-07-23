using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class NewsCenterViewModel
{
    private const int TimelinePageSize = 50;
    private const int MaximumTimelineKeywordLength = 200;
    private const int MaximumTimelineNoteLength = 4000;
    private const int MaximumTimelineTagLength = 80;
    private const string FeedEntryFavoriteType = "feed_entry";
    private const string DefaultTimelineTagColor = "#4B6B88";
    private const string DefaultTimelineProfile = "default";
    private readonly IFeedEntryRepository _feedEntryRepository;
    private readonly IFeedCatalogRepository _feedCatalogRepository;
    private readonly IFeedCatalogSyncService _feedCatalogSync;
    private readonly IEntryStateRepository _entryStateRepository;
    private readonly IFavoriteRepository _favoriteRepository;
    private readonly SynchronizationContext? _timelineSynchronizationContext;
    private FeedCatalogSnapshot? _timelineCatalog;
    private FeedTimelineFilterOption? _selectedTimelineCategory;
    private FeedTimelineFilterOption? _selectedTimelineFeed;
    private FeedTimelineReadFilterOption? _selectedTimelineReadFilter;
    private FeedTimelineFilterOption? _selectedTimelineTag;
    private FeedTimelineItem? _selectedTimelineEntry;
    private NewsArticle? _selectedFeedArticle;
    private DateTime? _selectedTimelineDate;
    private DateTimeOffset? _lastTimelineRefreshAt;
    private DateTimeOffset? _catalogLastSynchronizedAt;
    private string _timelineKeyword = string.Empty;
    private string _timelineStatus = "正在读取本地 Feed 缓存…";
    private string _selectedTimelineNote = string.Empty;
    private string _timelineTagInput = string.Empty;
    private string _timelineEditorStatus = "收藏、备注和标签仅保存在本机。";
    private Task _selectedTimelineEditorLoad = Task.CompletedTask;
    private bool _hasMoreTimelineEntries;
    private bool _timelineFavoritesOnly;
    private bool _timelineDisposed;
    private int _timelineCatalogGeneration;
    private int _timelineQueryGeneration;
    private int _timelineEditorGeneration;
    private int _timelineNextOffset;

    public ObservableCollection<FeedTimelineItem> TimelineEntries { get; } = [];
    public ObservableCollection<FeedTimelineFilterOption> TimelineCategories { get; } = [];
    public ObservableCollection<FeedTimelineFilterOption> TimelineFeeds { get; } = [];
    public ObservableCollection<FeedTimelineReadFilterOption> TimelineReadFilters { get; } =
    [
        new(FeedEntryReadFilter.All, "全部"),
        new(FeedEntryReadFilter.Unread, "未读"),
        new(FeedEntryReadFilter.Read, "已读")
    ];
    public ObservableCollection<FeedTimelineFilterOption> TimelineTags { get; } = [];
    public ObservableCollection<TagItem> SelectedTimelineTags { get; } = [];
    public AsyncRelayCommand ApplyTimelineFiltersCommand { get; private set; } = null!;
    public AsyncRelayCommand LoadMoreTimelineCommand { get; private set; } = null!;
    public AsyncRelayCommand ClearTimelineFiltersCommand { get; private set; } = null!;
    public AsyncRelayCommand<FeedTimelineItem> ToggleTimelineReadCommand { get; private set; } = null!;
    public AsyncRelayCommand<FeedTimelineItem> ToggleTimelineStarCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveTimelineNoteCommand { get; private set; } = null!;
    public AsyncRelayCommand AddTimelineTagCommand { get; private set; } = null!;
    public AsyncRelayCommand<TagItem> RemoveTimelineTagCommand { get; private set; } = null!;

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
                SelectedTimelineNote = value?.Note ?? string.Empty;
                TimelineTagInput = string.Empty;
                SelectedTimelineTags.Clear();
                int generation = Interlocked.Increment(ref _timelineEditorGeneration);
                _selectedTimelineEditorLoad = LoadSelectedTimelineEditorAsync(value, generation);
                OnPropertyChanged(nameof(SelectedTimelineEditorLoad));
                SaveTimelineNoteCommand.NotifyCanExecuteChanged();
                AddTimelineTagCommand.NotifyCanExecuteChanged();
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

    public FeedTimelineReadFilterOption? SelectedTimelineReadFilter
    {
        get => _selectedTimelineReadFilter;
        set => SetProperty(ref _selectedTimelineReadFilter, value);
    }

    public bool TimelineFavoritesOnly
    {
        get => _timelineFavoritesOnly;
        set => SetProperty(ref _timelineFavoritesOnly, value);
    }

    public FeedTimelineFilterOption? SelectedTimelineTag
    {
        get => _selectedTimelineTag;
        set => SetProperty(ref _selectedTimelineTag, value);
    }

    public string SelectedTimelineNote
    {
        get => _selectedTimelineNote;
        set
        {
            string normalized = value ?? string.Empty;
            if (normalized.Length > MaximumTimelineNoteLength)
            {
                normalized = normalized[..MaximumTimelineNoteLength];
            }
            SetProperty(ref _selectedTimelineNote, normalized);
        }
    }

    public string TimelineTagInput
    {
        get => _timelineTagInput;
        set
        {
            string normalized = value ?? string.Empty;
            if (normalized.Length > MaximumTimelineTagLength)
            {
                normalized = normalized[..MaximumTimelineTagLength];
            }
            if (SetProperty(ref _timelineTagInput, normalized))
            {
                AddTimelineTagCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string TimelineEditorStatus
    {
        get => _timelineEditorStatus;
        private set => SetProperty(ref _timelineEditorStatus, value);
    }

    public Task SelectedTimelineEditorLoad => _selectedTimelineEditorLoad;

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
        _selectedTimelineReadFilter = TimelineReadFilters[0];
        ApplyTimelineFiltersCommand = new(ApplyTimelineFiltersAsync);
        LoadMoreTimelineCommand = new(
            LoadMoreTimelineAsync,
            () => HasMoreTimelineEntries);
        ClearTimelineFiltersCommand = new(ClearTimelineFiltersAsync);
        ToggleTimelineReadCommand = new(ToggleTimelineReadAsync, item => item is not null);
        ToggleTimelineStarCommand = new(ToggleTimelineStarAsync, item => item is not null);
        SaveTimelineNoteCommand = new(
            SaveTimelineNoteAsync,
            () => SelectedTimelineEntry is not null);
        AddTimelineTagCommand = new(
            AddTimelineTagAsync,
            () => SelectedTimelineEntry is not null
                  && !string.IsNullOrWhiteSpace(TimelineTagInput));
        RemoveTimelineTagCommand = new(
            RemoveTimelineTagAsync,
            tag => SelectedTimelineEntry is not null && tag is not null);
        _feedCatalogSync.StatusChanged += OnTimelineCatalogSyncStatusChanged;
    }

    private async Task InitializeTimelineAsync(CancellationToken cancellationToken)
    {
        await ReloadTimelineTagChoicesAsync(cancellationToken);
        await ReloadTimelineCatalogAsync(preserveSelection: false, cancellationToken);
    }

    private async Task ReloadTimelineTagChoicesAsync(CancellationToken cancellationToken)
    {
        string? selectedTagId = SelectedTimelineTag?.Id;
        IReadOnlyList<TagItem> tags;
        try
        {
            tags = await _favoriteRepository.GetTagsAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            tags = [];
        }

        TimelineTags.Clear();
        TimelineTags.Add(new(null, "全部标签"));
        foreach (TagItem tag in tags)
        {
            TimelineTags.Add(new(tag.Id, tag.Name));
        }
        SelectedTimelineTag = TimelineTags.FirstOrDefault(
            option => string.Equals(option.Id, selectedTagId, StringComparison.Ordinal))
            ?? TimelineTags[0];
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
        await AppendTimelinePageAsync(
            page,
            generation,
            cancellationToken: cancellationToken);
        if (_timelineDisposed
            || generation != Volatile.Read(ref _timelineQueryGeneration))
        {
            return;
        }
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

        await AppendTimelinePageAsync(
            page,
            generation,
            expectedVisibleCount,
            expectedOffset,
            cancellationToken);
        OnPropertyChanged(nameof(TimelineEntrySummary));
    }

    private async Task ClearTimelineFiltersAsync(CancellationToken cancellationToken)
    {
        SelectedTimelineCategory = TimelineCategories.FirstOrDefault();
        SelectedTimelineFeed = TimelineFeeds.FirstOrDefault();
        SelectedTimelineReadFilter = TimelineReadFilters[0];
        TimelineFavoritesOnly = false;
        SelectedTimelineTag = TimelineTags.FirstOrDefault();
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
            SelectedTimelineReadFilter?.Value ?? FeedEntryReadFilter.All,
            offset,
            TimelinePageSize,
            FavoritesOnly: TimelineFavoritesOnly,
            TagId: SelectedTimelineTag?.Id,
            LocalProfile: DefaultTimelineProfile);
    }

    private async Task AppendTimelinePageAsync(
        FeedEntryPage page,
        int expectedGeneration,
        int? expectedVisibleCount = null,
        int? expectedOffset = null,
        CancellationToken cancellationToken = default)
    {
        HashSet<string> existingIds = TimelineEntries
            .Select(item => item.Entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyDictionary<string, EntryState> states = page.Items.Count == 0
            ? new Dictionary<string, EntryState>(StringComparer.Ordinal)
            : await _entryStateRepository.GetAsync(
                page.Items.Select(item => item.Id).ToArray(),
                DefaultTimelineProfile,
                cancellationToken);
        IReadOnlyDictionary<string, FavoriteItem> favorites = page.Items.Count == 0
            ? new Dictionary<string, FavoriteItem>(StringComparer.Ordinal)
            : await _favoriteRepository.GetForEntitiesAsync(
                FeedEntryFavoriteType,
                page.Items.Select(item => item.Id).ToArray(),
                cancellationToken);
        if (_timelineDisposed
            || expectedGeneration != Volatile.Read(ref _timelineQueryGeneration)
            || (expectedVisibleCount is not null
                && expectedVisibleCount.Value != TimelineEntries.Count)
            || (expectedOffset is not null
                && expectedOffset.Value != _timelineNextOffset))
        {
            return;
        }
        _timelineNextOffset = checked(page.Offset + page.Items.Count);
        foreach (FeedEntry entry in page.Items)
        {
            if (existingIds.Add(entry.Id))
            {
                TimelineEntries.Add(CreateTimelineItem(
                    entry,
                    states.GetValueOrDefault(entry.Id),
                    favorites.GetValueOrDefault(entry.Id)));
            }

            if (_lastTimelineRefreshAt is null || entry.FetchedAt > _lastTimelineRefreshAt)
            {
                _lastTimelineRefreshAt = entry.FetchedAt;
            }
        }

        HasMoreTimelineEntries = page.HasMore;
        UpdateTimelineStatus(_feedCatalogSync.Current);
    }

    private async Task ToggleTimelineReadAsync(
        FeedTimelineItem? item,
        CancellationToken cancellationToken)
    {
        if (item is null) return;
        EntryState state = await _entryStateRepository.PatchAsync(
            item.Entry.Id,
            DefaultTimelineProfile,
            new EntryStatePatch(IsRead: !item.IsRead),
            cancellationToken);
        ReplaceTimelineItem(item, state);
    }

    private async Task ToggleTimelineStarAsync(
        FeedTimelineItem? item,
        CancellationToken cancellationToken)
    {
        if (item is null) return;
        try
        {
            bool isStarred = !item.IsStarred;
            bool favoriteChanged = false;
            FavoriteItem? favorite = item.Favorite;
            if (isStarred)
            {
                favorite = await _favoriteRepository.UpsertAsync(
                    FeedEntryFavoriteType,
                    item.Entry.Id,
                    item.Note,
                    cancellationToken);
                favoriteChanged = true;
            }
            else
            {
                await _favoriteRepository.RemoveAsync(
                    FeedEntryFavoriteType,
                    item.Entry.Id,
                    cancellationToken);
                favoriteChanged = true;
                favorite = null;
            }

            EntryState state;
            try
            {
                state = await _entryStateRepository.PatchAsync(
                    item.Entry.Id,
                    DefaultTimelineProfile,
                    new EntryStatePatch(
                        IsStarred: isStarred,
                        Note: isStarred ? null : item.Note),
                    cancellationToken);
            }
            catch
            {
                if (favoriteChanged)
                {
                    await RestoreTimelineFavoriteAsync(item);
                }
                throw;
            }
            ReplaceTimelineItem(item, state, favorite, replaceFavorite: true);
            SetTimelineEditorStatusIfSelected(
                item,
                isStarred
                    ? "已收藏到本机。"
                    : "已取消本机收藏；私人备注仍保留。");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SetTimelineEditorStatusIfSelected(
                item,
                "收藏状态保存失败，当前界面未更新。");
        }
    }

    private Task RestoreTimelineFavoriteAsync(FeedTimelineItem item) =>
        item.Favorite is null
            ? _favoriteRepository.RemoveAsync(
                FeedEntryFavoriteType,
                item.Entry.Id,
                CancellationToken.None)
            : RestoreExistingTimelineFavoriteAsync(item.Favorite);

    private async Task RestoreExistingTimelineFavoriteAsync(FavoriteItem favorite)
    {
        await _favoriteRepository.UpsertAsync(
            FeedEntryFavoriteType,
            favorite.EntityId,
            favorite.Note,
            CancellationToken.None);
    }

    private async Task LoadSelectedTimelineEditorAsync(
        FeedTimelineItem? item,
        int expectedGeneration)
    {
        if (item is null)
        {
            TimelineEditorStatus = "选择条目后可编辑本机收藏、备注和标签。";
            return;
        }

        try
        {
            IReadOnlyList<TagItem> tags = await _favoriteRepository.GetTagsForEntityAsync(
                FeedEntryFavoriteType,
                item.Entry.Id,
                CancellationToken.None);
            if (_timelineDisposed
                || expectedGeneration != Volatile.Read(ref _timelineEditorGeneration)
                || !string.Equals(
                    SelectedTimelineEntry?.Entry.Id,
                    item.Entry.Id,
                    StringComparison.Ordinal))
            {
                return;
            }

            SelectedTimelineTags.Clear();
            foreach (TagItem tag in tags)
            {
                SelectedTimelineTags.Add(tag);
            }
            TimelineEditorStatus = "收藏、备注和标签仅保存在本机。";
        }
        catch (Exception) when (!_timelineDisposed)
        {
            if (expectedGeneration == Volatile.Read(ref _timelineEditorGeneration))
            {
                TimelineEditorStatus = "标签读取失败；正文和已缓存状态仍可使用。";
            }
        }
    }

    private async Task SaveTimelineNoteAsync(CancellationToken cancellationToken)
    {
        FeedTimelineItem? item = SelectedTimelineEntry;
        if (item is null) return;
        string note = SelectedTimelineNote;
        int editorGeneration = Volatile.Read(ref _timelineEditorGeneration);
        try
        {
            bool favoriteChanged = false;
            FavoriteItem? favorite = item.Favorite;
            if (item.IsStarred)
            {
                favorite = await _favoriteRepository.UpsertAsync(
                    FeedEntryFavoriteType,
                    item.Entry.Id,
                    note,
                    cancellationToken);
                favoriteChanged = true;
            }
            EntryState state;
            try
            {
                state = await _entryStateRepository.PatchAsync(
                    item.Entry.Id,
                    DefaultTimelineProfile,
                    new EntryStatePatch(Note: note),
                    cancellationToken);
            }
            catch
            {
                if (favoriteChanged)
                {
                    await RestoreTimelineFavoriteAsync(item);
                }
                throw;
            }
            ReplaceTimelineItem(
                item,
                state,
                favorite,
                replaceFavorite: item.IsStarred);
            SetTimelineEditorStatusIfCurrent(
                item,
                editorGeneration,
                "私人备注已保存到本机。");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SetTimelineEditorStatusIfCurrent(
                item,
                editorGeneration,
                "私人备注保存失败，原有内容未从界面移除。");
        }
    }

    private async Task AddTimelineTagAsync(CancellationToken cancellationToken)
    {
        FeedTimelineItem? item = SelectedTimelineEntry;
        string name = TimelineTagInput.Trim();
        if (item is null || name.Length == 0) return;
        int editorGeneration = Volatile.Read(ref _timelineEditorGeneration);
        string[] existingTagIds = SelectedTimelineTags
            .Select(value => value.Id)
            .ToArray();
        try
        {
            TagItem tag = await _favoriteRepository.UpsertTagAsync(
                name,
                DefaultTimelineTagColor,
                cancellationToken);
            string[] tagIds = existingTagIds
                .Append(tag.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            await _favoriteRepository.SetTagsAsync(
                FeedEntryFavoriteType,
                item.Entry.Id,
                tagIds,
                cancellationToken);
            if (!IsCurrentTimelineEditor(item, editorGeneration))
            {
                return;
            }

            TagItem? existing = SelectedTimelineTags.FirstOrDefault(value => value.Id == tag.Id);
            if (existing is not null)
            {
                int index = SelectedTimelineTags.IndexOf(existing);
                SelectedTimelineTags[index] = tag;
            }
            else
            {
                SelectedTimelineTags.Add(tag);
            }
            UpsertTimelineTagChoice(tag);
            TimelineTagInput = string.Empty;
            TimelineEditorStatus = $"已添加标签“{tag.Name}”。";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SetTimelineEditorStatusIfCurrent(
                item,
                editorGeneration,
                "标签保存失败，现有标签未从界面移除。");
        }
    }

    private void UpsertTimelineTagChoice(TagItem tag)
    {
        FeedTimelineFilterOption? existing = TimelineTags.FirstOrDefault(
            option => string.Equals(option.Id, tag.Id, StringComparison.Ordinal));
        var updated = new FeedTimelineFilterOption(tag.Id, tag.Name);
        if (existing is not null)
        {
            int index = TimelineTags.IndexOf(existing);
            TimelineTags[index] = updated;
            if (ReferenceEquals(SelectedTimelineTag, existing))
            {
                SelectedTimelineTag = updated;
            }
            return;
        }

        int insertionIndex = 1;
        while (insertionIndex < TimelineTags.Count
               && string.Compare(
                   TimelineTags[insertionIndex].Label,
                   tag.Name,
                   StringComparison.OrdinalIgnoreCase) < 0)
        {
            insertionIndex++;
        }
        TimelineTags.Insert(insertionIndex, updated);
    }

    private async Task RemoveTimelineTagAsync(
        TagItem? tag,
        CancellationToken cancellationToken)
    {
        FeedTimelineItem? item = SelectedTimelineEntry;
        if (item is null || tag is null) return;
        int editorGeneration = Volatile.Read(ref _timelineEditorGeneration);
        try
        {
            string[] remaining = SelectedTimelineTags
                .Where(value => value.Id != tag.Id)
                .Select(value => value.Id)
                .ToArray();
            await _favoriteRepository.SetTagsAsync(
                FeedEntryFavoriteType,
                item.Entry.Id,
                remaining,
                cancellationToken);
            if (!IsCurrentTimelineEditor(item, editorGeneration))
            {
                return;
            }

            SelectedTimelineTags.Remove(tag);
            TimelineEditorStatus = $"已移除条目标签“{tag.Name}”。";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SetTimelineEditorStatusIfCurrent(
                item,
                editorGeneration,
                "标签移除失败，现有标签保持不变。");
        }
    }

    private void ReplaceTimelineItem(
        FeedTimelineItem item,
        EntryState state,
        FavoriteItem? favorite = null,
        bool replaceFavorite = false)
    {
        int index = -1;
        for (int position = 0; position < TimelineEntries.Count; position++)
        {
            if (string.Equals(
                    TimelineEntries[position].Entry.Id,
                    item.Entry.Id,
                    StringComparison.Ordinal))
            {
                index = position;
                break;
            }
        }
        if (index < 0) return;
        FeedTimelineItem current = TimelineEntries[index];
        FeedTimelineItem updated = current with
        {
            State = state,
            Favorite = replaceFavorite ? favorite : current.Favorite
        };
        TimelineEntries[index] = updated;
        if (string.Equals(
                SelectedTimelineEntry?.Entry.Id,
                item.Entry.Id,
                StringComparison.Ordinal))
        {
            _selectedTimelineEntry = updated;
            OnPropertyChanged(nameof(SelectedTimelineEntry));
            SelectedFeedArticle = CreateReaderArticle(updated);
            SelectedTimelineNote = updated.Note;
        }
    }

    private bool IsCurrentTimelineEditor(
        FeedTimelineItem item,
        int expectedGeneration) =>
        expectedGeneration == Volatile.Read(ref _timelineEditorGeneration)
        && string.Equals(
            SelectedTimelineEntry?.Entry.Id,
            item.Entry.Id,
            StringComparison.Ordinal);

    private void SetTimelineEditorStatusIfCurrent(
        FeedTimelineItem item,
        int expectedGeneration,
        string status)
    {
        if (IsCurrentTimelineEditor(item, expectedGeneration))
        {
            TimelineEditorStatus = status;
        }
    }

    private void SetTimelineEditorStatusIfSelected(
        FeedTimelineItem item,
        string status)
    {
        if (string.Equals(
                SelectedTimelineEntry?.Entry.Id,
                item.Entry.Id,
                StringComparison.Ordinal))
        {
            TimelineEditorStatus = status;
        }
    }

    private FeedTimelineItem CreateTimelineItem(
        FeedEntry entry,
        EntryState? state = null,
        FavoriteItem? favorite = null)
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
            category?.Name ?? "未分类",
            state,
            favorite);
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
        Interlocked.Increment(ref _timelineEditorGeneration);
        _feedCatalogSync.StatusChanged -= OnTimelineCatalogSyncStatusChanged;
        ApplyTimelineFiltersCommand.Dispose();
        LoadMoreTimelineCommand.Dispose();
        ClearTimelineFiltersCommand.Dispose();
        ToggleTimelineReadCommand.Dispose();
        ToggleTimelineStarCommand.Dispose();
        SaveTimelineNoteCommand.Dispose();
        AddTimelineTagCommand.Dispose();
        RemoveTimelineTagCommand.Dispose();
    }

    private static DateTimeOffset ToTimelineBoundary(DateTime value)
    {
        DateTime local = DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
}
