using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 把统一导出请求写入预先配置的本地根目录；
/// 适配器不解析任意请求路径，也不负责下载缺失资源。
/// </summary>
public sealed class MarkdownEntryExporter : IEntryExporter, IDisposable
{
    private const int BufferSize = 80 * 1024;
    private const int VersionKeyLength = 20;
    private static readonly UTF8Encoding Utf8WithoutBom =
        new(encoderShouldEmitUTF8Identifier: false);
    private readonly Dictionary<string, MarkdownExportTarget>
        _targets;
    private readonly IEntryAssetStore? _assetStore;
    private readonly SemaphoreSlim _exportGate = new(1, 1);

    public const string ExporterId = "markdown";

    public MarkdownEntryExporter(
        IEnumerable<MarkdownExportTarget> targets,
        IEntryAssetStore? assetStore)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var validated = new Dictionary<string, MarkdownExportTarget>(
            StringComparer.Ordinal);
        foreach (MarkdownExportTarget? target in targets)
        {
            MarkdownExportTarget normalized = ValidateTarget(target);
            if (!validated.TryAdd(normalized.TargetId, normalized))
            {
                throw new ArgumentException(
                    "Markdown target identifiers must be unique.",
                    nameof(targets));
            }
        }
        _targets = validated;
        _assetStore = assetStore;
    }

    public EntryExportCapability Capability { get; } = new(
        ExporterId,
        "Markdown 文件",
        Array.AsReadOnly(Enum.GetValues<EntryViewKind>()),
        RequiresCredentials: false,
        MaximumContentBytes: null,
        // 新版本文件名由请求幂等键派生；崩溃恢复不会再次制造副本。
        IsIdempotent: true);

    public async Task<EntryExportResult> ExportAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                request.ExporterId,
                ExporterId,
                StringComparison.Ordinal)
            || !_targets.TryGetValue(
                request.TargetId,
                out MarkdownExportTarget? target))
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }

        await _exportGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await ExportCoreAsync(
                    request,
                    target,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure(
                EntryExportErrorCode.AccessDenied,
                exception);
        }
        catch (IOException exception)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                exception,
                isRetryable: true);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private async Task<EntryExportResult> ExportCoreAsync(
        EntryExportRequest request,
        MarkdownExportTarget target,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target.RootDirectory);
        MarkdownExportPathPolicy.EnsureNoReparsePoints(
            target.RootDirectory);
        string stem = MarkdownExportPathPolicy.CreateFileStem(
            request.Entry.Title,
            request.Entry.Id);
        string destination = SelectDestination(
            target,
            stem,
            request.IdempotencyKey);
        string relativeFileName = Path.GetFileName(destination);
        if (target.ExistingFileBehavior is
                MarkdownExistingFileBehavior.Skip
                or MarkdownExistingFileBehavior.CreateNewVersion
            && File.Exists(destination))
        {
            return EntryExportResult.Success(
                request.IdempotencyKey,
                relativeFileName,
                remoteUrl: null);
        }

        IReadOnlyDictionary<string, string> localImages =
            target.ContentMode
                == MarkdownExportContentMode.ContentWithCachedImages
                ? await CopyCachedImagesAsync(
                        request.Entry,
                        target.RootDirectory,
                        cancellationToken)
                    .ConfigureAwait(false)
                : new Dictionary<string, string>(
                    StringComparer.Ordinal);
        string markdown = MarkdownDocumentRenderer.Render(
            request.Entry,
            request.ViewKind,
            target.ContentMode,
            localImages);
        string temporaryPath = MarkdownExportPathPolicy
            .ResolveContainedPath(
                target.RootDirectory,
                $".{Guid.NewGuid():N}.md.tmp");
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    markdown,
                    Utf8WithoutBom,
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(
                temporaryPath,
                destination,
                overwrite: target.ExistingFileBehavior
                    == MarkdownExistingFileBehavior.Overwrite);
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        return EntryExportResult.Success(
            request.IdempotencyKey,
            relativeFileName,
            remoteUrl: null);
    }

    private async Task<IReadOnlyDictionary<string, string>>
        CopyCachedImagesAsync(
            FeedEntry entry,
            string rootDirectory,
            CancellationToken cancellationToken)
    {
        var exported = new Dictionary<string, string>(
            StringComparer.Ordinal);
        if (_assetStore is null)
        {
            return exported;
        }

        foreach (MarkdownImageCandidate candidate
                 in MarkdownDocumentRenderer.FindImageCandidates(
                     entry.SanitizedContent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EntryAsset? asset = await _assetStore.GetAsync(
                    entry.Id,
                    candidate.SourceUrl,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsExportableCachedImage(
                    entry,
                    candidate,
                    asset))
            {
                continue;
            }

            await using Stream? source = await _assetStore
                .OpenReadAsync(asset!, cancellationToken)
                .ConfigureAwait(false);
            if (source is null)
            {
                continue;
            }
            string extension = GetImageExtension(asset!.MimeType)!;
            string assetsDirectory = MarkdownExportPathPolicy
                .ResolveContainedPath(rootDirectory, "_assets");
            Directory.CreateDirectory(assetsDirectory);
            string assetFileName =
                $"{asset.ContentHash}{extension}";
            string destination = MarkdownExportPathPolicy
                .ResolveContainedPath(
                    assetsDirectory,
                    assetFileName);
            string temporaryPath = MarkdownExportPathPolicy
                .ResolveContainedPath(
                    assetsDirectory,
                    $".{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous
                    | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(
                            output,
                            BufferSize,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                File.Move(
                    temporaryPath,
                    destination,
                    overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
            exported[candidate.SourceUrl] =
                $"_assets/{assetFileName}";
        }
        return exported;
    }

    private static string SelectDestination(
        MarkdownExportTarget target,
        string stem,
        string idempotencyKey)
    {
        if (target.ExistingFileBehavior
            == MarkdownExistingFileBehavior.CreateNewVersion)
        {
            string versionKey = idempotencyKey[..VersionKeyLength];
            return MarkdownExportPathPolicy.ResolveContainedPath(
                target.RootDirectory,
                $"{stem}--v-{versionKey}.md");
        }
        return MarkdownExportPathPolicy.ResolveContainedPath(
            target.RootDirectory,
            $"{stem}.md");
    }

    private static bool IsExportableCachedImage(
        FeedEntry entry,
        MarkdownImageCandidate candidate,
        EntryAsset? asset) =>
        asset is not null
        && string.Equals(
            asset.EntryId,
            entry.Id,
            StringComparison.Ordinal)
        && string.Equals(
            asset.SourceUrl,
            candidate.SourceUrl,
            StringComparison.Ordinal)
        && asset.MimeType.StartsWith(
            "image/",
            StringComparison.OrdinalIgnoreCase)
        // 与 P1 已做签名校验的栅格缓存白名单保持一致，不把 SVG/未知 image MIME 落到可浏览目录。
        && GetImageExtension(asset.MimeType) is not null
        && asset.ContentHash.Length == 64
        && asset.ContentHash.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f')
        && asset.SizeBytes >= 0;

    private static string? GetImageExtension(string mimeType) =>
        mimeType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => null
        };

    private static MarkdownExportTarget ValidateTarget(
        MarkdownExportTarget? target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.TargetId);
        if (target.TargetId.Length > 128
            || !string.Equals(
                target.TargetId,
                target.TargetId.Trim(),
                StringComparison.Ordinal)
            || target.TargetId.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(
            target.RootDirectory);
        if (!Path.IsPathFullyQualified(target.RootDirectory)
            || !Enum.IsDefined(target.ContentMode)
            || !Enum.IsDefined(target.ExistingFileBehavior))
        {
            throw new ArgumentException(
                "Markdown targets require an absolute path and valid options.",
                nameof(target));
        }
        return target with
        {
            RootDirectory = Path.GetFullPath(
                target.RootDirectory)
        };
    }

    private static EntryExportException Failure(
        EntryExportErrorCode code,
        Exception? innerException = null,
        bool isRetryable = false) =>
        new(
            new(
                code,
                isRetryable),
            innerException);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 最佳努力清理临时文件，不覆盖原始导出异常。
        }
    }

    public void Dispose() => _exportGate.Dispose();
}
