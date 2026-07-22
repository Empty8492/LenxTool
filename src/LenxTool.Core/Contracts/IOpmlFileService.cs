using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IOpmlFileService
{
    Task<OpmlDocument> LoadAsync(string path, CancellationToken cancellationToken);
    Task SaveAsync(string path, OpmlDocument document, CancellationToken cancellationToken);
}
