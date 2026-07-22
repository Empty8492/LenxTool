using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.SystemServices;

public sealed class OpmlFileService(IOpmlCodec codec) : IOpmlFileService
{
    public async Task<OpmlDocument> LoadAsync(string path, CancellationToken cancellationToken)
    {
        string fullPath = RequirePath(path);
        try
        {
            await using var source = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await codec.ParseAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw FileError(AppErrorCode.FileNotFound, "找不到 OPML 文件", "所选 OPML 文件已不存在。", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileError(AppErrorCode.FileAccessDenied, "无法读取 OPML 文件", "系统无法读取所选 OPML 文件。", exception);
        }
    }

    public async Task SaveAsync(
        string path,
        OpmlDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        string fullPath = RequirePath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("An OPML destination directory is required.", nameof(path));
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await codec.WriteAsync(destination, document, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException)
        {
            throw FileError(AppErrorCode.FileNotFound, "找不到保存位置", "所选 OPML 保存目录已不存在。", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileError(AppErrorCode.FileAccessDenied, "无法保存 OPML 文件", "系统无法写入所选 OPML 文件。", exception);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static string RequirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new ArgumentException("A valid OPML file path is required.", nameof(path));
        return Path.GetFullPath(path);
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The destination was never replaced; a uniquely named temporary file can be cleaned later.
        }
    }

    private static AppException FileError(
        AppErrorCode code,
        string title,
        string message,
        Exception exception) => new(new(
            code,
            title,
            message,
            "请重新选择可访问的本地位置后重试。",
            $"OPML file operation failed: {exception.GetType().Name}"), exception);
}
