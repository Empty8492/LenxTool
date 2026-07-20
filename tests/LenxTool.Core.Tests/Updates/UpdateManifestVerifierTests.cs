using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Updates;

namespace LenxTool.Core.Tests.Updates;

public sealed class UpdateManifestVerifierTests
{
    [Fact]
    public void VerifyAcceptsSignedPayloadAndRejectsTampering()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new UpdateManifest(
            1,
            "stable",
            [new("1.2.0", 1024, new string('a', 64), "package-signature", "更新说明", "1.0.0", false, ["https://example.test/setup.exe"])])));
        byte[] signature = signer.SignData(payload, HashAlgorithmName.SHA256);
        var envelope = new SignedUpdateManifest(
            Convert.ToBase64String(payload),
            Convert.ToBase64String(signature));

        UpdateManifest verified = UpdateManifestVerifier.Verify(envelope, signer.ExportSubjectPublicKeyInfoPem());

        Assert.Equal("1.2.0", Assert.Single(verified.Releases).Version);
        SignedUpdateManifest tampered = envelope with
        {
            PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))
        };
        Assert.Throws<CryptographicException>(() => UpdateManifestVerifier.Verify(
            tampered,
            signer.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void SelectCandidateHonorsMinimumAndMandatorySecurityUpdate()
    {
        UpdateManifest manifest = new(
            1,
            "stable",
            [new("2.0.0", 100, new string('b', 64), "sig", "安全修复", "1.5.0", true, ["https://example.test/setup.exe"])]);

        UpdateCandidate? candidate = UpdateManifestVerifier.SelectCandidate(
            manifest,
            SemanticVersion.Parse("1.0.0"));

        Assert.NotNull(candidate);
        Assert.True(candidate.IsMandatory);
        Assert.True(candidate.IsBelowMinimumVersion);
    }
}
