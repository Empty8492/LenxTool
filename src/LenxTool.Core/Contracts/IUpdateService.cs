using LenxTool.Core.Updates;

namespace LenxTool.Core.Contracts;

public interface IUpdateService
{
    Task<UpdateCandidate?> CheckAsync(CancellationToken cancellationToken);

    Task<string> DownloadAsync(
        UpdateCandidate candidate,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    void LaunchInstallerAndExit(string installerPath);
}
