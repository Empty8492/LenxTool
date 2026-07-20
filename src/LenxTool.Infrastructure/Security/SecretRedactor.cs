using System.Text.RegularExpressions;

namespace LenxTool.Infrastructure.Security;

public static partial class SecretRedactor
{
    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string redacted = BearerPattern().Replace(value, "$1[REDACTED]");
        redacted = NamedSecretPattern().Replace(redacted, "$1=[REDACTED]");
        return ServiceKeyPattern().Replace(redacted, "[REDACTED]");
    }

    [GeneratedRegex("(?i)(Bearer\\s+)[A-Za-z0-9._~+/-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(?i)\\b(api[_-]?key|password|refresh[_-]?token|access[_-]?token)\\s*[:=]\\s*[^\\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretPattern();

    [GeneratedRegex("(?i)\\b(?:gsk_[A-Za-z0-9_-]{8,}|sk-[A-Za-z0-9_-]{8,})\\b", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceKeyPattern();
}
