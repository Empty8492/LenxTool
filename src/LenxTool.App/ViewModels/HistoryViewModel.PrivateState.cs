using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class HistoryViewModel
{
    private const string FeedEntryFavoriteType = "feed_entry";
    private const string DefaultProfile = "default";
    private const string DefaultTagColor = "#4B6B88";
    private const int MaximumNoteLength = 4000;
    private const int MaximumTagLength = 80;
    private FavoriteItem? _selectedSearchFavorite;
    private EntryState? _selectedSearchState;
    private string _selectedSearchPrivateNote = string.Empty;
    private string _selectedSearchSavedNote = string.Empty;
    private string _selectedSearchTagInput = string.Empty;
    private string _selectedSearchPrivateStatus = "选择 Feed 搜索结果后可编辑本机状态。";
    private Task _selectedSearchPrivateStateLoad = Task.CompletedTask;
    private int _selectedSearchPrivateStateGeneration;

    public ObservableCollection<TagItem> SelectedSearchTags { get; } = [];
    public AsyncRelayCommand<ContentSearchResult> ToggleSelectedSearchReadCommand { get; private set; } = null!;
    public AsyncRelayCommand<ContentSearchResult> ToggleSelectedSearchStarCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveSelectedSearchNoteCommand { get; private set; } = null!;
    public RelayCommand CancelSelectedSearchNoteCommand { get; private set; } = null!;
    public AsyncRelayCommand AddSelectedSearchTagCommand { get; private set; } = null!;
    public AsyncRelayCommand<TagItem> RemoveSelectedSearchTagCommand { get; private set; } = null!;

    public bool SelectedSearchIsFeedEntry =>
        SelectedSearchResult?.Type == ContentSearchResultType.FeedEntry;

    public bool SelectedSearchIsRead => _selectedSearchState?.IsRead ?? false;

    public bool SelectedSearchIsStarred =>
        _selectedSearchFavorite is not null || (_selectedSearchState?.IsStarred ?? false);

    public string SelectedSearchReadActionLabel =>
        SelectedSearchIsRead ? "标为未读" : "标为已读";

    public string SelectedSearchStarActionLabel =>
        SelectedSearchIsStarred ? "取消收藏" : "收藏";

    public string SelectedSearchPrivateNote
    {
        get => _selectedSearchPrivateNote;
        set
        {
            string normalized = value ?? string.Empty;
            if (normalized.Length > MaximumNoteLength)
            {
                normalized = normalized[..MaximumNoteLength];
            }
            if (SetProperty(ref _selectedSearchPrivateNote, normalized))
            {
                NotifySelectedSearchNoteCommands();
            }
        }
    }

    public string SelectedSearchTagInput
    {
        get => _selectedSearchTagInput;
        set
        {
            string normalized = value ?? string.Empty;
            if (normalized.Length > MaximumTagLength)
            {
                normalized = normalized[..MaximumTagLength];
            }
            if (SetProperty(ref _selectedSearchTagInput, normalized))
            {
                AddSelectedSearchTagCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectedSearchPrivateStatus
    {
        get => _selectedSearchPrivateStatus;
        private set => SetProperty(ref _selectedSearchPrivateStatus, value);
    }

    public Task SelectedSearchPrivateStateLoad => _selectedSearchPrivateStateLoad;

    private void ConfigureSelectedSearchPrivateState()
    {
        ToggleSelectedSearchReadCommand = new(
            ToggleSelectedSearchReadAsync,
            result => result?.Type == ContentSearchResultType.FeedEntry);
        ToggleSelectedSearchStarCommand = new(
            ToggleSelectedSearchStarAsync,
            result => result?.Type == ContentSearchResultType.FeedEntry);
        SaveSelectedSearchNoteCommand = new(
            SaveSelectedSearchNoteAsync,
            () => SelectedSearchIsFeedEntry && IsSelectedSearchNoteDirty);
        CancelSelectedSearchNoteCommand = new(
            CancelSelectedSearchNote,
            () => SelectedSearchIsFeedEntry && IsSelectedSearchNoteDirty);
        AddSelectedSearchTagCommand = new(
            AddSelectedSearchTagAsync,
            () => SelectedSearchIsFeedEntry
                  && !string.IsNullOrWhiteSpace(SelectedSearchTagInput));
        RemoveSelectedSearchTagCommand = new(
            RemoveSelectedSearchTagAsync,
            tag => SelectedSearchIsFeedEntry && tag is not null);
    }

    private bool IsSelectedSearchNoteDirty =>
        SelectedSearchIsFeedEntry
        && !string.Equals(
            SelectedSearchPrivateNote,
            _selectedSearchSavedNote,
            StringComparison.Ordinal);

    private void OnSelectedSearchResultChanged(ContentSearchResult? result)
    {
        int generation = Interlocked.Increment(ref _selectedSearchPrivateStateGeneration);
        ApplySelectedSearchPrivateState(null, null, []);
        SelectedSearchPrivateStatus = result?.Type == ContentSearchResultType.FeedEntry
            ? "正在读取 Feed 本机状态…"
            : "选择 Feed 搜索结果后可编辑本机状态。";
        _selectedSearchPrivateStateLoad = LoadSelectedSearchPrivateStateAsync(result, generation);
        OnPropertyChanged(nameof(SelectedSearchPrivateStateLoad));
        NotifySelectedSearchNoteCommands();
    }

    private async Task LoadSelectedSearchPrivateStateAsync(
        ContentSearchResult? result,
        int expectedGeneration)
    {
        if (result?.Type != ContentSearchResultType.FeedEntry)
        {
            ApplySelectedSearchPrivateState(null, null, []);
            SelectedSearchPrivateStatus = "选择 Feed 搜索结果后可编辑本机状态。";
            return;
        }

        try
        {
            FavoriteItem? favorite = await _favorites.GetAsync(
                FeedEntryFavoriteType,
                result.EntityId,
                CancellationToken.None);
            IReadOnlyDictionary<string, EntryState> states = await _entryStates.GetAsync(
                [result.EntityId],
                DefaultProfile,
                CancellationToken.None);
            IReadOnlyList<TagItem> tags = await _favorites.GetTagsForEntityAsync(
                FeedEntryFavoriteType,
                result.EntityId,
                CancellationToken.None);
            if (!IsCurrentSearchSelection(result, expectedGeneration)) return;
            ApplySelectedSearchPrivateState(
                favorite,
                states.GetValueOrDefault(result.EntityId),
                tags);
            SelectedSearchPrivateStatus = "收藏、备注和标签仅保存在本机。";
        }
        catch (Exception) when (expectedGeneration == Volatile.Read(
            ref _selectedSearchPrivateStateGeneration))
        {
            SelectedSearchPrivateStatus = "历史页私人状态读取失败；搜索结果仍可使用。";
        }
    }

    private void ApplySelectedSearchPrivateState(
        FavoriteItem? favorite,
        EntryState? state,
        IReadOnlyList<TagItem> tags)
    {
        _selectedSearchFavorite = favorite;
        _selectedSearchState = state;
        OnPropertyChanged(nameof(SelectedSearchIsRead));
        OnPropertyChanged(nameof(SelectedSearchIsStarred));
        OnPropertyChanged(nameof(SelectedSearchReadActionLabel));
        OnPropertyChanged(nameof(SelectedSearchStarActionLabel));
        _selectedSearchSavedNote = favorite?.Note ?? state?.Note ?? string.Empty;
        SetProperty(
            ref _selectedSearchPrivateNote,
            _selectedSearchSavedNote,
            nameof(SelectedSearchPrivateNote));
        SelectedSearchTags.Clear();
        foreach (TagItem tag in tags)
        {
            SelectedSearchTags.Add(tag);
        }
        SelectedSearchTagInput = string.Empty;
        NotifySelectedSearchNoteCommands();
        AddSelectedSearchTagCommand.NotifyCanExecuteChanged();
    }

    private async Task ToggleSelectedSearchReadAsync(
        ContentSearchResult? result,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentSearchSelection(result, Volatile.Read(
            ref _selectedSearchPrivateStateGeneration)))
        {
            return;
        }
        EntryState state = await _entryStates.PatchAsync(
            result!.EntityId,
            DefaultProfile,
            new EntryStatePatch(IsRead: !SelectedSearchIsRead),
            cancellationToken);
        if (!IsCurrentSearchSelection(result, Volatile.Read(
            ref _selectedSearchPrivateStateGeneration)))
        {
            return;
        }
        _selectedSearchState = state;
        OnPropertyChanged(nameof(SelectedSearchIsRead));
        OnPropertyChanged(nameof(SelectedSearchReadActionLabel));
        SelectedSearchPrivateStatus = state.IsRead
            ? "历史页已标记为已读。"
            : "历史页已恢复为未读。";
    }

    private async Task ToggleSelectedSearchStarAsync(
        ContentSearchResult? result,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentSearchSelection(result, Volatile.Read(
            ref _selectedSearchPrivateStateGeneration)))
        {
            return;
        }

        bool isStarred = !SelectedSearchIsStarred;
        bool favoriteChanged = false;
        FavoriteItem? originalFavorite = _selectedSearchFavorite;
        FavoriteItem? favorite = originalFavorite;
        string savedNote = _selectedSearchSavedNote;
        try
        {
            if (isStarred)
            {
                favorite = await _favorites.UpsertAsync(
                    FeedEntryFavoriteType,
                    result!.EntityId,
                    savedNote,
                    cancellationToken);
                favoriteChanged = true;
            }
            else
            {
                await _favorites.RemoveAsync(
                    FeedEntryFavoriteType,
                    result!.EntityId,
                    cancellationToken);
                favoriteChanged = true;
                favorite = null;
            }

            EntryState state;
            try
            {
                state = await _entryStates.PatchAsync(
                    result.EntityId,
                    DefaultProfile,
                    new EntryStatePatch(
                        IsStarred: isStarred,
                        Note: isStarred ? null : savedNote),
                    cancellationToken);
            }
            catch
            {
                if (favoriteChanged)
                {
                    await RestoreSelectedSearchFavoriteAsync(
                        result.EntityId,
                        originalFavorite);
                }
                throw;
            }
            if (!IsCurrentSearchSelection(result, Volatile.Read(
                ref _selectedSearchPrivateStateGeneration)))
            {
                return;
            }
            _selectedSearchFavorite = favorite;
            _selectedSearchState = state;
            OnPropertyChanged(nameof(SelectedSearchIsRead));
            OnPropertyChanged(nameof(SelectedSearchIsStarred));
            OnPropertyChanged(nameof(SelectedSearchReadActionLabel));
            OnPropertyChanged(nameof(SelectedSearchStarActionLabel));
            UpdateSelectedSearchSavedNote(
                state.Note,
                replaceEditorText: false);
            SelectedSearchPrivateStatus = isStarred
                ? "历史页已收藏到本机。"
                : "历史页已取消收藏；私人备注仍保留。";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SelectedSearchPrivateStatus = "历史页收藏状态保存失败，当前界面未更新。";
        }
    }

    private async Task SaveSelectedSearchNoteAsync(CancellationToken cancellationToken)
    {
        ContentSearchResult? result = SelectedSearchResult;
        if (!SelectedSearchIsFeedEntry || result is null) return;
        string note = SelectedSearchPrivateNote;
        int generation = Volatile.Read(ref _selectedSearchPrivateStateGeneration);
        bool wasStarred = SelectedSearchIsStarred;
        FavoriteItem? originalFavorite = _selectedSearchFavorite;
        bool favoriteChanged = false;
        try
        {
            FavoriteItem? favorite = _selectedSearchFavorite;
            if (wasStarred)
            {
                favorite = await _favorites.UpsertAsync(
                    FeedEntryFavoriteType,
                    result.EntityId,
                    note,
                    cancellationToken);
                favoriteChanged = true;
            }

            EntryState state;
            try
            {
                state = await _entryStates.PatchAsync(
                    result.EntityId,
                    DefaultProfile,
                    new EntryStatePatch(Note: note),
                    cancellationToken);
            }
            catch
            {
                if (favoriteChanged)
                {
                    await RestoreSelectedSearchFavoriteAsync(
                        result.EntityId,
                        originalFavorite);
                }
                throw;
            }
            if (!IsCurrentSearchSelection(result, generation)) return;
            _selectedSearchFavorite = favorite;
            _selectedSearchState = state;
            OnPropertyChanged(nameof(SelectedSearchIsRead));
            OnPropertyChanged(nameof(SelectedSearchIsStarred));
            OnPropertyChanged(nameof(SelectedSearchReadActionLabel));
            OnPropertyChanged(nameof(SelectedSearchStarActionLabel));
            UpdateSelectedSearchSavedNote(note, replaceEditorText: true);
            SelectedSearchPrivateStatus = "历史页私人备注已保存到本机。";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SelectedSearchPrivateStatus = "历史页私人备注保存失败，原有内容未从界面移除。";
        }
    }

    private void CancelSelectedSearchNote()
    {
        if (!IsSelectedSearchNoteDirty) return;
        UpdateSelectedSearchSavedNote(_selectedSearchSavedNote, replaceEditorText: true);
        SelectedSearchPrivateStatus = "已撤销历史页未保存的私人备注编辑。";
    }

    private async Task AddSelectedSearchTagAsync(CancellationToken cancellationToken)
    {
        ContentSearchResult? result = SelectedSearchResult;
        string name = SelectedSearchTagInput.Trim();
        if (!SelectedSearchIsFeedEntry || result is null || name.Length == 0) return;
        int generation = Volatile.Read(ref _selectedSearchPrivateStateGeneration);
        string[] currentTagIds = SelectedSearchTags.Select(tag => tag.Id).ToArray();
        try
        {
            TagItem tag = await _favorites.UpsertTagAsync(
                name,
                DefaultTagColor,
                cancellationToken);
            string[] tagIds = currentTagIds
                .Append(tag.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            await _favorites.SetTagsAsync(
                FeedEntryFavoriteType,
                result.EntityId,
                tagIds,
                cancellationToken);
            if (!IsCurrentSearchSelection(result, generation)) return;
            TagItem? existing = SelectedSearchTags.FirstOrDefault(
                value => value.Id == tag.Id);
            if (existing is not null)
            {
                SelectedSearchTags[SelectedSearchTags.IndexOf(existing)] = tag;
            }
            else
            {
                SelectedSearchTags.Add(tag);
            }
            SelectedSearchTagInput = string.Empty;
            SelectedSearchPrivateStatus = $"已添加历史页标签“{tag.Name}”。";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SelectedSearchPrivateStatus = "历史页标签保存失败，现有标签未从界面移除。";
        }
    }

    private async Task RemoveSelectedSearchTagAsync(
        TagItem? tag,
        CancellationToken cancellationToken)
    {
        ContentSearchResult? result = SelectedSearchResult;
        if (!SelectedSearchIsFeedEntry || result is null || tag is null) return;
        int generation = Volatile.Read(ref _selectedSearchPrivateStateGeneration);
        string[] remaining = SelectedSearchTags
            .Where(value => value.Id != tag.Id)
            .Select(value => value.Id)
            .ToArray();
        try
        {
            await _favorites.SetTagsAsync(
                FeedEntryFavoriteType,
                result.EntityId,
                remaining,
                cancellationToken);
            if (!IsCurrentSearchSelection(result, generation)) return;
            SelectedSearchTags.Remove(tag);
            SelectedSearchPrivateStatus = $"已移除历史页标签“{tag.Name}”。";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SelectedSearchPrivateStatus = "历史页标签移除失败，现有标签保持不变。";
        }
    }

    private async Task RestoreSelectedSearchFavoriteAsync(
        string entityId,
        FavoriteItem? originalFavorite)
    {
        if (originalFavorite is null)
        {
            await _favorites.RemoveAsync(
                FeedEntryFavoriteType,
                entityId,
                CancellationToken.None);
            return;
        }
        await _favorites.UpsertAsync(
            FeedEntryFavoriteType,
            entityId,
            originalFavorite.Note,
            CancellationToken.None);
    }

    private bool IsCurrentSearchSelection(
        ContentSearchResult? result,
        int expectedGeneration) =>
        result is not null
        && expectedGeneration == Volatile.Read(ref _selectedSearchPrivateStateGeneration)
        && ReferenceEquals(SelectedSearchResult, result);

    private void UpdateSelectedSearchSavedNote(
        string note,
        bool replaceEditorText)
    {
        _selectedSearchSavedNote = note;
        if (replaceEditorText)
        {
            SetProperty(
                ref _selectedSearchPrivateNote,
                note,
                nameof(SelectedSearchPrivateNote));
        }
        OnPropertyChanged(nameof(IsSelectedSearchNoteDirty));
        NotifySelectedSearchNoteCommands();
    }

    private void NotifySelectedSearchNoteCommands()
    {
        OnPropertyChanged(nameof(IsSelectedSearchNoteDirty));
        SaveSelectedSearchNoteCommand.NotifyCanExecuteChanged();
        CancelSelectedSearchNoteCommand.NotifyCanExecuteChanged();
        ToggleSelectedSearchReadCommand.NotifyCanExecuteChanged();
        ToggleSelectedSearchStarCommand.NotifyCanExecuteChanged();
        AddSelectedSearchTagCommand.NotifyCanExecuteChanged();
        RemoveSelectedSearchTagCommand.NotifyCanExecuteChanged();
    }
}
