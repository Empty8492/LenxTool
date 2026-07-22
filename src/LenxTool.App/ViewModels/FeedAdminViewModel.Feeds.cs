using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class FeedAdminViewModel
{
    private void BeginNewFeed()
    {
        SelectedFeed = null;
        _applyingDiscovery = true;
        FeedUrlInput = string.Empty;
        _applyingDiscovery = false;
        FeedDisplayNameInput = string.Empty;
        FeedSiteUrlInput = string.Empty;
        SelectedCategoryId = null;
        SelectedViewKind = ViewKindChoices[0];
        FeedRefreshIntervalMinutes = 60;
        FeedSortOrder = NextSortOrder(Feeds.Select(feed => feed.SortOrder));
        FeedIsEnabled = true;
        PendingDeleteFeedId = null;
        ClearDiscoveryPreview();
        Status = "先输入站点或 Feed URL 并执行安全验证。";
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        string requestedUrl = FeedUrlInput.Trim();
        Status = "正在执行 DNS、重定向、响应大小和 XML 安全验证…";
        ClearDiscoveryPreview();
        try
        {
            FeedDiscoveryResult result = await _discoveryService.DiscoverAsync(requestedUrl, cancellationToken);
            DiscoveredFeed discovered = result.Feeds[0];
            _applyingDiscovery = true;
            FeedUrlInput = discovered.FeedUrl;
            _applyingDiscovery = false;
            if (string.IsNullOrWhiteSpace(FeedDisplayNameInput) && !string.IsNullOrWhiteSpace(discovered.Title))
                FeedDisplayNameInput = discovered.Title;
            if (string.IsNullOrWhiteSpace(FeedSiteUrlInput)
                && !string.Equals(result.RequestedUrl, discovered.FeedUrl, StringComparison.Ordinal)
                && IsValidHttpsUrl(result.RequestedUrl))
            {
                FeedSiteUrlInput = result.RequestedUrl;
            }

            _verifiedFeedUrl = discovered.FeedUrl;
            DiscoveryTitle = discovered.Title ?? "未提供标题";
            DiscoverySite = result.RequestedUrl;
            DiscoveryType = discovered.Kind == FeedDocumentKind.Rss20 ? "RSS 2.0" : "Atom";
            bool workerCompatible = IsValidHttpsUrl(discovered.FeedUrl);
            DiscoveryWarning = !workerCompatible
                ? "本机信任策略允许该地址，但当前 Worker v1 仅接受 HTTPS，暂不能保存。"
                : result.Feeds.Count > 1
                    ? $"发现 {result.Feeds.Count} 个有效订阅，当前选择第一个；保存前请核对地址。"
                    : "安全检查通过；保存时 Worker 仍会校验管理员角色与目录版本。";
            HasDiscoveryPreview = true;
            Status = workerCompatible ? "Feed 已验证，可以保存。" : "Feed 已验证，但不符合 Worker v1 的 HTTPS 契约。";
        }
        catch (AppException exception)
        {
            ClearDiscoveryPreview();
            Status = $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
    }

    private Task SaveFeedAsync(CancellationToken cancellationToken)
    {
        FeedCatalogItemInput input = CreateFeedInput(
            FeedUrlInput.Trim(),
            FeedDisplayNameInput.Trim(),
            NullIfWhiteSpace(FeedSiteUrlInput),
            SelectedCategoryId,
            SelectedViewKind.Kind,
            FeedRefreshIntervalMinutes,
            FeedSortOrder,
            FeedIsEnabled);
        return ExecuteMutationAsync(
            (version, token) => SelectedFeed is null
                ? _adminService.CreateFeedAsync(input, version, token)
                : _adminService.UpdateFeedAsync(SelectedFeed.Id, input, version, token),
            "Feed 已保存并同步。",
            cancellationToken);
    }

    private Task ToggleFeedAsync(FeedCatalogItem? feed, CancellationToken cancellationToken) =>
        feed is null
            ? Task.CompletedTask
            : ExecuteMutationAsync(
                (version, token) => _adminService.UpdateFeedAsync(
                    feed.Id,
                    FromFeed(feed, isEnabled: !feed.IsEnabled),
                    version,
                    token),
                feed.IsEnabled ? "Feed 已停用。" : "Feed 已启用。",
                cancellationToken);

    private Task MoveFeedAsync(FeedCatalogItem? feed, int direction, CancellationToken cancellationToken)
    {
        int index = IndexOf(Feeds, feed?.Id);
        if (index < 0 || index + direction < 0 || index + direction >= Feeds.Count)
            return Task.CompletedTask;
        int sortOrder = OrderAround(Feeds[index + direction].SortOrder, direction);
        return ExecuteMutationAsync(
            (version, token) => _adminService.UpdateFeedAsync(
                feed!.Id,
                FromFeed(feed, sortOrder: sortOrder),
                version,
                token),
            "Feed 排序已更新。",
            cancellationToken);
    }

    private void PrepareDeleteFeed(FeedCatalogItem? feed)
    {
        if (feed is null) return;
        SelectedFeed = feed;
        PendingDeleteFeedId = feed.Id;
        Status = $"再次确认将删除 Feed“{feed.DisplayName}”；已缓存文章会继续保留。";
    }

    private Task ConfirmDeleteFeedAsync(CancellationToken cancellationToken)
    {
        string? id = PendingDeleteFeedId;
        if (id is null) return Task.CompletedTask;
        return ExecuteMutationAsync(
            (version, token) => _adminService.DeleteFeedAsync(id, version, token),
            "Feed 已从共享目录删除；已缓存文章继续保留。",
            cancellationToken);
    }

    private void CancelDeleteFeed()
    {
        PendingDeleteFeedId = null;
        Status = "已取消删除 Feed。";
    }
}
