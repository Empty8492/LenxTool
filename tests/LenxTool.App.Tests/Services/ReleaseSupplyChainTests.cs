namespace LenxTool.App.Tests.Services;

public sealed class ReleaseSupplyChainTests
{
    private const string MicrosoftPublisher =
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    [Fact]
    public void BundledMicrosoftInstallersArePinnedAndVerifiedBeforePackaging()
    {
        string script = ReadFixture("Build-Release.ps1");
        int webViewDownload = script.IndexOf(
            "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
            StringComparison.Ordinal);
        int webViewValidation = script.IndexOf(
            "-ExpectedSha256 $WebViewBootstrapperSha256",
            StringComparison.Ordinal);
        int appRuntimeDownload = script.IndexOf(
            "https://aka.ms/windowsappsdk/2.3/2.3.1/windowsappruntimeinstall-x64.exe",
            StringComparison.Ordinal);
        int appRuntimeValidation = script.IndexOf(
            "-ExpectedSha256 $WindowsAppRuntimeSha256",
            StringComparison.Ordinal);
        int languageDownload = script.IndexOf(
            "Inno-Setup-Chinese-Simplified-Translation/main/ChineseSimplified.isl",
            StringComparison.Ordinal);
        int languageValidation = script.IndexOf(
            "$ActualChineseLanguageSha256 = (",
            StringComparison.Ordinal);
        int installerCompilation = script.IndexOf("& $Iscc", StringComparison.Ordinal);

        Assert.Contains(
            "$WebViewBootstrapperSha256 = \"23a55fbff920c0f99887848cfc25125f8f915df35638e01beb8f8fa9b5a0bc51\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$WindowsAppRuntimeSha256 = \"4011748ddf472b7e856d909fdfb4e9b19c3d23fcd8121039ac91f99d5ffa65db\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$ChineseLanguageSha256 = \"869e43e7c7b8d20c7e4397c8e98f7d1b7cf0528803acdf019ad350143ec85469\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA256", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", script, StringComparison.Ordinal);
        Assert.Contains("$Signature.Status -ne \"Valid\"", script, StringComparison.Ordinal);
        Assert.Contains(MicrosoftPublisher, script, StringComparison.Ordinal);
        Assert.True(webViewDownload >= 0 && webViewValidation > webViewDownload);
        Assert.True(appRuntimeDownload >= 0 && appRuntimeValidation > appRuntimeDownload);
        Assert.True(languageDownload >= 0 && languageValidation > languageDownload);
        Assert.True(installerCompilation > webViewValidation);
        Assert.True(installerCompilation > appRuntimeValidation);
        Assert.True(installerCompilation > languageValidation);
    }

    [Fact]
    public void InstallerRunsOnlyThePinnedMicrosoftInstallerNames()
    {
        string installer = ReadFixture("LenxTool.iss");

        Assert.Contains(
            "Source: \"assets\\MicrosoftEdgeWebview2Setup.exe\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Filename: \"{tmp}\\MicrosoftEdgeWebview2Setup.exe\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Source: \"assets\\WindowsAppRuntimeInstall-x64.exe\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Filename: \"{tmp}\\WindowsAppRuntimeInstall-x64.exe\"",
            installer,
            StringComparison.Ordinal);
    }

    private static string ReadFixture(string fileName) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}
