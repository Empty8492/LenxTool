using System.Security.Cryptography;
using LenxTool.Core.Contracts;

namespace LenxTool.Infrastructure.SystemServices;

public sealed class FileHashService : IFileHashService
{
    public async Task<string> ComputeSha256Async(
        string filePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var file = new FileInfo(filePath);
        if (!file.Exists) throw new FileNotFoundException("找不到待校验文件。", filePath);

        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 128];
        long readTotal = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            readTotal += read;
            progress?.Report(file.Length == 0 ? 100 : readTotal * 100d / file.Length);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
