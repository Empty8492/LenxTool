using LenxTool.App.Services;

namespace LenxTool.App.Tests.Services;

public sealed class DesktopFileDialogServiceTests
{
    [Fact]
    public void OpenUriRejectsNonWebTargets()
    {
        var service = new DesktopFileDialogService();

        string[] unsafeTargets =
        [
            "file:///C:/Windows/System32/calc.exe",
            "javascript:alert('x')",
            "not a uri"
        ];

        Assert.All(unsafeTargets, uri =>
            Assert.Throws<ArgumentException>(() => service.OpenUri(uri)));
    }
}
