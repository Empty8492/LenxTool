namespace LenxTool.Core.Contracts;

public interface IEntryExportQueueProcessor
{
    /// <summary>
    /// 每次最多领取并执行一个任务，进程内与数据库租约共同保证并发为一。
    /// </summary>
    Task<int> ProcessBackgroundBatchAsync(
        CancellationToken cancellationToken);
}
