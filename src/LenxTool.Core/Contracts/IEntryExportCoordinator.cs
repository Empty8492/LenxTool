using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IEntryExportCoordinator
{
    IReadOnlyList<EntryExportCapability> Capabilities { get; }

    Task<EntryExportResult> ExportAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken);
}
