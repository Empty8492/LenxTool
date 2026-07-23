namespace LenxTool.Core.Models;

public sealed record TagItem(
    string Id,
    string Name,
    string Color,
    DateTimeOffset CreatedAt);
