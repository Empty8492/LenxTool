using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record FeedCategoryChoice(string? Id, string Name, bool IsEnabled);

public sealed record FeedViewKindChoice(FeedViewKind Kind, string Label);
public sealed record FeedFullTextPolicyChoice(FeedFullTextPolicy Policy, string Label);

public sealed partial class FeedAdminViewModel : PageViewModel
{
    private const int MaximumSortOrder = 1_000_000;
    private readonly IFeedCatalogAdminService _adminService;
    private readonly IFeedCatalogRepository _repository;
    private readonly IFeedCatalogSyncService _catalogSync;
    private readonly IFeedDiscoveryService _discoveryService;
    private readonly IAccountSessionService _accountSession;
    private readonly IFeedCatalogBatchService _batchService;
    private readonly IOpmlFileService _opmlFileService;
    private readonly IOpmlFileDialogService _opmlFileDialogs;
    private bool _isAdmin;
    private bool _catalogIsCurrent;
    private long _catalogVersion;
    private string _status = "正在读取共享目录…";
    private FeedCategory? _selectedCategory;
    private string _categoryNameInput = string.Empty;
    private int _categorySortOrder = 100;
    private bool _categoryIsEnabled = true;
    private string? _pendingDeleteCategoryId;
    private FeedCatalogItem? _selectedFeed;
    private string _feedUrlInput = string.Empty;
    private string _feedDisplayNameInput = string.Empty;
    private string _feedSiteUrlInput = string.Empty;
    private string? _selectedCategoryId;
    private FeedViewKindChoice _selectedViewKind;
    private FeedFullTextPolicyChoice _selectedFullTextPolicy;
    private int _feedRefreshIntervalMinutes = 60;
    private int _feedSortOrder = 100;
    private bool _feedIsEnabled = true;
    private string? _pendingDeleteFeedId;
    private bool _hasDiscoveryPreview;
    private string _discoveryTitle = "尚未验证";
    private string _discoverySite = "—";
    private string _discoveryType = "—";
    private string _discoveryWarning = "输入站点或 Feed URL 后先执行安全验证。";
    private string? _verifiedFeedUrl;
    private bool _applyingDiscovery;

