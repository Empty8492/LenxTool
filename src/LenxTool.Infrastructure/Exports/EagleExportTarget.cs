using System.Security.Cryptography;
using System.Text;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 表示用户显式配置的 Eagle 本机 Web API 目标。队列只保存不透明的配置作用域，
/// 不把端口或其他本机信息写入公开任务标识。
/// </summary>
public sealed record EagleExportTarget(
    string TargetId,
    Uri Endpoint)
{
    private const int QueueRevisionLength = 24;
    public const string DefaultTargetId = "default";

    /// <summary>
    /// 根据规范化端点与当前资源库的不透明修订派生队列作用域；
    /// 任一目标变化后，旧队列任务都必须显式冲突失败。
    /// </summary>
    public string CreateQueueTargetId(string libraryRevision)
    {
        if (!string.Equals(
                TargetId,
                DefaultTargetId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Eagle 当前只支持默认导出目标。");
        }
        if (!IsOpaqueRevision(libraryRevision))
        {
            throw new ArgumentException(
                "Eagle 资源库修订必须是不透明的小写十六进制值。",
                nameof(libraryRevision));
        }

        return $"{TargetId}.{CreateEndpointRevision()}.{libraryRevision}";
    }

    /// <summary>
    /// 在连接本机服务前先校验队列是否仍属于当前端点；资源库部分随后由实时探测核对。
    /// </summary>
    internal bool MatchesQueueEndpoint(string? value)
    {
        if (!IsSupportedQueueTargetId(value))
        {
            return false;
        }
        return value!.StartsWith(
            $"{TargetId}.{CreateEndpointRevision()}.",
            StringComparison.Ordinal);
    }

    internal static bool IsSupportedQueueTargetId(string? value)
    {
        string prefix = $"{DefaultTargetId}.";
        return value is not null
               && value.Length == prefix.Length
                   + QueueRevisionLength
                   + 1
                   + QueueRevisionLength
               && value.StartsWith(prefix, StringComparison.Ordinal)
               && value[prefix.Length + QueueRevisionLength] == '.'
               && IsOpaqueRevision(
                   value.Substring(prefix.Length, QueueRevisionLength))
               && IsOpaqueRevision(value[(prefix.Length
                   + QueueRevisionLength
                   + 1)..]);
    }

    private string CreateEndpointRevision()
    {
        Uri normalized = EagleApiClient.ValidateEndpoint(Endpoint);
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(normalized.AbsoluteUri));
        return Convert.ToHexString(hash)
            .ToLowerInvariant()[..QueueRevisionLength];
    }

    private static bool IsOpaqueRevision(string? value) =>
        value is not null
        && value.Length == QueueRevisionLength
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');
}

/// <summary>
/// 导出期间持有的端点配置代际。目标快照与租约在同一个互斥边界内取得，
/// 释放前同进程不能保存下一代端点配置。
/// </summary>
public interface IEagleExportTargetLease : IAsyncDisposable
{
    EagleExportTarget? Target { get; }
}

/// <summary>
/// 从单一设置文档读取和原子替换当前 Eagle 本机目标。
/// </summary>
public interface IEagleExportTargetStore
{
    Task<EagleExportTarget?> GetAsync(
        CancellationToken cancellationToken);

    Task<IEagleExportTargetLease> AcquireExportLeaseAsync(
        CancellationToken cancellationToken);

    Task SaveAsync(
        EagleExportTarget target,
        CancellationToken cancellationToken);
}
