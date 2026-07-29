using System.Security.Cryptography;
using System.Text;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 描述一个经用户授权、且只能写入其 Vault 子目录的 Obsidian 导出目标。
/// 模板只控制正文，front matter 和文件路径始终由导出器生成。
/// </summary>
public sealed record ObsidianExportTarget(
    string TargetId,
    string VaultRootPath,
    string RelativeDirectory,
    string? TemplateMarkdown,
    IReadOnlyList<string> Tags,
    bool IncludeSourceLink)
{
    private const int QueueRevisionLength = 24;
    public const string DefaultTargetId = "default";

    /// <summary>
    /// 为耐久队列生成不泄露路径或模板的配置作用域。带版本任务只在该作用域
    /// 仍与当前配置精确匹配时有效；旧版 default 任务的兼容由导出器单独处理。
    /// </summary>
    public string CreateQueueTargetId()
    {
        if (!string.Equals(
                TargetId,
                DefaultTargetId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Obsidian 当前只支持默认导出目标。");
        }
        ArgumentNullException.ThrowIfNull(Tags);
        var canonical = new StringBuilder();
        Append(
            canonical,
            NormalizeWindowsPathForQueueScope(
                VaultRootPath ?? string.Empty));
        Append(
            canonical,
            MarkdownExportPathPolicy.NormalizeRelativeDirectory(
                    RelativeDirectory)
                .ToUpperInvariant());
        Append(canonical, TemplateMarkdown ?? string.Empty);
        foreach (string tag in Tags)
        {
            Append(canonical, tag ?? string.Empty);
        }
        Append(canonical, IncludeSourceLink ? "1" : "0");
        string revision = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant()[..QueueRevisionLength];
        return $"{TargetId}.{revision}";
    }

    private static string NormalizeWindowsPathForQueueScope(
        string value)
    {
        if (value.Length == 0)
        {
            return value;
        }
        return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(value))
            .ToUpperInvariant();
    }

    internal static bool IsSupportedQueueTargetId(string value)
    {
        if (string.Equals(
                value,
                DefaultTargetId,
                StringComparison.Ordinal))
        {
            // 兼容已落库的 P2-11 预发布任务。
            return true;
        }
        string prefix = $"{DefaultTargetId}.";
        return value.Length == prefix.Length + QueueRevisionLength
               && value.StartsWith(prefix, StringComparison.Ordinal)
               && value[prefix.Length..].All(character =>
                   character is >= '0' and <= '9'
                   or >= 'a' and <= 'f');
    }

    private static void Append(
        StringBuilder target,
        string value)
    {
        target.Append(value.Length);
        target.Append(':');
        target.Append(value);
    }
}

/// <summary>
/// 用单一设置文档读取和原子替换当前 Obsidian 导出目标。
/// </summary>
public interface IObsidianExportTargetStore
{
    Task<ObsidianExportTarget?> GetAsync(
        CancellationToken cancellationToken);

    Task SaveAsync(
        ObsidianExportTarget target,
        CancellationToken cancellationToken);
}
