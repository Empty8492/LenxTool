using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

/// <summary>
/// 读取共享集成策略，并允许管理员按条件版本原子替换完整策略集。
/// </summary>
public interface IEntryIntegrationPolicyService
{
    Task<EntryIntegrationPolicySnapshot> GetAsync(
        EntryIntegrationPolicyScope scope,
        CancellationToken cancellationToken);

    Task<EntryIntegrationPolicyMutationResult> ReplaceAsync(
        IReadOnlyList<EntryIntegrationPolicyInput> inputs,
        long expectedVersion,
        CancellationToken cancellationToken);
}
