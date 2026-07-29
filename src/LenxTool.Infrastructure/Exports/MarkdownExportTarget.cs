namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 控制 Markdown 正文与本地缓存图片的导出范围。
/// </summary>
public enum MarkdownExportContentMode
{
    LinkOnly,
    Content,
    ContentWithCachedImages
}

/// <summary>
/// 明确定义目标文件已存在时的处理方式，避免后台导出隐式覆盖用户文件。
/// </summary>
public enum MarkdownExistingFileBehavior
{
    Overwrite,
    Skip,
    CreateNewVersion
}

/// <summary>
/// 把统一导出请求中的不透明 TargetId 映射到预先授权的本地目录与行为。
/// </summary>
public sealed record MarkdownExportTarget(
    string TargetId,
    string RootDirectory,
    MarkdownExportContentMode ContentMode,
    MarkdownExistingFileBehavior ExistingFileBehavior);
