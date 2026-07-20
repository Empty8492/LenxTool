using System.Security.Cryptography;
using System.Text.Json;

namespace LenxTool.Core.Updates;

public sealed record SignedUpdateManifest(string PayloadBase64, string SignatureBase64);

public sealed record UpdateManifest(
    int SchemaVersion,
    string Channel,
    IReadOnlyList<UpdateRelease> Releases);

public sealed record UpdateRelease(
    string Version,
    long Size,
    string Sha256,
    string PackageSignature,
    string ReleaseNotes,
    string MinimumSupportedVersion,
    bool MandatorySecurityUpdate,
    IReadOnlyList<string> Mirrors);

public sealed record UpdateCandidate(
    UpdateRelease Release,
    bool IsMandatory,
    bool IsBelowMinimumVersion);

public static class UpdateManifestVerifier
{
    public static UpdateManifest Verify(SignedUpdateManifest envelope, string publicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        byte[] payload = Convert.FromBase64String(envelope.PayloadBase64);
        byte[] signature = Convert.FromBase64String(envelope.SignatureBase64);
        using ECDsa verifier = ECDsa.Create();
        verifier.ImportFromPem(publicKeyPem);
        if (!verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256))
        {
            throw new CryptographicException("更新清单签名无效。");
        }

        UpdateManifest manifest = JsonSerializer.Deserialize<UpdateManifest>(payload)
            ?? throw new JsonException("更新清单内容为空。");
        if (manifest.SchemaVersion != 1) throw new NotSupportedException("不支持的更新清单版本。");
        return manifest;
    }

    public static UpdateCandidate? SelectCandidate(
        UpdateManifest manifest,
        SemanticVersion currentVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(currentVersion);

        UpdateRelease? release = manifest.Releases
            .Select(item => (Release: item, Version: SemanticVersion.Parse(item.Version)))
            .Where(item => item.Version > currentVersion)
            .OrderByDescending(item => item.Version)
            .Select(item => item.Release)
            .FirstOrDefault();
        if (release is null) return null;

        SemanticVersion minimum = SemanticVersion.Parse(release.MinimumSupportedVersion);
        bool belowMinimum = currentVersion < minimum;
        return new(release, release.MandatorySecurityUpdate || belowMinimum, belowMinimum);
    }

    public static bool VerifyPackageHashSignature(
        string sha256Hex,
        string signatureBase64,
        string publicKeyPem)
    {
        byte[] hash = Convert.FromHexString(sha256Hex);
        byte[] signature = Convert.FromBase64String(signatureBase64);
        using ECDsa verifier = ECDsa.Create();
        verifier.ImportFromPem(publicKeyPem);
        return verifier.VerifyHash(hash, signature);
    }
}
