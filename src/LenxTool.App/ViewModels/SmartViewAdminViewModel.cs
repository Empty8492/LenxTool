using System.Collections.ObjectModel;
using System.IO;
using LenxTool.App.Mvvm;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record SmartViewCategoryChoice(string? Id, string Label);

public sealed record SmartViewFeedChoice(
    string? Id,
    string Label,
    string? CategoryId);

public sealed record SmartViewKindChoice(
    EntryViewKind? Value,
    string Label);

public sealed record SmartViewReadFilterChoice(
    FeedEntryReadFilter Value,
    string Label);

public sealed class SmartViewAdminViewModel : PageViewModel
{
    private readonly IFeedSmartViewAdminService _adminService;
    private readonly IFeedSmartViewSyncService _syncService;
    private readonly IFeedCatalogRepository _catalogRepository;
    private readonly IAccountSessionService _accountSession;
    private readonly SynchronizationContext? _synchronizationContext;
    private FeedSmartView? _selectedSmartView;
    private string? _editingViewId;
    private string? _pendingDeleteViewId;
    private bool _isAdmin;
    private bool _isBusy;
    private long _viewSetVersion;
    private string _viewName = string.Empty;
    private int _sortOrder = 100;
    private bool _viewIsEnabled = true;
    private SmartViewCategoryChoice _selectedCategory;
    private SmartViewFeedChoice _selectedFeed;
    private SmartViewKindChoice _selectedViewKind;
    private SmartViewReadFilterChoice _selectedReadFilter;
    private bool _favoritesOnly;
    private string _searchText = string.Empty;
    private int? _publishedWithinDays;
    private string _status = "正在读取共享智能视图…";

    public SmartViewAdminViewModel(
        IFeedSmartViewAdminService adminService,
        IFeedSmartViewSyncService syncService,
        IFeedCatalogRepository catalogRepository,
        IAccountSessionService accountSession)
        : base(
            "智能视图",
            "发布只读筛选定义；正文、网址和用户的已读收藏状态不会上传")
    {
        _adminService = adminService;
        _syncService = syncService;
        _catalogRepository = catalogRepository;
        _accountSession = accountSession;
        _synchronizationContext = SynchronizationContext.Current;
        SmartViews = [];
        CategoryChoices = [new(null, "全部分类")];
        FeedChoices = [new(null, "全部 Feed", null)];
        ViewKindChoices =
        [
            new(null, "全部内容"),
            new(EntryViewKind.Article, "文章"),
            new(EntryViewKind.Picture, "图片"),
            new(EntryViewKind.Audio, "音频"),
            new(EntryViewKind.Video, "视频"),
            new(EntryViewKind.Notification, "通知")
        ];
        ReadFilterChoices =
        [
            new(FeedEntryReadFilter.All, "全部"),
            new(FeedEntryReadFilter.Unread, "未读"),
            new(FeedEntryReadFilter.Read, "已读")
        ];
        _selectedCategory = CategoryChoices[0];
        _selectedFeed = FeedChoices[0];
        _selectedViewKind = ViewKindChoices[0];
        _selectedReadFilter = ReadFilterChoices[0];

        RefreshCommand = new(RefreshAsync, () => IsAdmin && !IsBusy);
        BeginNewCommand = new(BeginNew, () => IsAdmin && !IsBusy);
        PublishCommand = new(PublishAsync, CanPublish);
        PrepareDeleteCommand = new(
            PrepareDelete,
            () => IsAdmin && !IsBusy && SelectedSmartView is not null);
        ConfirmDeleteCommand = new(
            ConfirmDeleteAsync,
            () => IsAdmin && !IsBusy && PendingDeleteViewId is not null);
        CancelDeleteCommand = new(
            CancelDelete,
            () => PendingDeleteViewId is not null);

        _accountSession.SessionChanged += OnSessionChanged;
        ApplySession(_accountSession.Current);
        BeginNew();
    }

