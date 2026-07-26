using System.Globalization;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed partial class DatabaseMaintenanceService
{
    private const int CleanupBatchSize = 5000;
    private const long MinimumVacuumFreeBytes = 32L * 1024 * 1024;

    internal Func<bool>? VacuumCapacityProbe { get; set; }

    public Task<LocalStorageUsage> GetStorageUsageAsync(
        CancellationToken cancellationToken) =>
        Task.Run(
            () => MeasureStorageUsage(cancellationToken),
            cancellationToken);

    public async Task<StorageCleanupPreview> PreviewCleanupAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        ValidateCutoff(cutoff);
        await using SqliteConnection connection = await _database
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            WITH candidates AS (
                SELECT e.id
                FROM feed_entries e
                WHERE {FeedRetentionSql.CandidateWhereClause}
            ),
            reclaimable_hashes AS (
                SELECT
                    asset.content_hash,
                    MAX(asset.size_bytes) AS size_bytes
                FROM entry_assets asset
                JOIN candidates candidate ON candidate.id=asset.entry_id
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM entry_assets keeper
                    WHERE keeper.content_hash=asset.content_hash
                      AND NOT EXISTS (
                          SELECT 1
                          FROM candidates kept_candidate
                          WHERE kept_candidate.id=keeper.entry_id))
                GROUP BY asset.content_hash
            )
            SELECT
                (SELECT COUNT(*) FROM candidates),
                (SELECT COUNT(*) FROM reclaimable_hashes),
                COALESCE(
                    (SELECT SUM(size_bytes) FROM reclaimable_hashes),
                    0);
            """;
        command.Parameters.AddWithValue(
            "$cutoff",
            cutoff.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("无法读取本地清理预览。");
        }
        return new(
            cutoff,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt64(2));
    }

    public async Task<StorageCleanupResult> RunCleanupAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        ValidateCutoff(cutoff);
        cancellationToken.ThrowIfCancellationRequested();

        int deletedEntries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int deleted = await _feedEntries
                .DeleteExpiredUnprotectedAsync(
                    cutoff,
                    CleanupBatchSize,
                    cancellationToken)
                .ConfigureAwait(false);
            deletedEntries = checked(deletedEntries + deleted);
            if (deleted < CleanupBatchSize)
            {
                break;
            }
        }

        EntryAssetPruneResult orphaned =
            await _entryAssets.RemoveUnreferencedFilesAsync(cancellationToken)
                .ConfigureAwait(false);
        await _entryAssets.PruneAsync([], cancellationToken)
            .ConfigureAwait(false);
        bool optimized = await TryOptimizeDatabaseAsync(cancellationToken)
            .ConfigureAwait(false);
        LocalStorageUsage usage = await GetStorageUsageAsync(cancellationToken)
            .ConfigureAwait(false);
        return new(
            cutoff,
            deletedEntries,
            orphaned.RemovedFileCount,
            orphaned.RemovedBytes,
            optimized,
            usage);
    }

    private LocalStorageUsage MeasureStorageUsage(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long databaseBytes = checked(
            GetFileLength(_paths.DatabasePath)
            + GetFileLength(_paths.DatabasePath + "-wal")
            + GetFileLength(_paths.DatabasePath + "-shm"));
        (long imageBytes, int imageFiles) = MeasureDirectory(
            _paths.AssetCacheDirectory,
            recursive: false,
            cancellationToken);
        (long modelBytes, int modelFiles) = MeasureDirectory(
            _paths.ModelsDirectory,
            recursive: false,
            cancellationToken);
        return new(
            databaseBytes,
            imageBytes,
            imageFiles,
            modelBytes,
            modelFiles);
    }

    private async Task<bool> TryOptimizeDatabaseAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = await _database
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA optimize;";
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!(VacuumCapacityProbe?.Invoke() ?? HasVacuumCapacity()))
            {
                return false;
            }
            command.CommandText = "VACUUM;";
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode is 5 or 13)
        {
            return false;
        }
    }

    private bool HasVacuumCapacity()
    {
        try
        {
            string? root = Path.GetPathRoot(
                Path.GetFullPath(_paths.DatabasePath));
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }
            long databaseBytes = GetFileLength(_paths.DatabasePath);
            long required = Math.Max(
                checked(databaseBytes * 2),
                MinimumVacuumFreeBytes);
            return new DriveInfo(root).AvailableFreeSpace >= required;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return false;
        }
    }

    private static (long Bytes, int Files) MeasureDirectory(
        string directory,
        bool recursive,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }
        long bytes = 0;
        int files = 0;
        var option = recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        try
        {
            foreach (string path in Directory.EnumerateFiles(
                directory,
                "*",
                option))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    bytes = checked(bytes + new FileInfo(path).Length);
                    files++;
                }
                catch (Exception exception) when (
                    exception is FileNotFoundException
                    or DirectoryNotFoundException
                    or IOException
                    or UnauthorizedAccessException)
                {
                    // Files can disappear while the background size scan runs.
                }
            }
        }
        catch (Exception exception) when (
            exception is DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException)
        {
            // The directory can be replaced while the background scan runs.
        }
        return (bytes, files);
    }

    private static long GetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void ValidateCutoff(DateTimeOffset cutoff)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(
            cutoff,
            default,
            nameof(cutoff));
    }
}
