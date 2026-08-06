using System.IO;
using System.Text;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public interface IAiReportTextExportService
{
    Task ExportAsync(
        string path,
        AiReport report,
        CancellationToken cancellationToken);
}

/// <summary>
/// 将已落库的报告写为纯文本。先写同目录临时文件再替换目标，取消或写入失败时
/// 不会留下半份目标文件；目标路径只来自用户确认过的保存对话框。
/// </summary>
public sealed class AiReportTextExportService : IAiReportTextExportService
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: true);
    private const int MaximumExportCharacters = 1_000_000;

    public async Task ExportAsync(
        string path,
        AiReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(
                Path.GetExtension(path),
                ".txt",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "AI 报告只能导出到明确选择的绝对 .txt 路径。",
                nameof(path));
        }
        if (report.Content.Length > MaximumExportCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(report),
                "报告正文超过本地导出上限。");
        }

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("报告导出目录不存在。");
        }
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        string text = string.Join(
            Environment.NewLine,
            report.Title,
            $"类型：{report.ReportType}",
            $"模型：{report.Model}",
            $"生成时间（UTC）：{report.CreatedAt.ToUniversalTime():O}",
            $"Token 用量：{report.TokenUsage}",
            string.Empty,
            "---",
            string.Empty,
            report.Content,
            string.Empty);
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                text,
                Utf8WithBom,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
