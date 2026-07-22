using System.Net;
using System.Text.RegularExpressions;

namespace LenxTool.Infrastructure.Networking;

internal static partial class FeedTextSanitizer
{
    public static string CleanLiteral(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string withoutExecutableBlocks = ExecutableBlockPattern().Replace(value, " ");
        string decoded = WebUtility.HtmlDecode(withoutExecutableBlocks);
        string normalized = WhitespacePattern().Replace(decoded, " ").Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    public static string Clean(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string withoutExecutableBlocks = ExecutableBlockPattern().Replace(value, " ");
        string withoutTags = TagPattern().Replace(withoutExecutableBlocks, " ");
        string decoded = WebUtility.HtmlDecode(withoutTags);
        string normalized = WhitespacePattern().Replace(decoded, " ").Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    [GeneratedRegex(
        "<(?:script|style|iframe|object|embed)\\b[^>]*>.*?</(?:script|style|iframe|object|embed)\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ExecutableBlockPattern();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TagPattern();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex WhitespacePattern();
}
