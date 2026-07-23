using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class FeedAdminViewModel
{
    private readonly IFeedFetchStateRepository _fetchStateRepository;
    private readonly IFeedRefreshService _feedRefreshService;

    public ObservableCollection<FeedHealthItem> HealthItems { get; } = [];
    public AsyncRelayCommand<FeedHealthItem> RetryFeedCommand { get; }
    public string HealthSummary =>
        HealthItems.Count == 0
            ? "暂无本机 Feed 抓取状态。"
            : $"共 {HealthItems.Count} 个 Feed：健康 {HealthItems.Count(item => item.StatusLabel == "健康")}，需关注 {HealthItems.Count(item => item.State?.ConsecutiveFailures > 0)}。";

    private async Task LoadHealthAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin)
        {
            ClearHealth();
            return;
        }

        try
        {
            IReadOnlyList<FeedRefreshTarget> targets = await _fetchStateRepository
                .GetAllTargetsAsync(cancellationToken);
            if (!IsAdmin)
            {
                ClearHealth();
                return;
            }

            HealthItems.Clear();
            foreach (FeedRefreshTarget target in targets)
            {
                HealthItems.Add(new(target.Feed, target.State));
            }
            HealthStatus = $"已读取 {HealthItems.Count} 个本机 Feed 抓取状态。";
            OnPropertyChanged(nameof(HealthSummary));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (IsAdmin)
        {
            HealthItems.Clear();
            OnPropertyChanged(nameof(HealthSummary));
            HealthStatus = "抓取状态暂时不可用；目录仍可继续管理。";
        }
    }

    private async Task RetryFeedAsync(
        FeedHealthItem? item,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin || item is null || !item.CanRetry) return;

        Status = $"正在重试 Feed“{item.Feed.DisplayName}”…";
        FeedRefreshResult result;
        try
        {
            result = await _feedRefreshService.RefreshAsync(
                item.Feed.Id,
                force: true,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            Status = $"Feed“{item.Feed.DisplayName}”重试未完成；错误详情已隐藏，请稍后再试。";
            return;
        }
        Status = result.Outcome switch
        {
            FeedRefreshOutcome.Updated =>
                $"Feed“{item.Feed.DisplayName}”已抓取 {result.ParsedEntryCount} 条。",
            FeedRefreshOutcome.NotModified =>
                $"Feed“{item.Feed.DisplayName}”未变化，已更新下次抓取时间。",
            FeedRefreshOutcome.Failed =>
                $"Feed“{item.Feed.DisplayName}”重试失败，已安排下次重试。",
            FeedRefreshOutcome.SkippedUnavailable =>
                $"Feed“{item.Feed.DisplayName}”当前不可用。",
            _ => $"Feed“{item.Feed.DisplayName}”未执行抓取。"
        };
        await LoadHealthAsync(cancellationToken);
    }

    private string _healthStatus = "尚未读取本机抓取状态。";
    public string HealthStatus
    {
        get => _healthStatus;
        private set => SetProperty(ref _healthStatus, value);
    }

    private void ClearHealth()
    {
        HealthItems.Clear();
        HealthStatus = "需要管理员账号才能查看本机抓取状态。";
        OnPropertyChanged(nameof(HealthSummary));
    }
}
