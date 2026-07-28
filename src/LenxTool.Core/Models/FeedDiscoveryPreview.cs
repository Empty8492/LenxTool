namespace LenxTool.Core.Models;

/// <summary>
/// 发现候选的本地近期条目投影；仅包含卡片需要的标题和时间，不携带正文。
/// </summary>
public sealed record FeedDiscoveryPreviewItem(
    string FeedId,
    string Title,
    DateTimeOffset PublishedAt);
