using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class FeedDiscoveryViewModel
{
    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        if (!CanPublish() || _adminService is null) return;
        FeedDiscoveryCandidateViewModel candidate =
            SelectedPublishCandidate!;
        long expectedVersion = CatalogVersion;
        FeedCatalogItemInput input = CreatePublishInput(candidate);
        IsPublishConfirmed = false;
        Status = $"正在向共享目录 v{expectedVersion} 提交…";

        long newVersion;
        try
        {
            newVersion = await _adminService.CreateFeedAsync(
                input,
                expectedVersion,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            InvalidatePublishingCatalog();
            if (!IsAdmin) return;
            Status = "发布已取消，远端结果未知；请刷新目录确认后再操作。";
            return;
        }
        catch (AppException exception) when (
            IsCatalogVersionConflict(exception))
        {
            await RefreshAfterPublishConflictAsync(cancellationToken);
            return;
        }
        catch (AppException exception)
        {
            if (!IsAdmin) return;
            if (exception.Error.Code is
                AppErrorCode.NetworkUnavailable
                or AppErrorCode.Timeout
                or AppErrorCode.ProviderUnavailable)
            {
                InvalidatePublishingCatalog();
                Status =
                    $"{exception.Error.Title}：发布结果未知；请刷新目录确认，系统不会自动重放写入。";
            }
            else
            {
                Status =
                    $"{exception.Error.Title}：{exception.Error.Suggestion}";
            }
            return;
        }

        try
        {
            if (!IsAdmin)
            {
                InvalidatePublishingCatalog();
                return;
            }
            await SynchronizePublishingCatalogAsync(
                newVersion,
                cancellationToken);
            FeedDiscoveryCandidateViewModel? updated = Candidates
                .FirstOrDefault(item =>
                    string.Equals(
                        item.FeedUrl,
                        candidate.FeedUrl,
                        StringComparison.Ordinal));
            SelectedPublishCandidate = updated;
            Status =
                $"已加入共享目录 v{newVersion}；候选状态已更新为“查看现有项”。";
        }
        catch (OperationCanceledException)
        {
            InvalidatePublishingCatalog();
            if (!IsAdmin) return;
            Status =
                $"远端已提交为 v{newVersion}，但本地刷新已取消；请先刷新目录，勿重复提交。";
        }
        catch (Exception)
        {
            InvalidatePublishingCatalog();
            if (!IsAdmin) return;
            Status =
                $"远端已提交为 v{newVersion}，但本地刷新失败；请先刷新目录，勿重复提交。";
        }
    }

    private void CancelPublish()
    {
        if (PublishCommand.IsRunning)
        {
            PublishCommand.Cancel();
            return;
        }
        ResetPublishingSelection();
        Status = "已取消发布确认。";
    }

    private async Task RefreshCatalogAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await SynchronizePublishingCatalogAsync(
                null,
                cancellationToken);
            Status = $"共享目录 v{CatalogVersion} 已刷新，请重新核对后确认。";
        }
        catch (OperationCanceledException)
        {
            Status = "目录刷新已取消。";
        }
        catch (AppException exception)
        {
            InvalidatePublishingCatalog();
            Status = $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
        catch (Exception)
        {
            InvalidatePublishingCatalog();
            Status = "共享目录刷新失败，请检查本地缓存和网络后重试。";
        }
    }

    private async Task RefreshAfterPublishConflictAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await SynchronizePublishingCatalogAsync(
                null,
                cancellationToken);
            Status =
                "其他管理员已更新目录；已刷新最新版本且未自动重放写入，请重新核对。";
        }
        catch (Exception)
        {
            InvalidatePublishingCatalog();
            Status =
                "目录版本冲突且刷新失败；当前写入未重放，请稍后刷新目录。";
        }
    }

    private async Task SynchronizePublishingCatalogAsync(
        long? minimumVersion,
        CancellationToken cancellationToken)
    {
        if (_catalogSync is null)
            throw new InvalidOperationException(
                "Catalog synchronization is unavailable.");
        await _catalogSync.SyncAsync(cancellationToken);
        FeedCatalogSnapshot? snapshot = await _catalogRepository
            .GetCatalogAsync(FeedCatalogScope.All, cancellationToken);
        if (snapshot is null
            || snapshot.State.Scope != FeedCatalogScope.All
            || minimumVersion is long required
                && snapshot.State.Version < required)
        {
            throw new AppException(new(
                AppErrorCode.ProviderUnavailable,
                "共享目录尚未刷新",
                "本地缓存没有达到远端写入后的目录版本。",
                "请稍后刷新目录，勿重复提交。",
                IsRetryable: true));
        }
        ApplyPublishingCatalog(snapshot);
        RefreshCandidateCatalogMatches();
    }

    private FeedCatalogItemInput CreatePublishInput(
        FeedDiscoveryCandidateViewModel candidate)
    {
        FeedViewKind? selectedKind = SelectedPublishView!.Kind;
        return new(
            candidate.FeedUrl,
            SafeDisplayName(candidate),
            IsPublishableHttpsUrl(candidate.SiteUrl)
                ? candidate.SiteUrl
                : null,
            SelectedPublishCategory!.Id,
            selectedKind ?? FeedViewKind.Article,
            SelectedPublishRefreshMinutes,
            NextPublishSortOrder(),
            true,
            SelectedPublishFullText!.Policy,
            IsViewKindExplicit: selectedKind is not null);
    }

    private int NextPublishSortOrder()
    {
        int maximum = _publishingCatalog?.Feeds
            .Select(item => item.SortOrder)
            .DefaultIfEmpty(0)
            .Max() ?? 0;
        return Math.Min(1_000_000, maximum + 100);
    }

    private static string SafeDisplayName(
        FeedDiscoveryCandidateViewModel candidate)
    {
        string source = candidate.Title.Trim();
        if (source.Length == 0
            || source.Any(char.IsControl)
            || string.Equals(
                source,
                candidate.FeedUrl,
                StringComparison.Ordinal))
        {
            source = new Uri(candidate.FeedUrl).IdnHost;
        }
        return string.Concat(source.EnumerateRunes().Take(160));
    }

    private static bool IsPublishableHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.IsDefaultPort;

    private static bool IsCatalogVersionConflict(
        AppException exception) =>
        exception.Error.Code == AppErrorCode.Conflict
        && exception.Error.TechnicalDetails?.Contains(
            "CATALOG_VERSION_CONFLICT",
            StringComparison.Ordinal) == true;
}
