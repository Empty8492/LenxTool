using LenxTool.App.Services;

namespace LenxTool.App.Tests.Services;

public sealed class ExceptionDiagnosticLogTests
{
    [Fact]
    public void WritePersistsExceptionTypeAndRedactsSecrets()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"LenxTool-tests-{Guid.NewGuid():N}");
        try
        {
            var log = new ExceptionDiagnosticLog(directory);

            log.Write(new InvalidOperationException("api_key=super-secret-value"));

            string file = Assert.Single(Directory.GetFiles(directory, "exceptions-*.jsonl"));
            string content = File.ReadAllText(file);
            Assert.Contains(nameof(InvalidOperationException), content, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-value", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
