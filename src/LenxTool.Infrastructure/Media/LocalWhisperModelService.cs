using LenxTool.Core.Contracts;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.Infrastructure.Media;

public sealed class LocalWhisperModelService(
    AppPaths paths,
    IFileHashService hashService) : ILocalModelService
{
    private const long MinimumModelBytes = 1024 * 1024;

    public async Task<LocalModelInfo> ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = new FileInfo(sourcePath);
        if (!source.Exists) throw new FileNotFoundException("找不到 Whisper 模型。", sourcePath);
        if (!source.Name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Extension, ".bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("仅支持导入 ggml-*.bin Whisper 模型。", nameof(sourcePath));
        }
        if (source.Length < MinimumModelBytes)
        {
            throw new InvalidDataException("模型文件过小，可能不是有效的 Whisper 模型。");
        }

        paths.EnsureCreated();
        string destination = Path.Combine(paths.ModelsDirectory, source.Name);
        string temporary = destination + ".importing";
        await using (FileStream input = source.OpenRead())
        await using (var output = new FileStream(
                         temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         1024 * 256, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, destination, overwrite: true);
        string sha256 = await hashService.ComputeSha256Async(destination, null, cancellationToken)
            .ConfigureAwait(false);
        return new(source.Name, destination, source.Length, sha256);
    }

    public async Task<IReadOnlyList<LocalModelInfo>> ListAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        var results = new List<LocalModelInfo>();
        foreach (string filePath in Directory.EnumerateFiles(paths.ModelsDirectory, "ggml-*.bin"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(filePath);
            string sha256 = await hashService.ComputeSha256Async(filePath, null, cancellationToken)
                .ConfigureAwait(false);
            results.Add(new(file.Name, file.FullName, file.Length, sha256));
        }
        return results;
    }
}
