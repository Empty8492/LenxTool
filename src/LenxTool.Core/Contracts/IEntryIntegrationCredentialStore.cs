using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

/// <summary>
/// 按集成类型和本机目标隔离个人凭据；实现不得把明文写入普通设置或共享存储。
/// </summary>
public interface IEntryIntegrationCredentialStore
{
    Task<string?> GetAsync(
        EntryIntegrationKind kind,
        string targetId,
        CancellationToken cancellationToken);

    Task<bool> ExistsAsync(
        EntryIntegrationKind kind,
        string targetId,
        CancellationToken cancellationToken);

    Task SetAsync(
        EntryIntegrationKind kind,
        string targetId,
        string value,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        EntryIntegrationKind kind,
        string targetId,
        CancellationToken cancellationToken);
}
