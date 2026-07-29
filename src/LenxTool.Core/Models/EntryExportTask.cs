namespace LenxTool.Core.Models;

/// <summary>
/// 持久化导出任务的生命周期。名称与界面语义保持稳定，
/// 数据库映射由基础设施层负责，避免把存储格式泄漏给调用方。
/// </summary>
public enum EntryExportTaskStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// 历史记录允许落盘的封闭错误集合。这里只保存分类，不保存异常正文、
/// 请求体或供应商响应，避免凭据和远端内容进入本地审计数据。
/// </summary>
public enum EntryExportTaskErrorCode
{
    InvalidRequest,
    ExporterNotFound,
    UnsupportedContent,
    CredentialsRequired,
    ContentTooLarge,
    RateLimited,
    DestinationUnavailable,
    AccessDenied,
    Conflict,
    ProviderRejected,
    Unknown,
    EntryMissing,
    EntryChanged
}

/// <summary>
/// 可供历史页面展示的最小任务快照。
/// </summary>
public sealed record EntryExportTask(
    string IdempotencyKey,
    string ExporterId,
    string TargetId,
    string EntryId,
    string ContentHash,
    EntryViewKind ViewKind,
    long ContentBytes,
    EntryExportTaskStatus Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    EntryExportTaskErrorCode? LastErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// 工作进程领取任务后得到的租约。租约令牌用于拒绝过期工作进程的提交。
/// </summary>
public sealed record EntryExportTaskLease(
    string IdempotencyKey,
    string ExporterId,
    string TargetId,
    string EntryId,
    string ContentHash,
    EntryViewKind ViewKind,
    long ContentBytes,
    int AttemptCount,
    string LeaseToken);

public sealed record EntryExportEnqueueResult(
    EntryExportTask Task,
    bool Created);

public enum EntryExportCancellationResult
{
    Cancelled,
    CancellationRequested,
    AlreadyTerminal,
    NotFound
}

/// <summary>
/// 导出队列固定为单并发；其余参数只控制租约、轮询和退避边界。
/// </summary>
public sealed record EntryExportQueueOptions(
    int MaximumAttempts,
    TimeSpan LeaseDuration,
    TimeSpan PollInterval,
    TimeSpan BaseRetryDelay,
    TimeSpan MaximumRetryDelay)
{
    public static EntryExportQueueOptions Default { get; } = new(
        MaximumAttempts: 5,
        LeaseDuration: TimeSpan.FromMinutes(10),
        PollInterval: TimeSpan.FromSeconds(10),
        BaseRetryDelay: TimeSpan.FromSeconds(30),
        MaximumRetryDelay: TimeSpan.FromDays(7));
}
