using LenxTool.Core.Models;

namespace LenxTool.Core.Feeds;

public static class EntryViewClassifier
{
    /// <summary>
    /// Resolves a stable per-entry view without treating a legacy catalog
    /// default as an explicit override.
    /// </summary>
    /// <param name="explicitOverride">
    /// A separately tracked administrator override; <see langword="null"/>
    /// enables media inference.
    /// </param>
    /// <param name="enclosures">
    /// Enclosures in normalized feed declaration order. The first allowed,
    /// verified media item wins.
    /// </param>
    /// <param name="primaryContentMedia">
    /// An optional structured, verified primary-media signal from the content
    /// extraction layer.
    /// </param>
    public static EntryViewKind Classify(
        EntryViewKind? explicitOverride,
        IReadOnlyList<FeedAttachmentClassification>? enclosures,
        FeedAttachmentClassification? primaryContentMedia)
    {
        if (explicitOverride is EntryViewKind overrideValue)
        {
            return Enum.IsDefined(overrideValue)
                ? overrideValue
                : EntryViewKind.Article;
        }

        if (enclosures is not null)
        {
            foreach (FeedAttachmentClassification enclosure in enclosures)
            {
                EntryViewKind? viewKind = MapVerifiedMedia(enclosure);
                if (viewKind is not null)
                {
                    return viewKind.Value;
                }
            }
        }

        return MapVerifiedMedia(primaryContentMedia)
            ?? EntryViewKind.Article;
    }

    private static EntryViewKind? MapVerifiedMedia(
        FeedAttachmentClassification? media)
    {
        if (media is null
            || media.UrlStatus != FeedAttachmentUrlStatus.Allowed
            || media.TypeStatus != FeedAttachmentTypeStatus.Verified)
        {
            return null;
        }

        return media.Kind switch
        {
            FeedAttachmentKind.Image => EntryViewKind.Picture,
            FeedAttachmentKind.Audio => EntryViewKind.Audio,
            FeedAttachmentKind.Video => EntryViewKind.Video,
            _ => null
        };
    }
}
