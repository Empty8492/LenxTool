using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
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
    private readonly IFeedSmartViewRepository? _feedSmartViewRepository;
    private readonly IFeedSmartViewSyncService? _feedSmartViewSync;
    private readonly TimeProvider _timelineTimeProvider;
    private readonly SynchronizationContext? _timelineSynchronizationContext;
    private FeedCatalogSnapshot? _timelineCatalog;
    private CancellationTokenSource? _timelineProgressCancellation;
    private Task _timelineProgressWrite = Task.CompletedTask;
    private FeedTimelineFilterOption? _selectedTimelineCategory;
    private FeedTimelineFilterOption? _selectedTimelineFeed;
    private FeedTimelineReadFilterOption? _selectedTimelineReadFilter;
    private FeedTimelineFilterOption? _selectedTimelineTag;
    private FeedTimelineItem? _selectedTimelineEntry;
    private FeedSmartView? _selectedTimelineSmartView;
    private FeedSmartView? _appliedTimelineSmartView;
    private NewsArticle? _selectedFeedArticle;
    private DateTime? _selectedTimelineDate;
    private DateTimeOffset? _lastTimelineRefreshAt;
    private DateTimeOffset? _catalogLastSynchronizedAt;
    private string _timelineKeyword = string.Empty;
    private string _timelineStatus = "正在读取本地 Feed 缓存…";
    private string _timelineSmartViewStatus = "共享智能视图尚未同步。";
    private string _selectedTimelineNote = string.Empty;
    private string _selectedTimelineSavedNote = string.Empty;
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
    public ObservableCollection<FeedSmartView> TimelineSmartViews { get; } = [];
    public ObservableCollection<TagItem> SelectedTimelineTags { get; } = [];
    public AsyncRelayCommand ApplyTimelineFiltersCommand { get; private set; } = null!;
    public AsyncRelayCommand LoadMoreTimelineCommand { get; private set; } = null!;
    public AsyncRelayCommand ClearTimelineFiltersCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplyTimelineSmartViewCommand { get; private set; } = null!;
    public AsyncRelayCommand<FeedTimelineItem> ToggleTimelineReadCommand { get; private set; } = null!;
    public AsyncRelayCommand<FeedTimelineItem> ToggleTimelineStarCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveTimelineNoteCommand { get; private set; } = null!;
    public RelayCommand CancelTimelineNoteEditCommand { get; private set; } = null!;
    public AsyncRelayCommand AddTimelineTagCommand { get; private set; } = null!;
    public AsyncRelayCommand<TagItem> RemoveTimelineTagCommand { get; private set; } = null!;
    public RelayCommand ResetTimelineProgressCommand { get; private set; } = null!;

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
                SelectFeedReaderEntry(value);
                UpdateTimelineSavedNote(value?.Note ?? string.Empty, replaceEditorText: true);
                TimelineTagInput = string.Empty;
                SelectedTimelineTags.Clear();
                int generation = Interlocked.Increment(ref _timelineEditorGeneration);
                _selectedTimelineEditorLoad = LoadSelectedTimelineEditorAsync(value, generation);
                OnPropertyChanged(nameof(SelectedTimelineEditorLoad));
                SaveTimelineNoteCommand.NotifyCanExecuteChanged();
                CancelTimelineNoteEditCommand.NotifyCanExecuteChanged();
                AddTimelineTagCommand.NotifyCanExecuteChanged();
                ResetTimelineProgressCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public FeedSmartView? SelectedTimelineSmartView
    {
        get => _selectedTimelineSmartView;
        set
        {
            if (SetProperty(ref _selectedTimelineSmartView, value))
            {
                ApplyTimelineSmartViewCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsTimelineSmartViewApplied =>
        _appliedTimelineSmartView is not null;

    public string TimelineSmartViewStatus
    {
        get => _timelineSmartViewStatus;
        private set => SetProperty(ref _timelineSmartViewStatus, value);
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
            if (SetProperty(ref _selectedTimelineNote, normalized))
            {
                OnPropertyChanged(nameof(IsTimelineNoteDirty));
                SaveTimelineNoteCommand.NotifyCanExecuteChanged();
                CancelTimelineNoteEditCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsTimelineNoteDirty =>
        SelectedTimelineEntry is not null
        && !string.Equals(
            SelectedTimelineNote,
            _selectedTimelineSavedNote,
            StringComparison.Ordinal);

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

    public Task TimelineProgressWrite => _timelineProgressWrite;

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

    public async Task OpenEntityAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                entityType,
                FeedEntryFavoriteType,
                StringComparison.Ordinal))
        {
            return;
        }
        FeedEntry? entry = await _feedEntryRepository.GetByIdAsync(
            entityId,
            cancellationToken);
        if (entry is null)
        {
            TimelineStatus = "对应的 Feed 条目已被清理。";
            return;
        }
        if (_timelineCatalog is null)
        {
            await ReloadTimelineCatalogAsync(
                preserveSelection: false,
                cancellationToken);
        }
        Task<IReadOnlyDictionary<string, EntryState>> stateTask =
            _entryStateRepository.GetAsync(
                [entry.Id],
                DefaultTimelineProfile,
                cancellationToken);
        Task<IReadOnlyDictionary<string, FavoriteItem>> favoriteTask =
            _favoriteRepository.GetForEntitiesAsync(
                FeedEntryFavoriteType,
                [entry.Id],
                cancellationToken);
        await Task.WhenAll(stateTask, favoriteTask);
        IReadOnlyDictionary<string, EntryState> states =
            await stateTask;
        IReadOnlyDictionary<string, FavoriteItem> favorites =
            await favoriteTask;
        var item = CreateTimelineItem(
            entry,
            states.GetValueOrDefault(entry.Id),
            favorites.GetValueOrDefault(entry.Id));
        FeedTimelineItem? existing = TimelineEntries.FirstOrDefault(
            value => string.Equals(
                value.Entry.Id,
                entry.Id,
                StringComparison.Ordinal));
        if (existing is not null)
        {
            int index = TimelineEntries.IndexOf(existing);
            TimelineEntries[index] = item;
        }
        else
        {
            TimelineEntries.Insert(0, item);
        }
        SelectedSectionIndex = 0;
        SelectedTimelineEntry = item;
        TimelineStatus = $"已从统一搜索打开：{entry.Title}";
    }

    private void ConfigureTimeline()
    {
        _selectedTimelineReadFilter = TimelineReadFilters[0];
        ApplyTimelineFiltersCommand = new(ApplyTimelineFiltersAsync);
        LoadMoreTimelineCommand = new(
            LoadMoreTimelineAsync,
            () => HasMoreTimelineEntries);
        ClearTimelineFiltersCommand = new(ClearTimelineFiltersAsync);
        ApplyTimelineSmartViewCommand = new(
            ApplyTimelineSmartViewAsync,
            () => SelectedTimelineSmartView is not null);
        ToggleTimelineReadCommand = new(ToggleTimelineReadAsync, item => item is not null);
        ToggleTimelineStarCommand = new(ToggleTimelineStarAsync, item => item is not null);
        SaveTimelineNoteCommand = new(
            SaveTimelineNoteAsync,
            () => IsTimelineNoteDirty);
        CancelTimelineNoteEditCommand = new(
            CancelTimelineNoteEdit,
            () => IsTimelineNoteDirty);
        AddTimelineTagCommand = new(
            AddTimelineTagAsync,
            () => SelectedTimelineEntry is not null
                  && !string.IsNullOrWhiteSpace(TimelineTagInput));
        RemoveTimelineTagCommand = new(
            RemoveTimelineTagAsync,
            tag => SelectedTimelineEntry is not null && tag is not null);
        ResetTimelineProgressCommand = new(
            ResetTimelineProgress,
            () => SelectedTimelineEntry is not null && SelectedTimelineEntry.Progress > 0);
        _feedCatalogSync.StatusChanged += OnTimelineCatalogSyncStatusChanged;
    }

    public void QueueTimelineProgress(FeedTimelineItem? item, double progress)
    {
        if (item is null || double.IsNaN(progress) || double.IsInfinity(progress)) return;
        double normalized = Math.Clamp(progress, 0, 100);
        if (Math.Abs(normalized - item.Progress) < 1) return;

        _timelineProgressCancellation?.Cancel();
        _timelineProgressCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _timelineProgressCancellation = cancellation;
        _timelineProgressWrite = PersistTimelineProgressAfterDelayAsync(
            item,
            normalized,
            cancellation.Token);
        OnPropertyChanged(nameof(TimelineProgressWrite));
    }

    private void ResetTimelineProgress()
    {
        FeedTimelineItem? item = SelectedTimelineEntry;
        if (item is null) return;
        QueueTimelineProgress(item, 0);
        TimelineEditorStatus = "已将阅读位置重置为开头。";
    }

    private async Task PersistTimelineProgressAfterDelayAsync(
        FeedTimelineItem item,
        double progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            EntryState state = await _entryStateRepository.PatchAsync(
                item.Entry.Id,
                DefaultTimelineProfile,
                new EntryStatePatch(Progress: progress),
                cancellationToken);
            if (_timelineDisposed) return;
            ReplaceTimelineItem(item, state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (!_timelineDisposed)
        {
            SetTimelineEditorStatusIfSelected(
                item,
                "阅读进度保存失败，正文仍可继续阅读。");
        }
    }

    private async Task InitializeTimelineAsync(CancellationToken cancellationToken)
    {
        await ReloadTimelineTagChoicesAsync(cancellationToken);
        await ReloadTimelineCatalogAsync(preserveSelection: false, cancellationToken);
        _ = await LoadTimelineSmartViewsAsync(cancellationToken);
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
        await ApplyTimelineQueryAsync(cancellationToken);
        if (PictureFeed is not null && _pictureFeedInitialized)
        {
            await PictureFeed.RefreshCatalogAsync(
                preserveSelection,
                cancellationToken);
        }
        if (AudioFeed is not null && _audioFeedInitialized)
        {
            await AudioFeed.RefreshCatalogAsync(
                preserveSelection,
                cancellationToken);
        }
        if (VideoFeed is not null && _videoFeedInitialized)
        {
            await VideoFeed.RefreshCatalogAsync(
                preserveSelection,
                cancellationToken);
        }
    }

    private async Task ApplyTimelineFiltersAsync(CancellationToken cancellationToken)
    {
        SetAppliedTimelineSmartView(null);
        await ApplyTimelineQueryAsync(cancellationToken);
    }

    private async Task ApplyTimelineSmartViewAsync(
        CancellationToken cancellationToken)
    {
        if (SelectedTimelineSmartView is not { } selectedView)
        {
            return;
        }
        string selectedId = selectedView.Id;
        if (!await LoadTimelineSmartViewsAsync(cancellationToken))
        {
            SetAppliedTimelineSmartView(null);
            return;
        }
        FeedSmartView? selected = TimelineSmartViews.FirstOrDefault(
            view => string.Equals(
                view.Id,
                selectedId,
                StringComparison.Ordinal));
        if (selected is null)
        {
            SetAppliedTimelineSmartView(null);
            TimelineSmartViewStatus =
                "该共享智能视图已被管理员移除，请选择其他视图。";
            return;
        }
        SelectedTimelineSmartView = selected;
        SetAppliedTimelineSmartView(selected);
        await ApplyTimelineQueryAsync(cancellationToken);
        TimelineSmartViewStatus =
            $"正在使用“{selected.Name}”；已读与收藏仍只从本机读取。";
    }

    private async Task ApplyTimelineQueryAsync(
        CancellationToken cancellationToken)
    {
        int generation = Interlocked.Increment(ref _timelineQueryGeneration);
        LoadMoreTimelineCommand.Cancel();
        GenerateVisibleFeedSummariesCommand.Cancel();
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
        SetAppliedTimelineSmartView(null);
        SelectedTimelineCategory = TimelineCategories.FirstOrDefault();
        SelectedTimelineFeed = TimelineFeeds.FirstOrDefault();
        SelectedTimelineReadFilter = TimelineReadFilters[0];
        TimelineFavoritesOnly = false;
        SelectedTimelineTag = TimelineTags.FirstOrDefault();
        SelectedTimelineDate = null;
        TimelineKeyword = string.Empty;
        await ApplyTimelineQueryAsync(cancellationToken);
    }

    private FeedEntryQuery CreateTimelineQuery(int offset)
    {
        if (_appliedTimelineSmartView is { } smartView)
        {
            return FeedSmartViewValidator.Apply(
                smartView,
                _timelineTimeProvider.GetUtcNow(),
                offset,
                TimelinePageSize,
                DefaultTimelineProfile);
        }
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
        _timelineNextOffset = page.NextOffset
            ?? checked(page.Offset + page.Items.Count);
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
        GenerateVisibleFeedSummariesCommand.NotifyCanExecuteChanged();
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

        await MarkTimelineEntryReadOnOpenAsync(item, expectedGeneration);
    }

    private async Task MarkTimelineEntryReadOnOpenAsync(
        FeedTimelineItem item,
        int expectedGeneration)
    {
        if (item.IsRead) return;
        try
        {
            EntryState state = await _entryStateRepository.PatchAsync(
                item.Entry.Id,
                DefaultTimelineProfile,
                new EntryStatePatch(IsRead: true),
                CancellationToken.None);
            if (_timelineDisposed) return;
            ReplaceTimelineItem(item, state);
        }
        catch (Exception) when (!_timelineDisposed)
        {
            SetTimelineEditorStatusIfCurrent(
                item,
                expectedGeneration,
                "自动标记已读失败，可使用“已读”按钮重试。");
        }
    }

    private void CancelTimelineNoteEdit()
    {
        if (!IsTimelineNoteDirty) return;
        UpdateTimelineSavedNote(_selectedTimelineSavedNote, replaceEditorText: true);
        TimelineEditorStatus = "已撤销未保存的私人备注编辑。";
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
                replaceFavorite: item.IsStarred,
                replaceEditorNote: IsCurrentTimelineEditor(item, editorGeneration));
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
        bool replaceFavorite = false,
        bool replaceEditorNote = false)
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
            UpdateTimelineSavedNote(updated.Note, replaceEditorNote);
            ResetTimelineProgressCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateTimelineSavedNote(
        string note,
        bool replaceEditorText)
    {
        _selectedTimelineSavedNote = note;
        if (replaceEditorText)
        {
            SetProperty(
                ref _selectedTimelineNote,
                note,
                nameof(SelectedTimelineNote));
        }
        OnPropertyChanged(nameof(IsTimelineNoteDirty));
        SaveTimelineNoteCommand.NotifyCanExecuteChanged();
        CancelTimelineNoteEditCommand.NotifyCanExecuteChanged();
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
        DisposeFeedReader();
        _timelineProgressCancellation?.Cancel();
        _timelineProgressCancellation?.Dispose();
        _timelineProgressCancellation = null;
        Interlocked.Increment(ref _timelineCatalogGeneration);
        Interlocked.Increment(ref _timelineQueryGeneration);
        Interlocked.Increment(ref _timelineEditorGeneration);
        _feedCatalogSync.StatusChanged -= OnTimelineCatalogSyncStatusChanged;
        ApplyTimelineFiltersCommand.Dispose();
        LoadMoreTimelineCommand.Dispose();
        ClearTimelineFiltersCommand.Dispose();
        ApplyTimelineSmartViewCommand.Dispose();
        ToggleTimelineReadCommand.Dispose();
        ToggleTimelineStarCommand.Dispose();
        SaveTimelineNoteCommand.Dispose();
        AddTimelineTagCommand.Dispose();
        RemoveTimelineTagCommand.Dispose();
    }

    private async Task RefreshTimelineSmartViewsAsync(
        CancellationToken cancellationToken)
    {
        bool shouldReapply =
            _appliedTimelineSmartView is not null;
        bool synchronizationFailed = false;
        if (_feedSmartViewSync is not null)
        {
            try
            {
                await _feedSmartViewSync.SyncAsync(cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                synchronizationFailed = true;
            }
        }
        bool cacheLoaded =
            await LoadTimelineSmartViewsAsync(cancellationToken);
        if (synchronizationFailed)
        {
            TimelineSmartViewStatus =
                $"同步失败，继续使用最后有效的离线版本。{TimelineSmartViewStatus}";
        }
        if (cacheLoaded &&
            shouldReapply &&
            _appliedTimelineSmartView is not null)
        {
            await ApplyTimelineQueryAsync(cancellationToken);
        }
    }

    private async Task<bool> LoadTimelineSmartViewsAsync(
        CancellationToken cancellationToken)
    {
        if (_feedSmartViewRepository is null)
        {
            TimelineSmartViews.Clear();
            SelectedTimelineSmartView = null;
            SetAppliedTimelineSmartView(null);
            TimelineSmartViewStatus = "当前未启用共享智能视图。";
            return false;
        }

        string? selectedId = SelectedTimelineSmartView?.Id;
        string? appliedId = _appliedTimelineSmartView?.Id;
        try
        {
            FeedSmartViewSnapshot snapshot =
                await _feedSmartViewRepository.GetAsync(cancellationToken);
            TimelineSmartViews.Clear();
            foreach (FeedSmartView view in snapshot.Views
                         .OrderBy(item => item.SortOrder)
                         .ThenBy(item => item.Name, StringComparer.CurrentCulture)
                         .ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                TimelineSmartViews.Add(view);
            }
            SelectedTimelineSmartView = selectedId is null
                ? TimelineSmartViews.FirstOrDefault()
                : TimelineSmartViews.FirstOrDefault(view =>
                    string.Equals(view.Id, selectedId, StringComparison.Ordinal))
                    ?? TimelineSmartViews.FirstOrDefault();
            FeedSmartView? updatedApplied = appliedId is null
                ? null
                : TimelineSmartViews.FirstOrDefault(view =>
                    string.Equals(view.Id, appliedId, StringComparison.Ordinal));
            SetAppliedTimelineSmartView(updatedApplied);
            string synchronized = snapshot.LastSyncedAt is { } synchronizedAt
                ? $" · 同步 {synchronizedAt.ToLocalTime():MM-dd HH:mm}"
                : string.Empty;
            TimelineSmartViewStatus =
                $"共享视图 v{snapshot.ViewSetVersion} · {TimelineSmartViews.Count} 个{synchronized}";
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            TimelineSmartViewStatus =
                "读取共享智能视图失败；临时筛选仍可使用。";
            return false;
        }
    }

    private void SetAppliedTimelineSmartView(FeedSmartView? value)
    {
        if (Equals(_appliedTimelineSmartView, value))
        {
            return;
        }
        _appliedTimelineSmartView = value;
        OnPropertyChanged(nameof(IsTimelineSmartViewApplied));
    }

    private static DateTimeOffset ToTimelineBoundary(DateTime value)
    {
        DateTime local = DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
}
