using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class FeedDiscoveryViewModel
{
    private FeedCatalogSnapshot? _publishingCatalog;
    private FeedDiscoveryCandidateViewModel? _selectedPublishCandidate;
    private FeedPublishCategoryChoice? _selectedPublishCategory;
    private FeedPublishViewChoice? _selectedPublishView;
    private FeedPublishFullTextChoice? _selectedPublishFullText;
    private int _selectedPublishRefreshMinutes = 60;
    private bool _isPublishConfirmed;
    private bool _isCatalogCurrent;

    public ObservableCollection<FeedPublishCategoryChoice>
        PublishCategories { get; private set; } = null!;

    public IReadOnlyList<int> PublishRefreshChoices { get; private set; } =
        null!;

    public IReadOnlyList<FeedPublishViewChoice> PublishViewChoices
    {
        get;
        private set;
    } = null!;

    public IReadOnlyList<FeedPublishFullTextChoice> PublishFullTextChoices
    {
        get;
        private set;
    } = null!;

    public RelayCommand<FeedDiscoveryCandidateViewModel>
        PreparePublishCommand { get; private set; } = null!;

    public AsyncRelayCommand PublishCommand { get; private set; } = null!;

    public RelayCommand CancelPublishCommand { get; private set; } = null!;

    public AsyncRelayCommand RefreshCatalogCommand
    {
        get;
        private set;
    } = null!;

    public bool IsPublishing => PublishCommand.IsRunning;

    public FeedDiscoveryCandidateViewModel? SelectedPublishCandidate
    {
        get => _selectedPublishCandidate;
        private set
        {
            if (!SetProperty(ref _selectedPublishCandidate, value)) return;
            NotifyPublishSummary();
        }
    }

    public bool HasPublishSelection => SelectedPublishCandidate is not null;

    public bool IsExistingSelection =>
        SelectedPublishCandidate?.IsExisting == true;

    public bool ShowPublishConfirmation =>
        HasPublishSelection && !IsExistingSelection;

    public bool CanEditPublishPolicy =>
        HasPublishSelection
        && !IsExistingSelection
        && IsAdmin
        && IsCatalogCurrent
        && !IsPublishing;

    public bool CanEditDiscoveryInput => !IsPublishing;

    public bool IsCatalogCurrent
    {
        get => _isCatalogCurrent;
        private set
        {
            if (!SetProperty(ref _isCatalogCurrent, value)) return;
            OnPropertyChanged(nameof(CanEditPublishPolicy));
            OnPropertyChanged(nameof(PublishValidationText));
            NotifyPublishingCommands();
        }
    }

    public long CatalogVersion => _publishingCatalog?.State.Version ?? 0;

    public FeedPublishCategoryChoice? SelectedPublishCategory
    {
        get => _selectedPublishCategory;
        set
        {
            if (!SetProperty(ref _selectedPublishCategory, value)) return;
            InvalidatePublishConfirmation();
            OnPropertyChanged(nameof(PublishCategoryText));
        }
    }

    public int SelectedPublishRefreshMinutes
    {
        get => _selectedPublishRefreshMinutes;
        set
        {
            if (!SetProperty(ref _selectedPublishRefreshMinutes, value)) return;
            InvalidatePublishConfirmation();
            OnPropertyChanged(nameof(PublishRefreshText));
        }
    }

    public FeedPublishViewChoice? SelectedPublishView
    {
        get => _selectedPublishView;
        set
        {
            if (!SetProperty(ref _selectedPublishView, value)) return;
            InvalidatePublishConfirmation();
            OnPropertyChanged(nameof(PublishViewText));
        }
    }

    public FeedPublishFullTextChoice? SelectedPublishFullText
    {
        get => _selectedPublishFullText;
        set
        {
            if (!SetProperty(ref _selectedPublishFullText, value)) return;
            InvalidatePublishConfirmation();
            OnPropertyChanged(nameof(PublishFullTextText));
        }
    }

    public bool IsPublishConfirmed
    {
        get => _isPublishConfirmed;
        set
        {
            if (!SetProperty(ref _isPublishConfirmed, value)) return;
            NotifyPublishingCommands();
        }
    }

    public string PublishPanelTitle =>
        IsExistingSelection ? "共享目录中的现有项" : "确认加入共享目录";

    public string PublishNormalizedUrl =>
        SelectedPublishCandidate?.FeedUrl ?? "—";

    public string PublishCategoryText =>
        SelectedPublishCategory?.Label ?? "未分类";

    public string PublishRefreshText =>
        $"每 {SelectedPublishRefreshMinutes} 分钟刷新";

    public string PublishViewText =>
        SelectedPublishView?.Label ?? "自动识别（默认文章）";

    public string PublishFullTextText =>
        SelectedPublishFullText?.Label ?? "不抓取全文";

    public string PublishValidationText =>
        IsExistingSelection
            ? "该规范化地址已经存在，不会再次提交写入。"
            : !IsCatalogCurrent
                ? "发布前必须先刷新管理员完整目录。"
                : !IsPublishableHttpsUrl(PublishNormalizedUrl)
                    ? "共享目录当前只接受规范化 HTTPS Feed 地址。"
                    : "请核对地址和全部策略，并勾选确认后发布。";

    private void InitializePublishing()
    {
        PublishCategories = [];
        PublishRefreshChoices = [15, 30, 60, 120, 360, 720, 1440];
        PublishViewChoices =
        [
            new(null, "自动识别（默认文章）"),
            new(FeedViewKind.Article, "文章"),
            new(FeedViewKind.Picture, "图片"),
            new(FeedViewKind.Audio, "音频"),
            new(FeedViewKind.Video, "视频"),
            new(FeedViewKind.Notification, "通知")
        ];
        PublishFullTextChoices =
        [
            new(FeedFullTextPolicy.None, "不抓取全文"),
            new(FeedFullTextPolicy.OnOpen, "打开文章时抓取"),
            new(FeedFullTextPolicy.Background, "后台自动抓取")
        ];
        RebuildPublishCategories(null);
        _selectedPublishView = PublishViewChoices[0];
        _selectedPublishFullText = PublishFullTextChoices[0];
        PreparePublishCommand = new(PreparePublish, CanPreparePublish);
        PublishCommand = new(PublishAsync, CanPublish);
        PublishCommand.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName != nameof(AsyncRelayCommand.IsRunning))
                return;
            OnPropertyChanged(nameof(IsPublishing));
            OnPropertyChanged(nameof(CanEditPublishPolicy));
            OnPropertyChanged(nameof(CanEditDiscoveryInput));
            NotifyCommands();
        };
        CancelPublishCommand = new(
            CancelPublish,
            () => HasPublishSelection || PublishCommand.IsRunning);
        RefreshCatalogCommand = new(
            RefreshCatalogAsync,
            () => IsAdmin
                && _catalogSync is not null
                && !PublishCommand.IsRunning);
    }

    private bool CanPreparePublish(
        FeedDiscoveryCandidateViewModel? candidate) =>
        IsAdmin
        && candidate is not null
        && !IsBusy
        && !PublishCommand.IsRunning;

    private void PreparePublish(
        FeedDiscoveryCandidateViewModel? candidate)
    {
        if (!CanPreparePublish(candidate)) return;
        SelectedPublishCandidate = candidate;
        IsPublishConfirmed = false;

        FeedCatalogItem? existing = candidate!.ExistingFeed;
        SelectedPublishCategory = FindPublishCategory(existing?.CategoryId);
        SelectedPublishRefreshMinutes =
            existing?.RefreshIntervalMinutes ?? 60;
        SelectedPublishView = FindPublishView(existing);
        SelectedPublishFullText = PublishFullTextChoices.Single(
            item => item.Policy
                == (existing?.FullTextPolicy ?? FeedFullTextPolicy.None));

        Status = existing is null
            ? "请核对规范化地址、分类、刷新和视图策略后确认发布。"
            : $"“{existing.DisplayName}”已存在于共享目录，当前不会重复写入。";
        NotifyPublishSummary();
    }

    private bool CanPublish() =>
        _adminService is not null
        && IsAdmin
        && IsCatalogCurrent
        && IsPublishConfirmed
        && SelectedPublishCandidate is { IsExisting: false }
        && SelectedPublishCategory is not null
        && SelectedPublishView is not null
        && SelectedPublishFullText is not null
        && SelectedPublishRefreshMinutes is >= 5 and <= 1440
        && IsPublishableHttpsUrl(PublishNormalizedUrl);

}
