using System.Security.Cryptography;
using System.Text;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 集中执行与宿主平台无关的 Windows 文件名清理和根目录包含校验。
/// </summary>
internal static class MarkdownExportPathPolicy
{
    // 为“--v-{20 位幂等摘要}.md”预留空间，使任何模式的文件名都不超过 120 字符。
    private const int MaximumStemLength = 92;
    private const int EntryKeyLength = 12;
    private static readonly char[] InvalidWindowsFileNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> ReservedWindowsNames =
        new(
            [
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5",
                "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
                "LPT6", "LPT7", "LPT8", "LPT9"
            ],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 标题只参与可读部分，稳定条目摘要用于隔离同名条目；
    /// 预留版本后缀空间可避免 Windows 常见路径上限被标题轻易耗尽。
    /// </summary>
    public static string CreateFileStem(
        string? title,
        string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        string entryKey = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(entryId)))
            .ToLowerInvariant()[..EntryKeyLength];
        int maximumTitleLength =
            MaximumStemLength - EntryKeyLength - 2;
        string safeTitle = SanitizeTitle(title, maximumTitleLength);
        return $"{safeTitle}--{entryKey}";
    }

    public static string ResolveContainedPath(
        string canonicalRoot,
        string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(
                Path.GetFileName(fileName),
                fileName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Export file names cannot contain path segments.",
                nameof(fileName));
        }

        string root = Path.GetFullPath(canonicalRoot);
        string candidate = Path.GetFullPath(
            Path.Combine(root, fileName));
        string rootPrefix = root.EndsWith(
                Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The export path escaped its configured root.");
        }
        EnsureNoReparsePoints(root);
        EnsureNoReparsePoints(candidate);
        return candidate;
    }

    /// <summary>
    /// 字符串包含关系无法阻止 junction/symlink 把真实写入位置重定向到根外；
    /// 因此所有已存在的路径组件都必须拒绝重解析点。
    /// </summary>
    public static void EnsureNoReparsePoints(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException(
                "The export path has no filesystem root.",
                nameof(path));
        string relative = Path.GetRelativePath(pathRoot, fullPath);
        string current = pathRoot;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                break;
            }
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Export paths cannot traverse filesystem reparse points.");
            }
        }
    }

    private static string SanitizeTitle(
        string? title,
        int maximumLength)
    {
        string normalized = (title ?? string.Empty)
            .Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        bool previousWasReplacement = false;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            bool invalid = Rune.IsControl(rune)
                || (rune.IsAscii
                    && InvalidWindowsFileNameCharacters.Contains(
                        (char)rune.Value));
            if (invalid)
            {
                if (!previousWasReplacement)
                {
                    builder.Append('-');
                    previousWasReplacement = true;
                }
                continue;
            }
            builder.Append(rune.ToString());
            previousWasReplacement = false;
        }

        string safe = builder.ToString()
            .Trim(' ', '.', '-');
        while (safe.Contains("..", StringComparison.Ordinal))
        {
            safe = safe.Replace(
                "..",
                "-",
                StringComparison.Ordinal);
        }
        if (safe.Length == 0)
        {
            safe = "entry";
        }
        if (ReservedWindowsNames.Contains(safe))
        {
            safe = $"_{safe}";
        }
        return TruncateWithoutSplittingRune(safe, maximumLength)
            .TrimEnd(' ', '.');
    }

    private static string TruncateWithoutSplittingRune(
        string value,
        int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        var result = new StringBuilder(maximumLength);
        foreach (Rune rune in value.EnumerateRunes())
        {
            string text = rune.ToString();
            if (result.Length + text.Length > maximumLength)
            {
                break;
            }
            result.Append(text);
        }
        return result.Length == 0 ? "entry" : result.ToString();
    }
}
