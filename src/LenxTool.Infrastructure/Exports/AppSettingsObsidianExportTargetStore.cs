using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 把完整 Obsidian 目标作为一个带版本的 JSON 文档保存，避免多键更新产生半配置状态。
/// </summary>
public sealed class AppSettingsObsidianExportTargetStore(
    IAppSettingsRepository settings)
    : IObsidianExportTargetStore
{
    private const int DocumentVersion = 1;
    private const int MaximumTemplateLength = 64 * 1024;
    private const int MaximumSettingsDocumentLength = 256 * 1024;
    private const int MaximumTagCount = 32;
    private const int MaximumTagLength = 64;
    private static readonly string[] SupportedPlaceholders =
    [
        "{{title}}",
        "{{content}}",
        "{{source_url}}",
        "{{author}}",
        "{{published_at}}"
    ];
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public const string SettingsKey =
        "integration.obsidian.target.v1";

    public async Task<ObsidianExportTarget?> GetAsync(
        CancellationToken cancellationToken)
    {
        string? json;
        try
        {
            json = await settings.GetAsync(
                    SettingsKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (IOException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new IOException(
                "Obsidian 导出设置暂时无法读取。",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new IOException(
                "Obsidian 导出设置暂时无法读取。",
                exception);
        }
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        if (Encoding.UTF8.GetByteCount(json)
            > MaximumSettingsDocumentLength)
        {
            return null;
        }

        try
        {
            StoredDocument? document =
                JsonSerializer.Deserialize<StoredDocument>(
                    json,
                    JsonOptions);
            return document is { Version: DocumentVersion, Target: not null }
                ? Normalize(
                    document.Target,
                    missingVaultIsTransient: true)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        ObsidianExportTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        ObsidianExportTarget normalized = Normalize(target);
        string json = JsonSerializer.Serialize(
            new StoredDocument(DocumentVersion, normalized),
            JsonOptions);
        try
        {
            await settings.SetAsync(
                    SettingsKey,
                    json,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (IOException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new IOException(
                "Obsidian 导出设置暂时无法保存。",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new IOException(
                "Obsidian 导出设置暂时无法保存。",
                exception);
        }
    }

    internal static ObsidianExportTarget Normalize(
        ObsidianExportTarget target,
        bool missingVaultIsTransient = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(
                target.TargetId,
                ObsidianExportTarget.DefaultTargetId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Obsidian 当前只支持默认导出目标。",
                nameof(target));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(
            target.VaultRootPath);
        if (!Path.IsPathFullyQualified(target.VaultRootPath))
        {
            throw new ArgumentException(
                "Obsidian Vault 必须使用绝对路径。",
                nameof(target));
        }
        if (target.VaultRootPath.StartsWith(
                @"\\",
                StringComparison.Ordinal)
            || target.VaultRootPath.StartsWith(
                "//",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Obsidian Vault 必须位于本地磁盘，不能使用 UNC 或设备路径。",
                nameof(target));
        }
        string vaultRoot = Path.GetFullPath(target.VaultRootPath);
        string driveRoot = Path.GetPathRoot(vaultRoot)
            ?? throw new ArgumentException(
                "Obsidian Vault 必须位于本地磁盘。",
                nameof(target));
        if (new DriveInfo(driveRoot).DriveType
            == DriveType.Network)
        {
            throw new ArgumentException(
                "Obsidian Vault 不能位于网络驱动器。",
                nameof(target));
        }
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(vaultRoot),
                Path.TrimEndingDirectorySeparator(driveRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Obsidian Vault 不能直接使用磁盘根目录。",
                nameof(target));
        }
        if (!Directory.Exists(vaultRoot))
        {
            if (missingVaultIsTransient)
            {
                throw new IOException(
                    "已配置的 Obsidian Vault 暂时不可用。");
            }
            throw new ArgumentException(
                "Obsidian Vault 目录必须已经存在。",
                nameof(target));
        }
        MarkdownExportPathPolicy.EnsureNoReparsePoints(vaultRoot);

        string relativeDirectory =
            MarkdownExportPathPolicy.NormalizeRelativeDirectory(
                target.RelativeDirectory);
        string? template = NormalizeTemplate(target.TemplateMarkdown);
        IReadOnlyList<string> tags = NormalizeTags(target.Tags);
        return target with
        {
            VaultRootPath = vaultRoot,
            RelativeDirectory = relativeDirectory,
            TemplateMarkdown = template,
            Tags = tags
        };
    }

    private static string? NormalizeTemplate(string? template)
    {
        if (template is null)
        {
            return null;
        }
        string normalized = template
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (normalized.Length == 0)
        {
            return null;
        }
        if (Encoding.UTF8.GetByteCount(normalized)
            > MaximumTemplateLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(template),
                "Obsidian 模板不能超过 64 KiB。");
        }

        string withoutKnownPlaceholders = normalized;
        foreach (string placeholder in SupportedPlaceholders)
        {
            if (CountOccurrences(normalized, placeholder) > 1)
            {
                throw new ArgumentException(
                    "Obsidian 模板中的每个占位符最多只能出现一次。",
                    nameof(template));
            }
            withoutKnownPlaceholders =
                withoutKnownPlaceholders.Replace(
                    placeholder,
                    string.Empty,
                    StringComparison.Ordinal);
        }
        if (withoutKnownPlaceholders.Contains(
                "{{",
                StringComparison.Ordinal)
            || withoutKnownPlaceholders.Contains(
                "}}",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Obsidian 模板包含不支持的占位符。",
                nameof(template));
        }
        return normalized;
    }

    private static int CountOccurrences(
        string value,
        string token)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(
                   token,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }

    private static ReadOnlyCollection<string> NormalizeTags(
        IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count > MaximumTagCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tags),
                "Obsidian 导出最多支持 32 个标签。");
        }

        var normalized = new List<string>(tags.Count);
        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string? rawTag in tags)
        {
            if (rawTag is null)
            {
                throw new ArgumentException(
                    "Obsidian 标签不能包含 null。",
                    nameof(tags));
            }
            string tag = rawTag.Trim();
            if (tag.StartsWith('#'))
            {
                tag = tag[1..].Trim();
            }
            tag = tag.Normalize(NormalizationForm.FormC);
            if (tag.Length is 0 or > MaximumTagLength
                || tag.StartsWith('/')
                || tag.EndsWith('/')
                || tag.Contains("//", StringComparison.Ordinal)
                || tag.EnumerateRunes().Any(rune =>
                    !(Rune.IsLetterOrDigit(rune)
                      || rune.Value is '_' or '-' or '/'))
                || !tag.EnumerateRunes().Any(rune =>
                    !Rune.IsDigit(rune)))
            {
                throw new ArgumentException(
                    "Obsidian 标签包含不支持的字符、非法层级或只有数字。",
                    nameof(tags));
            }
            if (seen.Add(tag))
            {
                normalized.Add(tag);
            }
        }
        return Array.AsReadOnly(normalized.ToArray());
    }

    private sealed record StoredDocument(
        int Version,
        ObsidianExportTarget? Target);
}
