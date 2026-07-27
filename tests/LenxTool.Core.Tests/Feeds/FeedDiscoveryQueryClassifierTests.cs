using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Feeds;

public sealed class FeedDiscoveryQueryClassifierTests
{
    [Theory]
    [InlineData(
        "HTTPS://Example.COM:443/feed/",
        "https://example.com/feed/",
        FeedDiscoveryQueryKind.Url)]
    [InlineData(
        "http://example.com/rss.xml",
        "http://example.com/rss.xml",
        FeedDiscoveryQueryKind.Url)]
    [InlineData(
        "rsshub://GitHub/trending/daily/",
        "rsshub://github/trending/daily/",
        FeedDiscoveryQueryKind.RssHubRoute)]
    [InlineData(
        "  .NET 开发者周刊  ",
        ".NET 开发者周刊",
        FeedDiscoveryQueryKind.Keyword)]
    public void ClassifyReturnsDeterministicNormalizedQuery(
        string input,
        string expectedValue,
        FeedDiscoveryQueryKind expectedKind)
    {
        FeedDiscoveryQuery result = FeedDiscoveryQueryClassifier.Classify(input);

        Assert.True(result.IsValid);
        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(expectedValue, result.NormalizedValue);
        Assert.Equal(FeedDiscoveryQueryError.None, result.Error);
    }

    [Fact]
    public void UnicodeKeywordUsesCompatibilityNormalization()
    {
        FeedDiscoveryQuery result =
            FeedDiscoveryQueryClassifier.Classify("  ＲＳＳ　阅读器  ");

        Assert.Equal(FeedDiscoveryQueryKind.Keyword, result.Kind);
        Assert.Equal("RSS 阅读器", result.NormalizedValue);
    }

    [Theory]
    [InlineData(null, FeedDiscoveryQueryError.Empty)]
    [InlineData("", FeedDiscoveryQueryError.Empty)]
    [InlineData("   ", FeedDiscoveryQueryError.Empty)]
    [InlineData("javascript:alert(1)", FeedDiscoveryQueryError.UnsupportedScheme)]
    [InlineData("file:///c:/private.xml", FeedDiscoveryQueryError.UnsupportedScheme)]
    [InlineData("https://user:secret@example.com/feed", FeedDiscoveryQueryError.CredentialsNotAllowed)]
    [InlineData("https://example.com/feed#latest", FeedDiscoveryQueryError.FragmentNotAllowed)]
    [InlineData("rsshub://", FeedDiscoveryQueryError.InvalidRssHubRoute)]
    [InlineData("rsshub://github/trending#daily", FeedDiscoveryQueryError.FragmentNotAllowed)]
    [InlineData("reader\u0000news", FeedDiscoveryQueryError.ControlCharacter)]
    public void InvalidInputIsRejectedWithoutAUsableValue(
        string? input,
        FeedDiscoveryQueryError expectedError)
    {
        FeedDiscoveryQuery result = FeedDiscoveryQueryClassifier.Classify(input);

        Assert.False(result.IsValid);
        Assert.Equal(FeedDiscoveryQueryKind.Invalid, result.Kind);
        Assert.Null(result.NormalizedValue);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public void OverlongInputIsRejected()
    {
        string input = string.Concat(
            Enumerable.Repeat("读", FeedDiscoveryQueryClassifier.MaximumInputCodePoints + 1));

        FeedDiscoveryQuery result = FeedDiscoveryQueryClassifier.Classify(input);

        Assert.Equal(FeedDiscoveryQueryError.TooLong, result.Error);
    }

    [Fact]
    public void UrlPathKeepsCaseAndTrailingSlashSemantics()
    {
        FeedDiscoveryQuery upper =
            FeedDiscoveryQueryClassifier.Classify("https://example.com/Feed/");
        FeedDiscoveryQuery lower =
            FeedDiscoveryQueryClassifier.Classify("https://example.com/feed");

        Assert.Equal("https://example.com/Feed/", upper.NormalizedValue);
        Assert.Equal("https://example.com/feed", lower.NormalizedValue);
        Assert.NotEqual(upper.NormalizedValue, lower.NormalizedValue);
    }
}
