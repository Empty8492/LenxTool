using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IOpmlCodec
{
    Task<OpmlDocument> ParseAsync(Stream source, CancellationToken cancellationToken);

    Task WriteAsync(Stream destination, OpmlDocument document, CancellationToken cancellationToken);
}
