namespace LenxTool.Core.Models;

public sealed record FavoriteItem(
    string Id,
    string EntityType,
    string EntityId,
    string Note,
    DateTimeOffset CreatedAt);
