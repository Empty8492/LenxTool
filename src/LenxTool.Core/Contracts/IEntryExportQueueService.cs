using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

/// <summary>
/// 所有导出适配器共用的持久化入口。新增适配器只实现 IEntryExporter，
/// 即可复用排队、取消、重试与历史能力。
/// </summary>
public interface IEntryExportQueueService
{
    Task<EntryExportEnqueueResult> EnqueueAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken);

    Task<EntryExportCancellationResult> CancelAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EntryExportTask?> GetAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntryExportTask>> GetRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken);
}
