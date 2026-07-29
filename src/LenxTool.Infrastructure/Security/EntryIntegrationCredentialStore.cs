using System.Security.Cryptography;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Security;

/// <summary>
/// 将集成类型和本机 TargetId 映射到不泄露原始标识的固定长度 DPAPI 槽位。
/// </summary>
public sealed class EntryIntegrationCredentialStore(
    ISecretStore secrets)
    : IEntryIntegrationCredentialStore
{
    public Task<string?> GetAsync(
        EntryIntegrationKind kind,
        string targetId,
        CancellationToken cancellationToken) =>
        secrets.GetAsync(
            CreateSlot(kind, targetId),
            cancellationToken);

    public async Task<bool> ExistsAsync(
        EntryIntegrationKind kind,
        string targetId,
        CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(
            await GetAsync(
                kind,
                targetId,
                cancellationToken).ConfigureAwait(false));

    public Task SetAsync(
        EntryIntegrationKind kind,
        string targetId,
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return secrets.SetAsync(
            CreateSlot(kind, targetId),
            value,
            cancellationToken);
    }

    public Task DeleteAsync(
        EntryIntegrationKind kind,
        string targetId,
        CancellationToken cancellationToken) =>
        secrets.DeleteAsync(
            CreateSlot(kind, targetId),
            cancellationToken);

    internal static string CreateSlot(
        EntryIntegrationKind kind,
        string targetId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (targetId.Length > 128
            || targetId.Any(char.IsControl)
            || !string.Equals(
                targetId,
                targetId.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "集成目标标识必须是长度不超过 128 的规范文本。",
                nameof(targetId));
        }

        string scope = $"{(int)kind}:{targetId}";
        string digest = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(scope)))
            .ToLowerInvariant();
        return $"int.{digest[..48]}";
    }
}
