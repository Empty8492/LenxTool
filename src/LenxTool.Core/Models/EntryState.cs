namespace LenxTool.Core.Models;

public sealed record EntryState(
    string EntryId,
    string LocalProfile,
    bool IsRead,
    bool IsStarred,
    bool IsHidden,
    double Progress,
    string Note,
    DateTimeOffset UpdatedAt);

public sealed record EntryStatePatch(
    bool? IsRead = null,
    bool? IsStarred = null,
    bool? IsHidden = null,
    double? Progress = null,
    string? Note = null);
