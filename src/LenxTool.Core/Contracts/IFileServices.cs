using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public sealed record LocalModelInfo(
    string Name,
    string Path,
    long Size,
    string Sha256);

public interface ILocalModelService
{
    Task<LocalModelInfo> ImportAsync(string sourcePath, CancellationToken cancellationToken);

    Task<IReadOnlyList<LocalModelInfo>> ListAsync(CancellationToken cancellationToken);
}

public interface IFileHashService
{
    Task<string> ComputeSha256Async(
        string filePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

public interface IDocumentConverter
{
    string Name { get; }

    bool IsAvailable { get; }

    Task ConvertToPdfAsync(
        string sourcePath,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

public interface IDatabaseMaintenanceService
{
    Task<string> BackupAsync(string? destinationPath, CancellationToken cancellationToken);

    Task RestoreAsync(string sourcePath, CancellationToken cancellationToken);

    Task<LocalStorageUsage> GetStorageUsageAsync(
        CancellationToken cancellationToken);

    Task<StorageCleanupPreview> PreviewCleanupAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken);

    Task<StorageCleanupResult> RunCleanupAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken);
}
