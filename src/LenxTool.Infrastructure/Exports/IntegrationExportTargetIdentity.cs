using System.Security.Cryptography;
using System.Text;

namespace LenxTool.Infrastructure.Exports;

internal static class IntegrationExportTargetIdentity
{
    private const int RevisionLength = 24;

    public static string Create(
        string targetId,
        params string[] canonicalFields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(canonicalFields);
        string canonical = string.Join('\n', ["v1", .. canonicalFields]);
        string revision = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..RevisionLength];
        return $"{targetId}.{revision}";
    }

    public static bool IsSupported(string? value, string targetId)
    {
        string prefix = $"{targetId}.";
        return value is not null
            && value.Length == prefix.Length + RevisionLength
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && value[prefix.Length..].All(character =>
                character is >= '0' and <= '9'
                or >= 'a' and <= 'f');
    }
}
