namespace LenxTool.Core.Models;

public sealed record EntryState(
    string EntryId,
    string LocalProfile,
    bool IsRead,
    bool IsStarred,
    double Progress,
    string Note,
    DateTimeOffset UpdatedAt);

public sealed record EntryStatePatch(
    bool? IsRead = null,
    bool? IsStarred = null,
    double? Progress = null,
    string? Note = null);
