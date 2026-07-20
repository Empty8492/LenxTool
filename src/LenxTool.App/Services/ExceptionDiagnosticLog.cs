using System.IO;
using System.Text.Json;
using LenxTool.Infrastructure.Security;

namespace LenxTool.App.Services;

public sealed class ExceptionDiagnosticLog(string logsDirectory)
{
    private readonly Lock _writeLock = new();
    private readonly string _logsDirectory = Path.GetFullPath(logsDirectory);

    public void Write(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Directory.CreateDirectory(_logsDirectory);

        var entry = new ExceptionLogEntry(
            DateTimeOffset.UtcNow,
            exception.GetType().FullName ?? exception.GetType().Name,
            SecretRedactor.Redact(exception.Message),
            SecretRedactor.Redact(exception.ToString()));
        string line = JsonSerializer.Serialize(entry);
        string path = Path.Combine(_logsDirectory, $"exceptions-{DateTime.UtcNow:yyyyMMdd}.jsonl");

        lock (_writeLock)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private sealed record ExceptionLogEntry(
        DateTimeOffset Timestamp,
        string ExceptionType,
        string Message,
        string Details);
}
