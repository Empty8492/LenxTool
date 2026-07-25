namespace LenxTool.Core.Models;

public enum FeedAttachmentKind
{
    Unknown,
    Image,
    Audio,
    Video
}

public enum FeedAttachmentTypeStatus
{
    Verified,
    Unverified,
    Conflicting,
    Unsupported
}

public enum FeedAttachmentUrlStatus
{
    Allowed,
    Blocked
}

public sealed record FeedAttachmentClassification(
    string? SafeUrl,
    FeedAttachmentKind Kind,
    FeedAttachmentTypeStatus TypeStatus,
    FeedAttachmentUrlStatus UrlStatus,
    string? NormalizedMediaType,
    string? FileExtension,
    long? Length,
    string? Title)
{
    public bool IsTypeVerified =>
        TypeStatus == FeedAttachmentTypeStatus.Verified;
}
