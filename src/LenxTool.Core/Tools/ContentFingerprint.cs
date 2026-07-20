using System.Security.Cryptography;
using System.Text;

namespace LenxTool.Core.Tools;

public static class ContentFingerprint
{
    public static string Create(params string?[] values)
    {
        string normalized = string.Join(
            '\n',
            values.Select(value => (value ?? string.Empty).Trim().ToUpperInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
