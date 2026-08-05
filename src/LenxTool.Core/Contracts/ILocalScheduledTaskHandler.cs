using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

/// <summary>
/// 一个稳定计划 ID 对应一个可重放的本地任务实现。持久租约提供的是
/// 至少一次执行，因此非幂等处理器不能注册到通用计划后台。
/// </summary>
public interface ILocalScheduledTaskHandler
{
    string ScheduleId { get; }

    bool IsIdempotent { get; }

    Task ExecuteAsync(
        LocalScheduleExecution execution,
        CancellationToken cancellationToken);
}
