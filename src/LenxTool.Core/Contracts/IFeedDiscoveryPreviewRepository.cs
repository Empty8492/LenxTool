using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

/// <summary>
/// 批量读取发现候选的本地近期预览，避免逐候选打开 SQLite 连接和物化正文。
/// </summary>
public interface IFeedDiscoveryPreviewRepository
{
    Task<IReadOnlyList<FeedDiscoveryPreviewItem>> GetRecentAsync(
        IReadOnlyCollection<string> feedIds,
        int maximumPerFeed,
        string localProfile,
        CancellationToken cancellationToken);
}
