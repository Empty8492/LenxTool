using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Feeds;

public sealed class FeedAttachmentClassifierTests
{
    [Theory]
    [InlineData(
        "https://cdn.example.com/cover.jpg",
        "image/jpeg",
        FeedAttachmentKind.Image)]
    [InlineData(
        "https://cdn.example.com/podcast.mp3",
        "audio/mpeg",
        FeedAttachmentKind.Audio)]
    [InlineData(
        "https://cdn.example.com/video.mp4",
        "video/mp4",
        FeedAttachmentKind.Video)]
    public void MatchingMimeAndExtensionAreVerified(
        string url,
        string mediaType,
        FeedAttachmentKind expectedKind)
    {
        FeedAttachmentClassification result =
            FeedAttachmentClassifier.Classify(
                new(url, mediaType, 1024, "Media"),
                baseUrl: null);

        Assert.Equal(FeedAttachmentUrlStatus.Allowed, result.UrlStatus);
        Assert.Equal(url, result.SafeUrl);
        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(
            FeedAttachmentTypeStatus.Verified,
            result.TypeStatus);
        Assert.True(result.IsTypeVerified);
    }

    [Fact]
    public void MissingMimeAndLengthRemainUnverifiedAndUnknown()
    {
        FeedAttachmentClassification result =
            FeedAttachmentClassifier.Classify(
                new(
                    "/episodes/latest.mp3",
                    null,
                    null,
                    "Latest episode"),
                "https://podcast.example/feed.xml");

        Assert.Equal(
            "https://podcast.example/episodes/latest.mp3",
            result.SafeUrl);
        Assert.Equal(FeedAttachmentKind.Audio, result.Kind);
        Assert.Equal(
            FeedAttachmentTypeStatus.Unverified,
            result.TypeStatus);
        Assert.Null(result.Length);
        Assert.False(result.IsTypeVerified);
    }

    [Fact]
    public void MimeAndExtensionConflictIsNotTrustedAsMedia()
    {
        FeedAttachmentClassification result =
            FeedAttachmentClassifier.Classify(
                new(
                    "https://cdn.example.com/payload.mp3",
                    "video/mp4; codecs=avc1",
                    4096,
                    null),
                baseUrl: null);

        Assert.Equal(FeedAttachmentKind.Unknown, result.Kind);
        Assert.Equal(
            FeedAttachmentTypeStatus.Conflicting,
            result.TypeStatus);
        Assert.Equal("video/mp4", result.NormalizedMediaType);
        Assert.Equal(".mp3", result.FileExtension);
        Assert.False(result.IsTypeVerified);
    }

    [Theory]
    [InlineData("file:///tmp/audio.mp3")]
    [InlineData("https://127.0.0.1/audio.mp3")]
    [InlineData("https://10.0.0.8/audio.mp3")]
    [InlineData("https://[::1]/audio.mp3")]
    [InlineData("https://player.internal/audio.mp3")]
    [InlineData("https://example.com:8443/audio.mp3")]
    public void UnsafeTargetsAreBlockedBeforeOpening(string url)
    {
        FeedAttachmentClassification result =
            FeedAttachmentClassifier.Classify(
                new(url, "audio/mpeg", 1024, null),
                baseUrl: null);

        Assert.Null(result.SafeUrl);
        Assert.Equal(
            FeedAttachmentUrlStatus.Blocked,
            result.UrlStatus);
    }

    [Fact]
    public void HttpAndHttpsDefaultPortsRemainAvailableAsExternalLinks()
    {
        FeedAttachmentClassification http =
            FeedAttachmentClassifier.Classify(
                new(
                    "http://media.example.com:80/file.ogg#player",
                    "audio/ogg",
                    512,
                    null),
                baseUrl: null);
        FeedAttachmentClassification https =
            FeedAttachmentClassifier.Classify(
                new(
                    "https://media.example.com:443/file.webm",
                    "video/webm",
                    1024,
                    null),
                baseUrl: null);

        Assert.Equal(
            "http://media.example.com/file.ogg",
            http.SafeUrl);
        Assert.Equal(
            "https://media.example.com/file.webm",
            https.SafeUrl);
    }
}
