using LenxTool.Core.Accounts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class FeedAdminViewModel
{
    private async Task ExecuteMutationAsync(
        Func<long, CancellationToken, Task<long>> mutation,
        string successMessage,
        CancellationToken cancellationToken)
    {
        if (!CanManage) return;
        Status = "正在提交共享目录更改…";
        long newVersion;
        try
        {
            newVersion = await mutation(CatalogVersion, cancellationToken);
        }
        catch (AppException exception) when (IsCatalogVersionConflict(exception))
        {
            await RefreshAfterConflictAsync(cancellationToken);
            return;
        }
        catch (AppException exception)
        {
            Status = $"{exception.Error.Title}：{exception.Error.Suggestion}";
            return;
        }

        PendingDeleteCategoryId = null;
        PendingDeleteFeedId = null;
        try
        {
            await _catalogSync.SyncAsync(cancellationToken);
            await LoadCatalogAsync(newVersion, cancellationToken);
            if (_catalogIsCurrent) Status = successMessage;
        }
        catch (AppException exception)
        {
            _catalogIsCurrent = false;
            NotifyAllCommands();
            Status = $"远端更改已提交为 v{newVersion}，但本地刷新失败：{exception.Error.Suggestion}";
        }
    }

    private async Task RefreshAfterConflictAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _catalogSync.SyncAsync(cancellationToken);
            await LoadCatalogAsync(null, cancellationToken);
            Status = "其他管理员已更新目录；已刷新最新版本，请核对表单后重试。";
        }
        catch (AppException exception)
        {
            _catalogIsCurrent = false;
            NotifyAllCommands();
            Status = $"目录版本冲突且刷新失败：{exception.Error.Suggestion}";
        }
    }

    private async Task LoadCatalogAsync(long? minimumVersion, CancellationToken cancellationToken)
    {
        if (!IsAdmin)
        {
            ClearAdminCatalog();
            return;
        }
        FeedCatalogSnapshot? snapshot = await _repository.GetCatalogAsync(
            FeedCatalogScope.All,
            cancellationToken);
        if (!IsAdmin)
        {
            ClearAdminCatalog();
            return;
        }
        if (snapshot is null)
        {
            _catalogIsCurrent = false;
            ClearHealth();
            Status = "尚无管理员完整目录缓存，请刷新后再编辑。";
            NotifyAllCommands();
            return;
        }

        string? selectedCategoryId = SelectedCategory?.Id;
        string? selectedFeedId = SelectedFeed?.Id;
        Categories.Clear();
        foreach (FeedCategory category in snapshot.Categories) Categories.Add(category);
        Feeds.Clear();
        foreach (FeedCatalogItem feed in snapshot.Feeds) Feeds.Add(feed);
        CategoryChoices.Clear();
        CategoryChoices.Add(new(null, "未分类", true));
        foreach (FeedCategory category in snapshot.Categories)
            CategoryChoices.Add(new(category.Id, category.Name, category.IsEnabled));
        SetAiPolicyDefaults(snapshot.AiPolicyDefaults);

        SetProperty(ref _catalogVersion, snapshot.State.Version, nameof(CatalogVersion));
        if (_opmlPreviewCatalogVersion is not null && _opmlPreviewCatalogVersion != snapshot.State.Version)
            ClearOpmlPreview();
        _catalogIsCurrent = snapshot.State.Scope == FeedCatalogScope.All
            && (minimumVersion is null || snapshot.State.Version >= minimumVersion.Value);
        SelectedCategory = Categories.FirstOrDefault(category => category.Id == selectedCategoryId);
        if (selectedCategoryId is not null && SelectedCategory is null) BeginNewCategory();
        SelectedFeed = Feeds.FirstOrDefault(feed => feed.Id == selectedFeedId);
        if (selectedFeedId is not null && SelectedFeed is null) BeginNewFeed();
        if (!_catalogIsCurrent && minimumVersion is not null)
            Status = "远端写入已完成，但本地目录尚未刷新到新版本；请刷新后继续。";
        NotifyAllCommands();
        await LoadHealthAsync(cancellationToken);
    }

    private void ApplyFeed(FeedCatalogItem feed)
    {
        _applyingDiscovery = true;
        FeedUrlInput = feed.OriginalUrl;
        _applyingDiscovery = false;
        FeedDisplayNameInput = feed.DisplayName;
        FeedSiteUrlInput = feed.SiteUrl ?? string.Empty;
        SelectedCategoryId = feed.CategoryId;
        SelectedViewKind = ViewKindChoices.First(choice => choice.Kind == feed.ViewKind);
        SelectedFullTextPolicy = FullTextPolicyChoices.First(
            choice => choice.Policy == feed.FullTextPolicy);
        FeedRefreshIntervalMinutes = feed.RefreshIntervalMinutes;
        FeedSortOrder = feed.SortOrder;
        FeedIsEnabled = feed.IsEnabled;
        ApplyFeedAiPolicy(feed.AiPolicy);
        PendingDeleteFeedId = null;
        _verifiedFeedUrl = feed.OriginalUrl;
        DiscoveryTitle = feed.DisplayName;
        DiscoverySite = feed.SiteUrl ?? "未提供站点地址";
        DiscoveryType = "已保存订阅";
        DiscoveryWarning = "当前 URL 已由共享目录保存；修改 URL 后必须重新安全验证。";
        HasDiscoveryPreview = true;
    }

    private void ClearDiscoveryPreview()
    {
        _verifiedFeedUrl = null;
        HasDiscoveryPreview = false;
        DiscoveryTitle = "尚未验证";
        DiscoverySite = "—";
        DiscoveryType = "—";
        DiscoveryWarning = "输入站点或 Feed URL 后先执行安全验证。";
    }

    private bool CanSaveCategory() => CanManage
        && IsValidText(CategoryNameInput, 80)
        && CategorySortOrder is >= 0 and <= MaximumSortOrder
        && IsValidAiPolicy(CreateCategoryAiPolicy());

    private bool CanDiscover() => IsAdmin && !string.IsNullOrWhiteSpace(FeedUrlInput);

    private bool CanSaveFeed() => CanManage
        && HasDiscoveryPreview
        && string.Equals(_verifiedFeedUrl, FeedUrlInput.Trim(), StringComparison.Ordinal)
        && IsValidHttpsUrl(FeedUrlInput)
        && IsValidText(FeedDisplayNameInput, 160)
        && (string.IsNullOrWhiteSpace(FeedSiteUrlInput) || IsValidHttpsUrl(FeedSiteUrlInput))
        && (SelectedCategoryId is null || Guid.TryParseExact(SelectedCategoryId, "D", out _))
        && FeedRefreshIntervalMinutes is >= 5 and <= 1440
        && FeedSortOrder is >= 0 and <= MaximumSortOrder
        && IsValidAiPolicy(CreateFeedAiPolicy());

    private bool CanMoveCategory(FeedCategory? category, int direction)
    {
        int index = IndexOf(Categories, category?.Id);
        return CanManage && index >= 0 && index + direction >= 0 && index + direction < Categories.Count;
    }

    private bool CanMoveFeed(FeedCatalogItem? feed, int direction)
    {
        int index = IndexOf(Feeds, feed?.Id);
        return CanManage && index >= 0 && index + direction >= 0 && index + direction < Feeds.Count;
    }

    private void ApplySession(AccountSessionSnapshot session)
    {
        if (!SetProperty(ref _isAdmin, session.IsAdmin, nameof(IsAdmin))) return;
        OnPropertyChanged(nameof(CanManage));
        if (!session.IsAdmin)
        {
            ClearAdminCatalog();
            return;
        }
        NotifyAllCommands();
    }

    private void ClearAdminCatalog()
    {
        _catalogIsCurrent = false;
        ClearOpmlPreview();
        Categories.Clear();
        Feeds.Clear();
        CategoryChoices.Clear();
        SetAiPolicyDefaults(null);
        ClearHealth();
        SelectedCategory = null;
        SelectedFeed = null;
        Status = "需要管理员账号才能读取和修改共享订阅目录。";
        NotifyAllCommands();
    }

    private void OnSessionChanged(object? sender, AccountSessionChangedEventArgs eventArgs) =>
        ApplySession(eventArgs.Session);

    private void NotifyAllCommands()
    {
        OnPropertyChanged(nameof(CanManage));
        RefreshCommand.NotifyCanExecuteChanged();
        BeginNewCategoryCommand.NotifyCanExecuteChanged();
        ToggleCategoryCommand.NotifyCanExecuteChanged();
        MoveCategoryUpCommand.NotifyCanExecuteChanged();
        MoveCategoryDownCommand.NotifyCanExecuteChanged();
        PrepareDeleteCategoryCommand.NotifyCanExecuteChanged();
        BeginNewFeedCommand.NotifyCanExecuteChanged();
        ToggleFeedCommand.NotifyCanExecuteChanged();
        MoveFeedUpCommand.NotifyCanExecuteChanged();
        MoveFeedDownCommand.NotifyCanExecuteChanged();
        PrepareDeleteFeedCommand.NotifyCanExecuteChanged();
        NotifyCategoryCommands();
        NotifyFeedCommands();
        NotifyOpmlCommands();
        RetryFeedCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCategoryCommands()
    {
        SaveCategoryCommand.NotifyCanExecuteChanged();
        ConfirmDeleteCategoryCommand.NotifyCanExecuteChanged();
        CancelDeleteCategoryCommand.NotifyCanExecuteChanged();
    }

    private void NotifyFeedCommands()
    {
        DiscoverCommand.NotifyCanExecuteChanged();
        SaveFeedCommand.NotifyCanExecuteChanged();
        ConfirmDeleteFeedCommand.NotifyCanExecuteChanged();
        CancelDeleteFeedCommand.NotifyCanExecuteChanged();
    }

    private static FeedCatalogItemInput FromFeed(
        FeedCatalogItem feed,
        int? sortOrder = null,
        bool? isEnabled = null) => CreateFeedInput(
            feed.OriginalUrl,
            feed.DisplayName,
            feed.SiteUrl,
            feed.CategoryId,
            feed.ViewKind,
            feed.FullTextPolicy,
            feed.RefreshIntervalMinutes,
            sortOrder ?? feed.SortOrder,
            isEnabled ?? feed.IsEnabled,
            feed.AiPolicy);

    private static FeedCatalogItemInput CreateFeedInput(
        string originalUrl,
        string displayName,
        string? siteUrl,
        string? categoryId,
        FeedViewKind viewKind,
        FeedFullTextPolicy fullTextPolicy,
        int refreshIntervalMinutes,
        int sortOrder,
        bool isEnabled,
        FeedAiPolicy? aiPolicy) => new(
            originalUrl,
            displayName,
            siteUrl,
            categoryId,
            viewKind,
            refreshIntervalMinutes,
            sortOrder,
            isEnabled,
            fullTextPolicy,
            aiPolicy);

    private static bool IsCatalogVersionConflict(AppException exception) =>
        exception.Error.Code == AppErrorCode.Conflict
        && exception.Error.TechnicalDetails?.Contains(
            "CATALOG_VERSION_CONFLICT",
            StringComparison.Ordinal) == true;

    private static bool IsValidText(string value, int maximumCodePoints)
    {
        string trimmed = value.Trim();
        return trimmed.Length > 0
            && trimmed.EnumerateRunes().Count() <= maximumCodePoints
            && !trimmed.Any(char.IsControl);
    }

    private static bool IsValidHttpsUrl(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.IsDefaultPort;

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int NextSortOrder(IEnumerable<int> values) =>
        Math.Min(MaximumSortOrder, values.DefaultIfEmpty(0).Max() + 100);

    private static int OrderAround(int adjacentOrder, int direction) => direction < 0
        ? Math.Max(0, adjacentOrder - 1)
        : Math.Min(MaximumSortOrder, adjacentOrder + 1);

    private static int IndexOf<T>(IEnumerable<T> items, string? id) where T : notnull
    {
        if (id is null) return -1;
        int index = 0;
        foreach (T item in items)
        {
            string itemId = item switch
            {
                FeedCategory category => category.Id,
                FeedCatalogItem feed => feed.Id,
                _ => string.Empty
            };
            if (string.Equals(itemId, id, StringComparison.Ordinal)) return index;
            index++;
        }
        return -1;
    }
}