    public ObservableCollection<FeedSmartView> SmartViews { get; }
    public ObservableCollection<SmartViewCategoryChoice> CategoryChoices { get; }
    public ObservableCollection<SmartViewFeedChoice> FeedChoices { get; }
    public IReadOnlyList<SmartViewKindChoice> ViewKindChoices { get; }
    public IReadOnlyList<SmartViewReadFilterChoice> ReadFilterChoices { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand BeginNewCommand { get; }
    public AsyncRelayCommand PublishCommand { get; }
    public RelayCommand PrepareDeleteCommand { get; }
    public AsyncRelayCommand ConfirmDeleteCommand { get; }
    public RelayCommand CancelDeleteCommand { get; }

    public bool IsAdmin => _isAdmin;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommands();
            }
        }
    }

    public long ViewSetVersion
    {
        get => _viewSetVersion;
        private set => SetProperty(ref _viewSetVersion, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsNewView => _editingViewId is null;
    public string EditorTitle => IsNewView
        ? "新建智能视图"
        : $"编辑智能视图 · v{SelectedSmartView?.Version ?? 0}";
    public string PublishLabel => IsNewView ? "发布新视图" : "发布新版本";

    public FeedSmartView? SelectedSmartView
    {
        get => _selectedSmartView;
        set
        {
            if (!SetProperty(ref _selectedSmartView, value) ||
                value is null)
            {
                return;
            }
            LoadView(value);
        }
    }

    public string ViewName
    {
        get => _viewName;
        set
        {
            if (SetProperty(ref _viewName, value ?? string.Empty))
            {
                DraftChanged();
            }
        }
    }

    public int SortOrder
    {
        get => _sortOrder;
        set
        {
            if (SetProperty(ref _sortOrder, value))
            {
                DraftChanged();
            }
        }
    }

    public bool ViewIsEnabled
    {
        get => _viewIsEnabled;
        set
        {
            if (SetProperty(ref _viewIsEnabled, value))
            {
                DraftChanged();
            }
        }
    }

    public SmartViewCategoryChoice SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(
                ref _selectedCategory,
                value ?? CategoryChoices[0]))
            {
                DraftChanged();
            }
        }
    }

    public SmartViewFeedChoice SelectedFeed
    {
        get => _selectedFeed;
        set
        {
            if (SetProperty(
                ref _selectedFeed,
                value ?? FeedChoices[0]))
            {
                DraftChanged();
            }
        }
    }

    public SmartViewKindChoice SelectedViewKind
    {
        get => _selectedViewKind;
        set
        {
            if (SetProperty(
                ref _selectedViewKind,
                value ?? ViewKindChoices[0]))
            {
                DraftChanged();
            }
        }
    }

    public SmartViewReadFilterChoice SelectedReadFilter
    {
        get => _selectedReadFilter;
        set
        {
            if (SetProperty(
                ref _selectedReadFilter,
                value ?? ReadFilterChoices[0]))
            {
                DraftChanged();
            }
        }
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (SetProperty(ref _favoritesOnly, value))
            {
                DraftChanged();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                DraftChanged();
            }
        }
    }

    public int? PublishedWithinDays
    {
        get => _publishedWithinDays;
        set
        {
            if (SetProperty(ref _publishedWithinDays, value))
            {
                DraftChanged();
            }
        }
    }

    public string? PendingDeleteViewId
    {
        get => _pendingDeleteViewId;
        private set
        {
            if (SetProperty(ref _pendingDeleteViewId, value))
            {
                OnPropertyChanged(nameof(IsDeletePending));
                NotifyCommands();
            }
        }
    }

    public bool IsDeletePending => PendingDeleteViewId is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ApplySession(_accountSession.Current);
        if (!IsAdmin)
        {
            Status = "需要管理员账号才能查看或发布共享智能视图。";
            return;
        }
        _ = await LoadAsync(cancellationToken);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            _ = await LoadAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> LoadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            FeedCatalogSnapshot? catalog = null;
            bool catalogAvailable = true;
            try
            {
                catalog = await _catalogRepository.GetCatalogAsync(
                    FeedCatalogScope.Active,
                    cancellationToken);
            }
            catch (Exception) when (
                !cancellationToken.IsCancellationRequested)
            {
                catalogAvailable = false;
            }
            FeedSmartViewSnapshot snapshot =
                await _adminService.GetAllAsync(cancellationToken);
            if (!IsAdmin)
            {
                return false;
            }
            RebuildCatalogChoices(catalog);
            string? selectedId = _editingViewId;
            SmartViews.Clear();
            foreach (FeedSmartView view in snapshot.Views
                         .OrderBy(item => item.SortOrder)
                         .ThenBy(item => item.Name, StringComparer.CurrentCulture)
                         .ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                SmartViews.Add(view);
            }
            ViewSetVersion = snapshot.ViewSetVersion;
            FeedSmartView? selected = selectedId is null
                ? SmartViews.FirstOrDefault()
                : SmartViews.FirstOrDefault(view =>
                    string.Equals(view.Id, selectedId, StringComparison.Ordinal));
            if (selected is null)
            {
                BeginNew();
            }
            else
            {
                SelectedSmartView = selected;
            }
            Status = catalogAvailable
                ? $"智能视图集 v{ViewSetVersion} 已加载，共 {SmartViews.Count} 个。"
                : $"智能视图集 v{ViewSetVersion} 已加载；目录选项暂不可用，已保留定义中的引用。";
            return true;
        }
        catch (Exception exception) when (
            exception is AppException
                or ArgumentException
                or InvalidDataException)
        {
            Status = FormatException(exception);
            return false;
        }
    }

    private void BeginNew()
    {
        _editingViewId = null;
        SetProperty(ref _selectedSmartView, null, nameof(SelectedSmartView));
        _viewName = string.Empty;
        _sortOrder = 100;
        _viewIsEnabled = true;
        _selectedCategory = CategoryChoices[0];
        _selectedFeed = FeedChoices[0];
        _selectedViewKind = ViewKindChoices[0];
        _selectedReadFilter = ReadFilterChoices[0];
        _favoritesOnly = false;
        _searchText = string.Empty;
        _publishedWithinDays = null;
        PendingDeleteViewId = null;
        RaiseEditorProperties();
    }

    private void LoadView(FeedSmartView view)
    {
        _editingViewId = view.Id;
        _viewName = view.Name;
        _sortOrder = view.SortOrder;
        _viewIsEnabled = view.IsEnabled;
        _selectedCategory = FindCategory(view.Filter.CategoryId);
        _selectedFeed = FindFeed(view.Filter.FeedId);
        _selectedViewKind = ViewKindChoices.Single(
            choice => choice.Value == view.Filter.ViewKind);
        _selectedReadFilter = ReadFilterChoices.Single(
            choice => choice.Value == view.Filter.ReadFilter);
        _favoritesOnly = view.Filter.FavoritesOnly;
        _searchText = view.Filter.SearchText ?? string.Empty;
        _publishedWithinDays = view.Filter.PublishedWithinDays;
        PendingDeleteViewId = null;
        RaiseEditorProperties();
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        FeedSmartViewInput input;
        try
        {
            input = BuildInput();
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException)
        {
            Status = FormatException(exception);
            return;
        }
        string? targetId = _editingViewId;
        IsBusy = true;
        try
        {
            FeedSmartViewMutationResult result = targetId is null
                ? await _adminService.CreateAsync(
                    input,
                    ViewSetVersion,
                    cancellationToken)
                : await _adminService.UpdateAsync(
                    targetId,
                    input,
                    ViewSetVersion,
                    cancellationToken);
            FeedSmartView view = result.View
                ?? throw new InvalidDataException(
                    "智能视图写入响应缺少定义。");
            if (!IsAdmin)
            {
                return;
            }
            ViewSetVersion = result.ViewSetVersion;
            Upsert(view);
            SelectedSmartView = view;
            bool synchronized =
                await SynchronizeActiveCacheAsync(cancellationToken);
            if (!IsAdmin)
            {
                return;
            }
            Status = synchronized
                ? $"智能视图已发布为视图集 v{ViewSetVersion}，普通用户只能只读使用。"
                : $"远端已提交为 v{ViewSetVersion}，本机 ACTIVE 缓存将在稍后重试。";
        }
        catch (AppException exception)
            when (exception.Error.Code == AppErrorCode.Conflict)
        {
            await RefreshAfterConflictAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is AppException
                or ArgumentException
                or InvalidDataException)
        {
            Status = FormatException(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PrepareDelete()
    {
        PendingDeleteViewId = SelectedSmartView?.Id;
    }

    private void CancelDelete()
    {
        PendingDeleteViewId = null;
    }

    private async Task ConfirmDeleteAsync(
        CancellationToken cancellationToken)
    {
        string? targetId = PendingDeleteViewId;
        if (targetId is null)
        {
            return;
        }
        IsBusy = true;
        try
        {
            FeedSmartViewMutationResult result =
                await _adminService.DeleteAsync(
                    targetId,
                    ViewSetVersion,
                    cancellationToken);
            if (!IsAdmin)
            {
                return;
            }
            ViewSetVersion = result.ViewSetVersion;
            FeedSmartView? removed = SmartViews.FirstOrDefault(
                view => string.Equals(
                    view.Id,
                    targetId,
                    StringComparison.Ordinal));
            if (removed is not null)
            {
                SmartViews.Remove(removed);
            }
            BeginNew();
            bool synchronized =
                await SynchronizeActiveCacheAsync(cancellationToken);
            if (!IsAdmin)
            {
                return;
            }
            Status = synchronized
                ? $"智能视图已删除，视图集更新为 v{ViewSetVersion}。"
                : $"远端已删除并更新为 v{ViewSetVersion}，本机 ACTIVE 缓存将在稍后重试。";
        }
        catch (AppException exception)
            when (exception.Error.Code == AppErrorCode.Conflict)
        {
            await RefreshAfterConflictAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is AppException
                or ArgumentException
                or InvalidDataException)
        {
            Status = FormatException(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAfterConflictAsync(
        CancellationToken cancellationToken)
    {
        if (await LoadAsync(cancellationToken))
        {
            Status =
                "其他管理员已更新智能视图；已刷新最新版本，请核对表单后重试。";
        }
    }

    private async Task<bool> SynchronizeActiveCacheAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _syncService.SyncAsync(cancellationToken);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private FeedSmartViewInput BuildInput() =>
        FeedSmartViewValidator.ValidateAndNormalize(new FeedSmartViewInput(
            ViewName,
            SortOrder,
            ViewIsEnabled,
            new(
                SelectedFeed.Id,
                SelectedCategory.Id,
                SelectedViewKind.Value,
                SelectedReadFilter.Value,
                FavoritesOnly,
                SearchText,
                PublishedWithinDays)));

    private bool CanPublish()
    {
        if (!IsAdmin || IsBusy)
        {
            return false;
        }
        try
        {
            _ = BuildInput();
            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    private void RebuildCatalogChoices(FeedCatalogSnapshot? catalog)
    {
        CategoryChoices.Clear();
        CategoryChoices.Add(new(null, "全部分类"));
        FeedChoices.Clear();
        FeedChoices.Add(new(null, "全部 Feed", null));
        if (catalog is null)
        {
            return;
        }
        foreach (FeedCategory category in catalog.Categories
                     .OrderBy(item => item.SortOrder)
                     .ThenBy(item => item.Name, StringComparer.CurrentCulture))
        {
            CategoryChoices.Add(new(category.Id, category.Name));
        }
        foreach (FeedCatalogItem feed in catalog.Feeds
                     .OrderBy(item => item.SortOrder)
                     .ThenBy(item => item.DisplayName, StringComparer.CurrentCulture))
        {
            FeedChoices.Add(new(
                feed.Id,
                feed.DisplayName,
                feed.CategoryId));
        }
    }

    private SmartViewCategoryChoice FindCategory(string? id)
    {
        SmartViewCategoryChoice? result = CategoryChoices.FirstOrDefault(
            choice => string.Equals(
                choice.Id,
                id,
                StringComparison.Ordinal));
        if (result is not null)
        {
            return result;
        }
        result = new(id, $"不可用分类 · {id![..8]}");
        CategoryChoices.Add(result);
        return result;
    }

    private SmartViewFeedChoice FindFeed(string? id)
    {
        SmartViewFeedChoice? result = FeedChoices.FirstOrDefault(
            choice => string.Equals(
                choice.Id,
                id,
                StringComparison.Ordinal));
        if (result is not null)
        {
            return result;
        }
        result = new(id, $"不可用 Feed · {id![..8]}", null);
        FeedChoices.Add(result);
        return result;
    }

    private void Upsert(FeedSmartView view)
    {
        FeedSmartView? existing = SmartViews.FirstOrDefault(
            item => string.Equals(
                item.Id,
                view.Id,
                StringComparison.Ordinal));
        if (existing is not null)
        {
            SmartViews.Remove(existing);
        }
        int index = 0;
        while (index < SmartViews.Count &&
               Compare(SmartViews[index], view) <= 0)
        {
            index++;
        }
        SmartViews.Insert(index, view);
    }

    private static int Compare(
        FeedSmartView left,
        FeedSmartView right)
    {
        int sort = left.SortOrder.CompareTo(right.SortOrder);
        if (sort != 0)
        {
            return sort;
        }
        int name = string.Compare(
            left.Name,
            right.Name,
            StringComparison.Ordinal);
        return name != 0
            ? name
            : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private void DraftChanged()
    {
        PendingDeleteViewId = null;
        NotifyCommands();
    }

    private void RaiseEditorProperties()
    {
        OnPropertyChanged(nameof(ViewName));
        OnPropertyChanged(nameof(SortOrder));
        OnPropertyChanged(nameof(ViewIsEnabled));
        OnPropertyChanged(nameof(SelectedCategory));
        OnPropertyChanged(nameof(SelectedFeed));
        OnPropertyChanged(nameof(SelectedViewKind));
        OnPropertyChanged(nameof(SelectedReadFilter));
        OnPropertyChanged(nameof(FavoritesOnly));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(PublishedWithinDays));
        OnPropertyChanged(nameof(IsNewView));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(PublishLabel));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        BeginNewCommand.NotifyCanExecuteChanged();
        PublishCommand.NotifyCanExecuteChanged();
        PrepareDeleteCommand.NotifyCanExecuteChanged();
        ConfirmDeleteCommand.NotifyCanExecuteChanged();
        CancelDeleteCommand.NotifyCanExecuteChanged();
    }

    private void OnSessionChanged(
        object? sender,
        AccountSessionChangedEventArgs eventArgs)
    {
        if (_synchronizationContext is not null &&
            SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(
                _ => ApplySession(eventArgs.Session),
                null);
            return;
        }
        ApplySession(eventArgs.Session);
    }

    private void ApplySession(AccountSessionSnapshot session)
    {
        bool isAdmin = session.IsAdmin;
        if (SetProperty(ref _isAdmin, isAdmin, nameof(IsAdmin)))
        {
            NotifyCommands();
        }
        if (isAdmin)
        {
            if (SmartViews.Count == 0)
            {
                Status = "管理员权限已确认，请刷新智能视图。";
            }
            return;
        }
        SmartViews.Clear();
        ViewSetVersion = 0;
        BeginNew();
        Status = "需要管理员账号才能查看或发布共享智能视图。";
    }

    private static string FormatException(Exception exception) =>
        exception is AppException appException
            ? $"{appException.Error.UserMessage} " +
              appException.Error.Suggestion
            : $"智能视图尚未完整：{exception.Message}";
}
