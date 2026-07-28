using LenxTool.Core.Models;

namespace LenxTool.Core.Errors;

public sealed class EntryExportException(
    EntryExportError error,
    Exception? innerException = null)
    : Exception(
        "The entry exporter returned a structured failure.",
        innerException)
{
    public EntryExportError Error { get; } =
        error ?? throw new ArgumentNullException(nameof(error));
}
