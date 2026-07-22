using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedParser
{
    ParsedFeedDocument Parse(
        string feedId,
        string feedUrl,
        ReadOnlyMemory<byte> content,
        DateTimeOffset fetchedAt);
}
