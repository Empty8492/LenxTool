using System.Globalization;
using System.Security.Cryptography;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class EntryAssetStore : IEntryAssetStore, IDisposable
{
    private const int BufferSize = 80 * 1024;
    private readonly SqliteDatabase _database;
    private readonly AppPaths _paths;
    private readonly AssetCacheOptions _options;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public EntryAssetStore(
        SqliteDatabase database,
        AppPaths paths,
        AssetCacheOptions options)
    {
        _database = database;
        _paths = paths;
        _options = options;
        if (options.MaximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumAssetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _paths.EnsureCreated();
    }

    public async Task<EntryAsset?> GetAsync(
        string entryId,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        ValidateEntryId(entryId);
        ValidateSourceUrl(sourceUrl);

        await using SqliteConnection connection = await _database
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT entry_id, source_url, content_hash, mime_type, size_bytes,
                   created_at, last_accessed_at
            FROM entry_assets
            WHERE entry_id=$entryId AND source_url=$sourceUrl;
            """;
        command.Parameters.AddWithValue("$entryId", entryId);
        command.Parameters.AddWithValue("$sourceUrl", sourceUrl);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAsset(reader)
            : null;
    }

    public async Task<EntryAsset> PutAsync(
        string entryId,
        string sourceUrl,
        string mimeType,
        Stream content,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await PutCoreAsync(
                    entryId,
                    sourceUrl,
                    mimeType,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<EntryAsset> PutCoreAsync(
        string entryId,
        string sourceUrl,
        string mimeType,
        Stream content,
        CancellationToken cancellationToken)
    {
        ValidateEntryId(entryId);
        ValidateSourceUrl(sourceUrl);
        ValidateMimeType(mimeType);
        ArgumentNullException.ThrowIfNull(content);

        _paths.EnsureCreated();
        string temporaryPath = Path.Combine(
            _paths.AssetCacheDirectory,
            $".{Guid.NewGuid():N}.tmp");
        string? finalPath = null;
        try
        {
            long size = 0;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[BufferSize];
                while (true)
                {
                    int read = await content.ReadAsync(
                        buffer.AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    size = checked(size + read);
                    if (size > _options.MaximumAssetBytes)
                    {
                        throw new InvalidDataException("资源超过单文件缓存上限。");
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            string contentHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            finalPath = GetAssetPath(contentHash);
            await PromoteFileAsync(temporaryPath, finalPath, size, cancellationToken)
                .ConfigureAwait(false);
            temporaryPath = string.Empty;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            EntryAsset asset = new(
                entryId,
                sourceUrl,
                contentHash,
                mimeType,
                size,
                now,
                now);
            await UpsertAsync(asset, cancellationToken).ConfigureAwait(false);
            await PruneCoreAsync([contentHash], cancellationToken)
                .ConfigureAwait(false);
            return asset;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                TryDelete(temporaryPath);
            }
        }
    }

    public async Task<Stream?> OpenReadAsync(
        EntryAsset asset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ValidateEntryId(asset.EntryId);
        ValidateSourceUrl(asset.SourceUrl);
        ValidateContentHash(asset.ContentHash);

        string path = GetAssetPath(asset.ContentHash);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != asset.SizeBytes)
            {
                await RemoveCorruptAssetAsync(asset, path, stream, cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BufferSize];
            while (true)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }

            string actualHash = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            if (!string.Equals(
                    actualHash,
                    asset.ContentHash,
                    StringComparison.Ordinal))
            {
                await RemoveCorruptAssetAsync(asset, path, stream, cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            stream.Position = 0;
            await TouchAsync(asset, cancellationToken).ConfigureAwait(false);
            FileStream result = stream;
            stream = null;
            return result;
        }
        catch (FileNotFoundException)
        {
            await RemoveRecordAsync(asset, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            await RemoveRecordAsync(asset, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RemoveCorruptAssetAsync(
        EntryAsset asset,
        string path,
        FileStream stream,
        CancellationToken cancellationToken)
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        await RemoveRecordAsync(asset, cancellationToken).ConfigureAwait(false);
        TryDelete(path);
    }

    public async Task<int> PruneAsync(
        IReadOnlyCollection<string> protectedContentHashes,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await PruneCoreAsync(
                    protectedContentHashes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<int> PruneCoreAsync(
        IReadOnlyCollection<string> protectedContentHashes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(protectedContentHashes);
        HashSet<string> protectedHashes = protectedContentHashes
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(hash => hash.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        foreach (string hash in protectedHashes)
        {
            ValidateContentHash(hash);
        }

        await using SqliteConnection connection = await _database
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT content_hash, MAX(size_bytes), MIN(last_accessed_at)
            FROM entry_assets
            GROUP BY content_hash
            ORDER BY MIN(last_accessed_at), content_hash;
            """;
        var candidates = new List<(string Hash, long Size)>();
        long total = 0;
        await using (SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long size = reader.GetInt64(1);
                candidates.Add((reader.GetString(0), size));
                total = checked(total + size);
            }
        }

        int removedRecords = 0;
        foreach ((string hash, long size) in candidates)
        {
            if (total <= _options.MaximumBytes) break;
            if (protectedHashes.Contains(hash)) continue;

            string path = GetAssetPath(hash);
            try
            {
                TryDelete(path);
                await using SqliteCommand delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM entry_assets WHERE content_hash=$hash;";
                delete.Parameters.AddWithValue("$hash", hash);
                removedRecords += await delete.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                total -= size;
            }
            catch (IOException)
            {
                // Keep the database row when the file cannot be removed.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the database row when the file cannot be removed.
            }
        }

        return removedRecords;
    }

    public async Task<EntryAssetPruneResult> RemoveUnreferencedFilesAsync(
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await _database
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT DISTINCT content_hash FROM entry_assets;";
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            await using (SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    referenced.Add(reader.GetString(0));
                }
            }

            if (!Directory.Exists(_paths.AssetCacheDirectory))
            {
                return new(0, 0);
            }
            int removedFiles = 0;
            long removedBytes = 0;
            try
            {
                foreach (string filePath in Directory.EnumerateFiles(
                    _paths.AssetCacheDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string fileName = Path.GetFileName(filePath);
                    if (referenced.Contains(fileName))
                    {
                        continue;
                    }
                    long size;
                    try
                    {
                        size = new FileInfo(filePath).Length;
                        File.Delete(filePath);
                    }
                    catch (FileNotFoundException)
                    {
                        continue;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                    removedFiles++;
                    removedBytes = checked(removedBytes + size);
                }
            }
            catch (Exception exception) when (
                exception is DirectoryNotFoundException
                or IOException
                or UnauthorizedAccessException)
            {
                // External cleanup can replace the directory mid-scan.
            }
            return new(removedFiles, removedBytes);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task UpsertAsync(
        EntryAsset asset,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _database
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO entry_assets(
                entry_id, source_url, content_hash, mime_type, size_bytes,
                created_at, last_accessed_at)
            VALUES($entryId, $sourceUrl, $contentHash, $mimeType, $sizeBytes,
                   $createdAt, $lastAccessedAt)
            ON CONFLICT(entry_id, source_url) DO UPDATE SET
                content_hash=excluded.content_hash,
                mime_type=excluded.mime_type,
                size_bytes=excluded.size_bytes,
                created_at=excluded.created_at,
                last_accessed_at=excluded.last_accessed_at;
            """;
        command.Parameters.AddWithValue("$entryId", asset.EntryId);
        command.Parameters.AddWithValue("$sourceUrl", asset.SourceUrl);
        command.Parameters.AddWithValue("$contentHash", asset.ContentHash);
        command.Parameters.AddWithValue("$mimeType", asset.MimeType);
        command.Parameters.AddWithValue("$sizeBytes", asset.SizeBytes);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(asset.CreatedAt));
        command.Parameters.AddWithValue("$lastAccessedAt", FormatTimestamp(asset.LastAccessedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TouchAsync(
        EntryAsset asset,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _database
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE entry_assets
            SET last_accessed_at=$lastAccessedAt
            WHERE entry_id=$entryId AND source_url=$sourceUrl
              AND content_hash=$contentHash;
            """;
        command.Parameters.AddWithValue("$lastAccessedAt", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$entryId", asset.EntryId);
        command.Parameters.AddWithValue("$sourceUrl", asset.SourceUrl);
        command.Parameters.AddWithValue("$contentHash", asset.ContentHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveRecordAsync(
        EntryAsset asset,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _database
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM entry_assets
            WHERE entry_id=$entryId AND source_url=$sourceUrl
              AND content_hash=$contentHash;
            """;
        command.Parameters.AddWithValue("$entryId", asset.EntryId);
        command.Parameters.AddWithValue("$sourceUrl", asset.SourceUrl);
        command.Parameters.AddWithValue("$contentHash", asset.ContentHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PromoteFileAsync(
        string temporaryPath,
        string finalPath,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        if (File.Exists(finalPath))
        {
            bool valid = new FileInfo(finalPath).Length == expectedSize;
            if (valid)
            {
                await using FileStream existing = new(
                    finalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[BufferSize];
                while (true)
                {
                    int read = await existing.ReadAsync(
                        buffer.AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    hash.AppendData(buffer, 0, read);
                }
                valid = string.Equals(
                    Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                    Path.GetFileName(finalPath),
                    StringComparison.Ordinal);
            }

            if (valid)
            {
                TryDelete(temporaryPath);
                return;
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
            return;
        }

        File.Move(temporaryPath, finalPath);
    }

    private string GetAssetPath(string contentHash) =>
        Path.Combine(_paths.AssetCacheDirectory, contentHash);

    private static EntryAsset ReadAsset(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        ParseTimestamp(reader.GetString(5)),
        ParseTimestamp(reader.GetString(6)));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateEntryId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256) throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static void ValidateSourceUrl(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 4096
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("资源地址必须是 HTTP 或 HTTPS。", nameof(value));
        }
    }

    private static void ValidateMimeType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static void ValidateContentHash(string value)
    {
        if (value.Length != 64
            || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("资源内容哈希无效。", nameof(value));
        }
    }

    private static void TryDelete(string path)
    {
        if (path.Length == 0) return;
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    public void Dispose() => _mutationGate.Dispose();
}
