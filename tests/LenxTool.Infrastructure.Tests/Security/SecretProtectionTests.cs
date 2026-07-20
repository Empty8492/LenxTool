using System.Text;
using LenxTool.Infrastructure.Security;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.Infrastructure.Tests.Security;

public sealed class SecretProtectionTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx 密钥 tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DpapiStoreRoundTripsSecretWithoutWritingPlaintext()
    {
        AppPaths paths = new(_testRoot);
        using var store = new DpapiSecretStore(paths);
        const string secret = "gsk_test_secret_value_1234567890";

        await store.SetAsync("groq_api_key", secret, CancellationToken.None);
        string? loaded = await store.GetAsync("groq_api_key", CancellationToken.None);

        Assert.Equal(secret, loaded);
        byte[] encrypted = await File.ReadAllBytesAsync(store.StoragePath, CancellationToken.None);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);

        await store.DeleteAsync("groq_api_key", CancellationToken.None);
        Assert.Null(await store.GetAsync("groq_api_key", CancellationToken.None));
    }

    [Fact]
    public void RedactorRemovesCredentialsButPreservesRequestId()
    {
        const string source =
            "Authorization: Bearer abc.def.secret requestId=req-42 api_key=gsk_live_secret password=letmein";

        string redacted = SecretRedactor.Redact(source);

        Assert.DoesNotContain("abc.def.secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("gsk_live_secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("letmein", redacted, StringComparison.Ordinal);
        Assert.Contains("req-42", redacted, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
