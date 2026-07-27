using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Feeds;

public sealed class EntryViewClassifierTests
{
    [Theory]
    [InlineData(EntryViewKind.Article)]
    [InlineData(EntryViewKind.Picture)]
    [InlineData(EntryViewKind.Audio)]
    [InlineData(EntryViewKind.Video)]
    [InlineData(EntryViewKind.Notification)]
    public void ExplicitOverrideWinsOverMediaEvidence(
        EntryViewKind explicitOverride)
    {
        EntryViewKind result = EntryViewClassifier.Classify(
            explicitOverride,
            [Verified(FeedAttachmentKind.Video)],
            Verified(FeedAttachmentKind.Audio));

        Assert.Equal(explicitOverride, result);
    }

    [Fact]
    public void UnknownExplicitOverrideFallsBackToArticle()
    {
        EntryViewKind result = EntryViewClassifier.Classify(
            (EntryViewKind)999,
            [Verified(FeedAttachmentKind.Video)],
            Verified(FeedAttachmentKind.Audio));

        Assert.Equal(EntryViewKind.Article, result);
    }

    [Theory]
    [InlineData(FeedAttachmentKind.Image, EntryViewKind.Picture)]
    [InlineData(FeedAttachmentKind.Audio, EntryViewKind.Audio)]
    [InlineData(FeedAttachmentKind.Video, EntryViewKind.Video)]
    public void VerifiedEnclosureDeterminesViewKind(
        FeedAttachmentKind attachmentKind,
        EntryViewKind expected)
    {
        EntryViewKind result = EntryViewClassifier.Classify(
            explicitOverride: null,
            [Verified(attachmentKind)],
            primaryContentMedia: null);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FirstVerifiedEnclosureWinsInDeclarationOrder()
    {
        FeedAttachmentClassification[] enclosures =
        [
            Untrusted(
                FeedAttachmentKind.Audio,
                FeedAttachmentTypeStatus.Conflicting),
            Verified(FeedAttachmentKind.Image),
            Verified(FeedAttachmentKind.Video)
        ];

        EntryViewKind result = EntryViewClassifier.Classify(
            explicitOverride: null,
            enclosures,
            Verified(FeedAttachmentKind.Audio));

        Assert.Equal(EntryViewKind.Picture, result);
    }

    [Fact]
    public void UntrustedEnclosuresAreSkippedBeforePrimaryContentMedia()
    {
        FeedAttachmentClassification[] enclosures =
        [
            Untrusted(
                FeedAttachmentKind.Image,
                FeedAttachmentTypeStatus.Unverified),
            Untrusted(
                FeedAttachmentKind.Audio,
                FeedAttachmentTypeStatus.Conflicting),
            Untrusted(
                FeedAttachmentKind.Video,
                FeedAttachmentTypeStatus.Verified,
                FeedAttachmentUrlStatus.Blocked)
        ];

        EntryViewKind result = EntryViewClassifier.Classify(
            explicitOverride: null,
            enclosures,
            Verified(FeedAttachmentKind.Audio));

        Assert.Equal(EntryViewKind.Audio, result);
    }

    [Theory]
    [InlineData(FeedAttachmentKind.Image, EntryViewKind.Picture)]
    [InlineData(FeedAttachmentKind.Audio, EntryViewKind.Audio)]
    [InlineData(FeedAttachmentKind.Video, EntryViewKind.Video)]
    public void VerifiedPrimaryContentMediaDeterminesViewKind(
        FeedAttachmentKind attachmentKind,
        EntryViewKind expected)
    {
        EntryViewKind result = EntryViewClassifier.Classify(
            explicitOverride: null,
            enclosures: null,
            Verified(attachmentKind));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void UntrustedPrimaryContentMediaFallsBackToArticle()
    {
        EntryViewKind result = EntryViewClassifier.Classify(
            explicitOverride: null,
            enclosures: null,
            Untrusted(
                FeedAttachmentKind.Image,
                FeedAttachmentTypeStatus.Unverified));

        Assert.Equal(EntryViewKind.Article, result);
    }

    [Fact]
    public void MissingMediaFallsBackToArticle()
    {
        EntryViewKind result = EntryViewClassifier.Classify(
            explicitOverride: null,
            enclosures: null,
            primaryContentMedia: null);

        Assert.Equal(EntryViewKind.Article, result);
    }

    [Fact]
    public void UnknownMediaFallsBackToArticle()
    {
        EntryViewKind result = EntryViewClassifier.Classify(
            explicitOverride: null,
            [Verified(FeedAttachmentKind.Unknown)],
            Verified(FeedAttachmentKind.Unknown));

        Assert.Equal(EntryViewKind.Article, result);
    }

    [Theory]
    [InlineData("https://media.example.com/photo.jpg", null)]
    [InlineData("https://media.example.com/photo", "image/jpeg")]
    public void UrlOrMimeAloneDoesNotDetermineViewKind(
        string url,
        string? mediaType)
    {
        FeedAttachmentClassification enclosure =
            FeedAttachmentClassifier.Classify(
                new(url, mediaType, 1024, null),
                baseUrl: null);

        EntryViewKind result = EntryViewClassifier.Classify(
            explicitOverride: null,
            [enclosure],
            primaryContentMedia: null);

        Assert.Equal(EntryViewKind.Article, result);
    }

    [Fact]
    public void SameInputAlwaysProducesTheSameClassification()
    {
        FeedAttachmentClassification[] enclosures =
        [
            Untrusted(
                FeedAttachmentKind.Image,
                FeedAttachmentTypeStatus.Unverified),
            Verified(FeedAttachmentKind.Video)
        ];

        EntryViewKind[] results = Enumerable.Range(0, 20)
            .Select(_ => EntryViewClassifier.Classify(
                explicitOverride: null,
                enclosures,
                Verified(FeedAttachmentKind.Audio)))
            .ToArray();

        Assert.All(
            results,
            result => Assert.Equal(EntryViewKind.Video, result));
    }

    private static FeedAttachmentClassification Verified(
        FeedAttachmentKind kind)
    {
        (string fileName, string mediaType, string extension) = kind switch
        {
            FeedAttachmentKind.Image => ("picture.jpg", "image/jpeg", ".jpg"),
            FeedAttachmentKind.Audio => ("episode.mp3", "audio/mpeg", ".mp3"),
            FeedAttachmentKind.Video => ("clip.mp4", "video/mp4", ".mp4"),
            _ => ("resource.bin", "application/octet-stream", ".bin")
        };
        return new(
            $"https://media.example.com/{fileName}",
            kind,
            FeedAttachmentTypeStatus.Verified,
            FeedAttachmentUrlStatus.Allowed,
            mediaType,
            extension,
            1024,
            null);
    }

    private static FeedAttachmentClassification Untrusted(
        FeedAttachmentKind kind,
        FeedAttachmentTypeStatus typeStatus,
        FeedAttachmentUrlStatus urlStatus = FeedAttachmentUrlStatus.Allowed) =>
        new(
            urlStatus == FeedAttachmentUrlStatus.Allowed
                ? "https://media.example.com/resource.bin"
                : null,
            kind,
            typeStatus,
            urlStatus,
            "application/octet-stream",
            ".bin",
            1024,
            null);
}