    public FeedAdminViewModel(
        IFeedCatalogAdminService adminService,
        IFeedCatalogRepository repository,
        IFeedCatalogSyncService catalogSync,
        IFeedDiscoveryService discoveryService,
        IAccountSessionService accountSession,
        IFeedCatalogBatchService batchService,
        IOpmlFileService opmlFileService,
        IOpmlFileDialogService opmlFileDialogs,
        IFeedFetchStateRepository fetchStateRepository,
        IFeedRefreshService feedRefreshService)
        : base("订阅管理", "维护所有用户共享的 RSS/Atom 目录；权限与版本仍由 Worker 强制校验")
    {
        _adminService = adminService;
        _repository = repository;
        _catalogSync = catalogSync;
        _discoveryService = discoveryService;
        _accountSession = accountSession;
        _batchService = batchService;
        _opmlFileService = opmlFileService;
        _opmlFileDialogs = opmlFileDialogs;
        _fetchStateRepository = fetchStateRepository;
        _feedRefreshService = feedRefreshService;
        Categories = [];
        Feeds = [];
        OpmlItems = [];
        CategoryChoices = [];
        ViewKindChoices =
        [
            new(FeedViewKind.Article, "文章"),
            new(FeedViewKind.Picture, "图片"),
            new(FeedViewKind.Audio, "音频"),
            new(FeedViewKind.Video, "视频"),
            new(FeedViewKind.Notification, "通知")
        ];
        _selectedViewKind = ViewKindChoices[0];
        FullTextPolicyChoices =
        [
            new(FeedFullTextPolicy.None, "不抓取全文"),
            new(FeedFullTextPolicy.OnOpen, "打开文章时抓取"),
            new(FeedFullTextPolicy.Background, "后台自动抓取")
        ];
        _selectedFullTextPolicy = FullTextPolicyChoices[0];

        RefreshCommand = new(RefreshAsync, () => IsAdmin);
        BeginNewCategoryCommand = new(BeginNewCategory, () => CanManage);
        SaveCategoryCommand = new(SaveCategoryAsync, CanSaveCategory);
        ToggleCategoryCommand = new AsyncRelayCommand<FeedCategory>(ToggleCategoryAsync, item => CanManage && item is not null);
        MoveCategoryUpCommand = new AsyncRelayCommand<FeedCategory>(
            (item, token) => MoveCategoryAsync(item, -1, token),
            item => CanMoveCategory(item, -1));
        MoveCategoryDownCommand = new AsyncRelayCommand<FeedCategory>(
            (item, token) => MoveCategoryAsync(item, 1, token),
            item => CanMoveCategory(item, 1));
        PrepareDeleteCategoryCommand = new RelayCommand<FeedCategory>(PrepareDeleteCategory, item => CanManage && item is not null);
        ConfirmDeleteCategoryCommand = new(ConfirmDeleteCategoryAsync, () => CanManage && PendingDeleteCategoryId is not null);
        CancelDeleteCategoryCommand = new(CancelDeleteCategory, () => PendingDeleteCategoryId is not null);

        BeginNewFeedCommand = new(BeginNewFeed, () => CanManage);
        DiscoverCommand = new(DiscoverAsync, CanDiscover);
        SaveFeedCommand = new(SaveFeedAsync, CanSaveFeed);
        ToggleFeedCommand = new AsyncRelayCommand<FeedCatalogItem>(ToggleFeedAsync, item => CanManage && item is not null);
        MoveFeedUpCommand = new AsyncRelayCommand<FeedCatalogItem>(
            (item, token) => MoveFeedAsync(item, -1, token),
            item => CanMoveFeed(item, -1));
        MoveFeedDownCommand = new AsyncRelayCommand<FeedCatalogItem>(
            (item, token) => MoveFeedAsync(item, 1, token),
            item => CanMoveFeed(item, 1));
        PrepareDeleteFeedCommand = new RelayCommand<FeedCatalogItem>(PrepareDeleteFeed, item => CanManage && item is not null);
        ConfirmDeleteFeedCommand = new(ConfirmDeleteFeedAsync, () => CanManage && PendingDeleteFeedId is not null);
        CancelDeleteFeedCommand = new(CancelDeleteFeed, () => PendingDeleteFeedId is not null);
        PreviewOpmlCommand = new(PreviewOpmlAsync, () => CanManage && !_isOpmlBusy);
        ImportSelectedOpmlCommand = new(ImportSelectedOpmlAsync, CanImportSelectedOpml);
        SelectAllNewOpmlCommand = new(SelectAllNewOpml, () => HasOpmlPreview && !_isOpmlBusy);
        ClearOpmlSelectionCommand = new(ClearOpmlSelection, () => HasOpmlPreview && !_isOpmlBusy);
        ExportOpmlCommand = new(ExportOpmlAsync, () => CanManage && Feeds.Count > 0 && !_isOpmlBusy);
        RetryFeedCommand = new(RetryFeedAsync, item => IsAdmin && item is not null && item.CanRetry);

        _accountSession.SessionChanged += OnSessionChanged;
        ApplySession(_accountSession.Current);
    }

