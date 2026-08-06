using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

/// <summary>
/// 把“外部模型调用已开始”先落盘，并在同一 SQLite 事务中提交报告与窗口终态。
/// DeepSeek 不提供可依赖的幂等键，因此崩溃或网络结果未知时选择停止自动重放，
/// 以丢失一次摘要换取不重复计费。
/// </summary>
public interface IFeedDigestExecutionStore
{
    Task<FeedDigestExecutionBeginResult> BeginAsync(
        LocalScheduleRunLease lease,
        string reportId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken);

    Task ClearForSafeRetryAsync(
        LocalScheduleRunLease lease,
        string reportId,
        DateTimeOffset clearedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        LocalScheduleRunLease lease,
        AiReport report,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    Task AbandonUncertainAsync(
        LocalScheduleRunLease lease,
        string reportId,
        DateTimeOffset abandonedAtUtc,
        CancellationToken cancellationToken);
}
