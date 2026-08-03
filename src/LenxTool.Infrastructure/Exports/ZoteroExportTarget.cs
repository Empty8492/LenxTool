using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// Zotero 当前只导出到个人库，并要求用户显式选择第三方条目类型。
/// 数值固定后才能安全进入版本化设置与持久队列。
/// </summary>
public enum ZoteroItemType
{
    Webpage = 1,
    JournalArticle = 2
}

/// <summary>
/// 表示一个不含凭据的 Zotero 个人库目标。API 根地址由 UserId 派生，
/// 不接受用户提供的任意主机或路径。
/// </summary>
public sealed record ZoteroExportTarget(
    string TargetId,
    long UserId,
    ZoteroItemType ItemType,
    bool IncludeSummaryNote,
    bool UploadFirstImageAttachment)
{
    private const int QueueRevisionLength = 24;
    public const string DefaultTargetId = "default";

    /// <summary>
    /// 返回 Zotero 官方个人库根地址；调用方不能通过设置覆盖主机。
    /// </summary>
    public Uri ApiRoot
    {
        get
        {
            Validate(this);
            return new Uri(
                $"https://api.zotero.org/users/{UserId.ToString(CultureInfo.InvariantCulture)}/",
                UriKind.Absolute);
        }
    }

    /// <summary>
    /// 将规范化目标的全部行为选项绑定到不透明队列代际；队列标识不泄露 UserId。
    /// </summary>
    public string CreateQueueTargetId()
    {
        Validate(this);
        string canonical = string.Join(
            '\n',
            "v1",
            TargetId,
            UserId.ToString(CultureInfo.InvariantCulture),
            ((int)ItemType).ToString(CultureInfo.InvariantCulture),
            IncludeSummaryNote ? "1" : "0",
            UploadFirstImageAttachment ? "1" : "0");
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical));
        string revision = Convert.ToHexString(hash)
            .ToLowerInvariant()[..QueueRevisionLength];
        return $"{TargetId}.{revision}";
    }

    internal bool MatchesQueueTargetId(string? value) =>
        string.Equals(
            CreateQueueTargetId(),
            value,
            StringComparison.Ordinal);

    internal static bool IsSupportedQueueTargetId(string? value)
    {
        string prefix = $"{DefaultTargetId}.";
        return value is not null
               && value.Length == prefix.Length + QueueRevisionLength
               && value.StartsWith(prefix, StringComparison.Ordinal)
               && value[prefix.Length..].All(character =>
                   character is >= '0' and <= '9'
                   or >= 'a' and <= 'f');
    }

    internal static void Validate(ZoteroExportTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(
                target.TargetId,
                DefaultTargetId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Zotero 当前只支持默认个人库目标。",
                nameof(target));
        }
        if (target.UserId <= 0)
        {
            throw new ArgumentException(
                "Zotero User ID 必须是正整数。",
                nameof(target));
        }
        if (target.ItemType is not (
                ZoteroItemType.Webpage
                or ZoteroItemType.JournalArticle))
        {
            throw new ArgumentException(
                "Zotero 条目类型必须是 Webpage 或 JournalArticle。",
                nameof(target));
        }
    }
}

/// <summary>
/// 导出期间持有的 Zotero 配置代际；释放前同进程不能保存下一代目标。
/// </summary>
public interface IZoteroExportTargetLease : IAsyncDisposable
{
    ZoteroExportTarget? Target { get; }
}

/// <summary>
/// 从单一版本化设置文档读取和原子替换当前 Zotero 个人库目标。
/// </summary>
public interface IZoteroExportTargetStore
{
    Task<ZoteroExportTarget?> GetAsync(
        CancellationToken cancellationToken);

    Task<IZoteroExportTargetLease> AcquireExportLeaseAsync(
        CancellationToken cancellationToken);

    Task SaveAsync(
        ZoteroExportTarget target,
        CancellationToken cancellationToken);
}
