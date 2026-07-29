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
    internal const long MaximumContentBytes = 8L * 1024 * 1024;
    internal const long MaximumOutputBytes = 12L * 1024 * 1024;
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
        MaximumContentBytes,
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
        EnsureWithinContentBounds(request);

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
        catch (MarkdownRenderLimitExceededException exception)
        {
            throw Failure(
                EntryExportErrorCode.ContentTooLarge,
                exception);
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
        // 任何输出预算失败都必须发生在创建目录或复制缓存资源之前。
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

        IReadOnlyList<CachedImagePlan> cachedImagePlans =
            target.ContentMode
                == MarkdownExportContentMode.ContentWithCachedImages
                ? await PlanCachedImagesAsync(
                        request.Entry,
                        cancellationToken)
                    .ConfigureAwait(false)
                : [];
        Dictionary<string, string> plannedImages =
            cachedImagePlans.ToDictionary(
                plan => plan.Candidate.SourceUrl,
                plan => plan.RelativePath,
                StringComparer.Ordinal);
        string markdown = MarkdownDocumentRenderer.Render(
            request.Entry,
            request.ViewKind,
            target.ContentMode,
            plannedImages,
            target.RenderOptions,
            checked((int)MaximumOutputBytes));

        // 创建前先检查所有已存在祖先，创建后再次检查实际目录。
        MarkdownExportPathPolicy.EnsureNoReparsePoints(
            target.RootDirectory);
        Directory.CreateDirectory(target.RootDirectory);
        MarkdownExportPathPolicy.EnsureNoReparsePoints(
            target.RootDirectory);
        if (cachedImagePlans.Count > 0)
        {
            IReadOnlyDictionary<string, string> copiedImages =
                await CopyCachedImagesAsync(
                        cachedImagePlans,
                        target.RootDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (copiedImages.Count != plannedImages.Count)
            {
                // A cache entry may disappear between metadata lookup and
                // opening its stream. Removing such links only shrinks output.
                markdown = MarkdownDocumentRenderer.Render(
                    request.Entry,
                    request.ViewKind,
                    target.ContentMode,
                    copiedImages,
                    target.RenderOptions,
                    checked((int)MaximumOutputBytes));
            }
        }

        string temporaryPath = MarkdownExportPathPolicy
            .ResolveContainedPath(
                target.RootDirectory,
                $".{Guid.NewGuid():N}.md.tmp");
        try
        {
            int outputByteCount = Utf8WithoutBom.GetByteCount(markdown);
            if (outputByteCount > MaximumOutputBytes)
            {
                throw Failure(
                    EntryExportErrorCode.ContentTooLarge);
            }
            byte[] bytes = Utf8WithoutBom.GetBytes(markdown);
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous
                | FileOptions.SequentialScan))
            {
                await output.WriteAsync(
                        bytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            // 临时写入后、原子移动前再次验证，缩短目录被替换的竞态窗口。
            MarkdownExportPathPolicy.ResolveContainedPath(
                target.RootDirectory,
                Path.GetFileName(temporaryPath));
            MarkdownExportPathPolicy.ResolveContainedPath(
                target.RootDirectory,
                Path.GetFileName(destination));
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

    private async Task<IReadOnlyList<CachedImagePlan>>
        PlanCachedImagesAsync(
            FeedEntry entry,
            CancellationToken cancellationToken)
    {
        var plans = new List<CachedImagePlan>();
        if (_assetStore is null)
        {
            return plans;
        }

        foreach (MarkdownImageCandidate candidate
                 in MarkdownDocumentRenderer.FindImageCandidates(
                     entry.SanitizedContent,
                     checked((int)MaximumOutputBytes)))
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

            string extension = GetImageExtension(asset!.MimeType)!;
            string assetFileName =
                $"{asset.ContentHash}{extension}";
            plans.Add(
                new(
                    candidate,
                    asset,
                    $"_assets/{assetFileName}"));
        }
        return plans;
    }

    private async Task<IReadOnlyDictionary<string, string>>
        CopyCachedImagesAsync(
            IReadOnlyList<CachedImagePlan> plans,
            string rootDirectory,
            CancellationToken cancellationToken)
    {
        var exported = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (CachedImagePlan plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using Stream? source = await _assetStore!
                .OpenReadAsync(plan.Asset, cancellationToken)
                .ConfigureAwait(false);
            if (source is null)
            {
                continue;
            }

            string assetsDirectory = MarkdownExportPathPolicy
                .ResolveContainedPath(rootDirectory, "_assets");
            Directory.CreateDirectory(assetsDirectory);
            string assetFileName = Path.GetFileName(
                plan.RelativePath);
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
            exported[plan.Candidate.SourceUrl] =
                plan.RelativePath;
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

    private static void EnsureWithinContentBounds(
        EntryExportRequest request)
    {
        if (request.ContentBytes < 0)
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
        if (request.ContentBytes > MaximumContentBytes)
        {
            throw Failure(EntryExportErrorCode.ContentTooLarge);
        }

        long actualBytes = 0;
        AddUtf8Bytes(ref actualBytes, request.Entry.Title);
        AddUtf8Bytes(ref actualBytes, request.Entry.NormalizedUrl);
        AddUtf8Bytes(ref actualBytes, request.Entry.Author);
        AddUtf8Bytes(ref actualBytes, request.Entry.SanitizedContent);
        AddUtf8Bytes(ref actualBytes, request.Entry.Id);
        AddUtf8Bytes(ref actualBytes, request.Entry.FeedId);
        AddUtf8Bytes(ref actualBytes, request.Entry.ContentHash);
        foreach (string category in request.Entry.Categories)
        {
            AddUtf8Bytes(ref actualBytes, category);
        }
        if (actualBytes > MaximumContentBytes)
        {
            throw Failure(EntryExportErrorCode.ContentTooLarge);
        }
    }

    private static void AddUtf8Bytes(
        ref long total,
        string? value)
    {
        if (value is null)
        {
            return;
        }
        total = checked(
            total + Utf8WithoutBom.GetByteCount(value));
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

    private sealed record CachedImagePlan(
        MarkdownImageCandidate Candidate,
        EntryAsset Asset,
        string RelativePath);
}