    public ObservableCollection<FeedCategory> Categories { get; }
    public ObservableCollection<FeedCatalogItem> Feeds { get; }
    public ObservableCollection<FeedCategoryChoice> CategoryChoices { get; }
    public ObservableCollection<OpmlImportItemViewModel> OpmlItems { get; }
    public IReadOnlyList<FeedViewKindChoice> ViewKindChoices { get; }
    public IReadOnlyList<FeedFullTextPolicyChoice> FullTextPolicyChoices { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand BeginNewCategoryCommand { get; }
    public AsyncRelayCommand SaveCategoryCommand { get; }
    public AsyncRelayCommand<FeedCategory> ToggleCategoryCommand { get; }
    public AsyncRelayCommand<FeedCategory> MoveCategoryUpCommand { get; }
    public AsyncRelayCommand<FeedCategory> MoveCategoryDownCommand { get; }
    public RelayCommand<FeedCategory> PrepareDeleteCategoryCommand { get; }
    public AsyncRelayCommand ConfirmDeleteCategoryCommand { get; }
    public RelayCommand CancelDeleteCategoryCommand { get; }
    public RelayCommand BeginNewFeedCommand { get; }
    public AsyncRelayCommand DiscoverCommand { get; }
    public AsyncRelayCommand SaveFeedCommand { get; }
    public AsyncRelayCommand<FeedCatalogItem> ToggleFeedCommand { get; }
    public AsyncRelayCommand<FeedCatalogItem> MoveFeedUpCommand { get; }
    public AsyncRelayCommand<FeedCatalogItem> MoveFeedDownCommand { get; }
    public RelayCommand<FeedCatalogItem> PrepareDeleteFeedCommand { get; }
    public AsyncRelayCommand ConfirmDeleteFeedCommand { get; }
    public RelayCommand CancelDeleteFeedCommand { get; }
    public AsyncRelayCommand PreviewOpmlCommand { get; }
    public AsyncRelayCommand ImportSelectedOpmlCommand { get; }
    public RelayCommand SelectAllNewOpmlCommand { get; }
    public RelayCommand ClearOpmlSelectionCommand { get; }
    public AsyncRelayCommand ExportOpmlCommand { get; }

    public bool IsAdmin => _isAdmin;
    public bool CanManage => IsAdmin && _catalogIsCurrent;
    public bool HasOpmlPreview => OpmlItems.Count > 0;
    public int SelectedOpmlCount => OpmlItems.Count(item => item.IsSelected);
    public string OpmlSummary => HasOpmlPreview
        ? $"共 {OpmlItems.Count} 项：新增 {OpmlItems.Count(item => item.Status == OpmlCatalogItemStatus.New)}，重复 {OpmlItems.Count(item => item.Status == OpmlCatalogItemStatus.Duplicate)}，冲突 {OpmlItems.Count(item => item.Status == OpmlCatalogItemStatus.Conflict)}，无效 {OpmlItems.Count(item => item.Status == OpmlCatalogItemStatus.Invalid)}；已选 {SelectedOpmlCount}。"
        : "尚未选择 OPML 文件；预览不会自动写入共享目录。";
    public long CatalogVersion => _catalogVersion;
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public FeedCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value) || value is null) return;
            CategoryNameInput = value.Name;
            CategorySortOrder = value.SortOrder;
            CategoryIsEnabled = value.IsEnabled;
            PendingDeleteCategoryId = null;
        }
    }

    public string CategoryNameInput
    {
        get => _categoryNameInput;
        set
        {
            if (SetProperty(ref _categoryNameInput, value ?? string.Empty)) NotifyCategoryCommands();
        }
    }

    public int CategorySortOrder
    {
        get => _categorySortOrder;
        set
        {
            if (SetProperty(ref _categorySortOrder, value)) NotifyCategoryCommands();
        }
    }

    public bool CategoryIsEnabled
    {
        get => _categoryIsEnabled;
        set => SetProperty(ref _categoryIsEnabled, value);
    }

    public string? PendingDeleteCategoryId
    {
        get => _pendingDeleteCategoryId;
        private set
        {
            if (SetProperty(ref _pendingDeleteCategoryId, value))
            {
                OnPropertyChanged(nameof(IsCategoryDeletePending));
                ConfirmDeleteCategoryCommand.NotifyCanExecuteChanged();
                CancelDeleteCategoryCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsCategoryDeletePending => PendingDeleteCategoryId is not null;

    public FeedCatalogItem? SelectedFeed
    {
        get => _selectedFeed;
        set
        {
            if (!SetProperty(ref _selectedFeed, value) || value is null) return;
            ApplyFeed(value);
        }
    }

    public string FeedUrlInput
    {
        get => _feedUrlInput;
        set
        {
            if (!SetProperty(ref _feedUrlInput, value ?? string.Empty)) return;
            if (!_applyingDiscovery && !string.Equals(_verifiedFeedUrl, _feedUrlInput, StringComparison.Ordinal))
                ClearDiscoveryPreview();
            NotifyFeedCommands();
        }
    }

    public string FeedDisplayNameInput
    {
        get => _feedDisplayNameInput;
        set
        {
            if (SetProperty(ref _feedDisplayNameInput, value ?? string.Empty)) NotifyFeedCommands();
        }
    }

    public string FeedSiteUrlInput
    {
        get => _feedSiteUrlInput;
        set
        {
            if (SetProperty(ref _feedSiteUrlInput, value ?? string.Empty)) NotifyFeedCommands();
        }
    }

    public string? SelectedCategoryId
    {
        get => _selectedCategoryId;
        set
        {
            if (SetProperty(ref _selectedCategoryId, value)) NotifyFeedCommands();
        }
    }

    public FeedViewKindChoice SelectedViewKind
    {
        get => _selectedViewKind;
        set
        {
            if (SetProperty(ref _selectedViewKind, value ?? ViewKindChoices[0])) NotifyFeedCommands();
        }
    }

    public FeedFullTextPolicyChoice SelectedFullTextPolicy
    {
        get => _selectedFullTextPolicy;
        set
        {
            if (SetProperty(
                ref _selectedFullTextPolicy,
                value ?? FullTextPolicyChoices[0]))
            {
                NotifyFeedCommands();
            }
        }
    }

    public int FeedRefreshIntervalMinutes
    {
        get => _feedRefreshIntervalMinutes;
        set
        {
            if (SetProperty(ref _feedRefreshIntervalMinutes, value)) NotifyFeedCommands();
        }
    }

    public int FeedSortOrder
    {
        get => _feedSortOrder;
        set
        {
            if (SetProperty(ref _feedSortOrder, value)) NotifyFeedCommands();
        }
    }

    public bool FeedIsEnabled
    {
        get => _feedIsEnabled;
        set => SetProperty(ref _feedIsEnabled, value);
    }

    public string? PendingDeleteFeedId
    {
        get => _pendingDeleteFeedId;
        private set
        {
            if (SetProperty(ref _pendingDeleteFeedId, value))
            {
                OnPropertyChanged(nameof(IsFeedDeletePending));
                ConfirmDeleteFeedCommand.NotifyCanExecuteChanged();
                CancelDeleteFeedCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsFeedDeletePending => PendingDeleteFeedId is not null;
    public bool HasDiscoveryPreview
    {
        get => _hasDiscoveryPreview;
        private set
        {
            if (SetProperty(ref _hasDiscoveryPreview, value)) NotifyFeedCommands();
        }
    }
    public string DiscoveryTitle
    {
        get => _discoveryTitle;
        private set => SetProperty(ref _discoveryTitle, value);
    }
    public string DiscoverySite
    {
        get => _discoverySite;
        private set => SetProperty(ref _discoverySite, value);
    }
    public string DiscoveryType
    {
        get => _discoveryType;
        private set => SetProperty(ref _discoveryType, value);
    }
    public string DiscoveryWarning
    {
        get => _discoveryWarning;
        private set => SetProperty(ref _discoveryWarning, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ApplySession(_accountSession.Current);
        if (!IsAdmin)
        {
            Status = "需要管理员账号才能读取和修改共享订阅目录。";
            return;
        }
        await LoadCatalogAsync(null, cancellationToken);
        if (_catalogIsCurrent) Status = $"共享目录 v{CatalogVersion} 已载入。";
    }

}
