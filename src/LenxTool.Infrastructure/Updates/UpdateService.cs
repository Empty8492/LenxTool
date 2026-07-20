using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Updates;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.Infrastructure.Updates;

public sealed record UpdateOptions(
    IReadOnlyList<Uri> ManifestUris,
    string PublicKeyPem,
    string Channel = "stable");

public sealed class UpdateService(
    IHttpClientFactory httpClientFactory,
    IFileHashService hashService,
    AppPaths paths,
    UpdateOptions options) : IUpdateService
{
    public async Task<UpdateCandidate?> CheckAsync(CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        foreach (Uri uri in options.ManifestUris)
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using HttpClient client = httpClientFactory.CreateClient("LenxTool.Update");
                using HttpResponseMessage response = await client.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                SignedUpdateManifest envelope = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("更新清单为空。");
                UpdateManifest manifest = UpdateManifestVerifier.Verify(envelope, options.PublicKeyPem);
                if (!string.Equals(manifest.Channel, options.Channel, StringComparison.OrdinalIgnoreCase)) continue;
                return UpdateManifestVerifier.SelectCandidate(manifest, CurrentVersion());
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or CryptographicException)
            {
                lastException = exception;
            }
        }

        if (lastException is not null)
        {
            throw new AppException(new(
                AppErrorCode.UpdateVerificationFailed,
                "无法验证更新",
                "更新清单不可用或签名校验失败，应用不会下载任何文件。",
                "请稍后重试，或从官方 GitHub Releases 手动下载。",
                lastException.Message,
                "Update",
                IsRetryable: true), lastException);
        }
        return null;
    }

    public async Task<string> DownloadAsync(
        UpdateCandidate candidate,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        paths.EnsureCreated();
        string destination = Path.Combine(
            paths.UpdatesDirectory,
            $"LenxTool-{candidate.Release.Version}-Setup.exe");
        string temporary = destination + ".download";
        Exception? lastException = null;

        foreach (string mirror in candidate.Release.Mirrors)
        {
            if (!Uri.TryCreate(mirror, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
            try
            {
                using HttpClient client = httpClientFactory.CreateClient("LenxTool.Update");
                using HttpResponseMessage response = await client.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using (var output = new FileStream(
                                 temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                                 1024 * 256, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[1024 * 256];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        total += read;
                        if (candidate.Release.Size > 0) progress?.Report(total * 100d / candidate.Release.Size);
                    }
                }

                var file = new FileInfo(temporary);
                if (file.Length != candidate.Release.Size) throw new InvalidDataException("更新包大小不匹配。");
                string sha256 = await hashService.ComputeSha256Async(temporary, null, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(sha256, candidate.Release.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !UpdateManifestVerifier.VerifyPackageHashSignature(
                        sha256,
                        candidate.Release.PackageSignature,
                        options.PublicKeyPem))
                {
                    throw new CryptographicException("更新包哈希或发布签名无效。");
                }

                File.Move(temporary, destination, overwrite: true);
                progress?.Report(100);
                return destination;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or CryptographicException)
            {
                lastException = exception;
            }
        }

        throw new AppException(new(
            AppErrorCode.UpdateVerificationFailed,
            "更新下载失败",
            "所有更新镜像均不可用，或下载文件未通过完整性校验。",
            "请稍后重试或从官方 GitHub Releases 手动安装。",
            lastException?.Message,
            "Update",
            IsRetryable: true), lastException);
    }

    public void LaunchInstallerAndExit(string installerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        if (!File.Exists(installerPath)) throw new FileNotFoundException("找不到已下载的安装程序。", installerPath);
        var startInfo = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!
        };
        startInfo.ArgumentList.Add("/VERYSILENT");
        startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
        startInfo.ArgumentList.Add("/RESTARTAPPLICATIONS");
        Process.Start(startInfo);
        Environment.Exit(0);
    }

    private static SemanticVersion CurrentVersion()
    {
        Version version = Assembly.GetEntryAssembly()?.GetName().Version ?? new(0, 1, 0);
        return SemanticVersion.Parse(string.Create(
            CultureInfo.InvariantCulture,
            $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}"));
    }
}
