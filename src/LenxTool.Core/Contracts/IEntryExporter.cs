using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IEntryExporter
{
    EntryExportCapability Capability { get; }

    Task<EntryExportResult> ExportAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken);
}
