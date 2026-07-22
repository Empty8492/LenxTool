using System.Text;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedDocumentParserTests
{
    private const string FeedId = "10000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset FetchedAt = new(2026, 7, 22, 8, 0, 0, TimeSpan.Zero);
    private readonly FeedDocumentParser _parser = new();

    [Fact]
    public void RssFixtureParsesCdataAuthorCategoriesEnclosureAndDeduplicatesGuid()
    {
        ParsedFeedDocument document = ParseFixture("rss-realworld.xml", "https://example.com/feed.xml");

        Assert.Equal("示例技术周刊", document.Title);
        Assert.Equal("https://example.com/weekly/", document.SiteUrl);
        Assert.Equal(FeedDocumentKind.Rss20, document.Kind);
        Assert.Equal(2, document.Entries.Count);

        FeedEntry first = document.Entries[0];
        Assert.Equal("post-42", first.ExternalId);
        Assert.Equal("版本 42 <发布>", first.Title);
        Assert.Equal("编辑 Alice", first.Author);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 2, 30, 0, TimeSpan.Zero), first.PublishedAt);
        Assert.Equal(["技术", ".NET"], first.Categories);
        Assert.Equal("https://example.com/posts/42?edition=cn", first.NormalizedUrl);
        Assert.Equal("摘要 & 更多", first.Summary);
        Assert.Contains("正文", first.SanitizedContent, StringComparison.Ordinal);
        Assert.Contains("安全文本", first.SanitizedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", first.SanitizedContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", first.SanitizedContent, StringComparison.OrdinalIgnoreCase);
        FeedEnclosure enclosure = Assert.Single(first.Enclosures);
        Assert.Equal("https://cdn.example.com/audio/42.mp3", enclosure.Url);
        Assert.Equal("audio/mpeg", enclosure.MediaType);
        Assert.Equal(123456, enclosure.Length);

        FeedEntry signed = document.Entries[1];
        Assert.Equal(signed.NormalizedUrl, signed.ExternalId);
        Assert.Contains("utm_source=rss", signed.NormalizedUrl, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Signature=abc123", signed.NormalizedUrl, StringComparison.Ordinal);
        Assert.Null(signed.PublishedAt);
    }

    [Fact]
    public void AtomFixtureParsesIdsDatesFeedAuthorCategoriesAndEnclosures()
    {
        ParsedFeedDocument document = ParseFixture("atom-realworld.xml", "https://example.org/feeds/atom.xml");

        Assert.Equal("Atom Updates", document.Title);
        Assert.Equal("https://example.org/blog/", document.SiteUrl);
        Assert.Equal(FeedDocumentKind.Atom, document.Kind);
        Assert.Equal(2, document.Entries.Count);

        FeedEntry first = document.Entries[0];
        Assert.Equal("tag:example.org,2026:entry-1", first.ExternalId);
        Assert.Equal("Entry Author", first.Author);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero), first.PublishedAt);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 13, 30, 0, TimeSpan.Zero), first.UpdatedAt);
        Assert.Equal(["release", "产品"], first.Categories);
        Assert.Equal("https://example.org/posts/one?ref=home", first.NormalizedUrl);
        Assert.Equal("Short summary", first.Summary);
        Assert.Contains("Full content", first.SanitizedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", first.SanitizedContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bad()", first.SanitizedContent, StringComparison.OrdinalIgnoreCase);
        FeedEnclosure enclosure = Assert.Single(first.Enclosures);
        Assert.Equal("video/mp4", enclosure.MediaType);
        Assert.Equal(98765, enclosure.Length);
        Assert.Equal("Demo", enclosure.Title);
        Assert.Null(document.Entries[1].UpdatedAt);
    }

    [Fact]
    public void StableIdentityPrefersGuidThenUrlThenFeedScopedContentFingerprint()
    {
        const string rss = """
            <rss version="2.0"><channel><title>IDs</title>
              <item><guid>guid-1</guid><link>https://example.com/a</link><title>A</title></item>
              <item><link>https://example.com/b?utm_source=x&amp;id=2</link><title>B</title></item>
              <item><title>C</title><description>body</description></item>
            </channel></rss>
            """;

        ParsedFeedDocument first = Parse(rss, FeedId, FetchedAt);
        ParsedFeedDocument repeated = Parse(rss, FeedId, FetchedAt.AddDays(1));
        ParsedFeedDocument otherFeed = Parse(
            rss,
            "10000000-0000-4000-8000-000000000002",
            FetchedAt);

        Assert.Equal("guid-1", first.Entries[0].ExternalId);
        Assert.Equal("https://example.com/b?id=2", first.Entries[1].ExternalId);
        Assert.StartsWith("urn:lenxtool:fingerprint:", first.Entries[2].ExternalId, StringComparison.Ordinal);
        Assert.Equal(first.Entries.Select(entry => entry.Id), repeated.Entries.Select(entry => entry.Id));
        Assert.NotEqual(first.Entries[2].Id, otherFeed.Entries[2].Id);
        Assert.All(first.Entries, entry => Assert.Equal(64, entry.Id.Length));
    }

    [Fact]
    public void IdentityAndSignatureQueriesRemainByteOrderedWhileKnownTrackingIsRemoved()
    {
        const string rss = """
            <rss version="2.0"><channel><title>URLs</title>
              <item><title>Signed</title><link>https://EXAMPLE.com:443/a/../download?b=2&amp;utm_source=x&amp;token=a%2Bb&amp;a=1#frag</link></item>
              <item><title>Tracked</title><link>https://EXAMPLE.com:443/a/../post?b=2&amp;utm_source=x&amp;a=1#frag</link></item>
            </channel></rss>
            """;

        ParsedFeedDocument document = Parse(rss);

        Assert.Equal(
            "https://example.com/download?b=2&utm_source=x&token=a%2Bb&a=1",
            document.Entries[0].NormalizedUrl);
        Assert.Equal("https://example.com/post?b=2&a=1", document.Entries[1].NormalizedUrl);
    }

    [Fact]
    public void MissingFieldsAndInvalidDatesDoNotCrashOrCreateEmptyIdentity()
    {
        const string rss = """
            <rss version="2.0"><channel><title></title>
              <item><pubDate>invalid</pubDate></item>
            </channel></rss>
            """;

        ParsedFeedDocument document = Parse(rss);

        FeedEntry entry = Assert.Single(document.Entries);
        Assert.Equal("未命名 Feed", document.Title);
        Assert.Equal("未命名条目", entry.Title);
        Assert.Null(entry.PublishedAt);
        Assert.StartsWith("urn:lenxtool:fingerprint:", entry.ExternalId, StringComparison.Ordinal);
        Assert.NotEmpty(entry.ContentHash);
    }

    [Fact]
    public void UnsupportedSchemesAreIgnoredForLinksAndEnclosures()
    {
        const string rss = """
            <rss version="2.0"><channel><title>Unsafe</title>
              <item><title>Entry</title><link>file:///etc/passwd</link>
                <enclosure url="javascript:alert(1)" type="text/plain" length="1" />
              </item>
            </channel></rss>
            """;

        FeedEntry entry = Assert.Single(Parse(rss).Entries);

        Assert.Null(entry.NormalizedUrl);
        Assert.Empty(entry.Enclosures);
        Assert.StartsWith("urn:lenxtool:fingerprint:", entry.ExternalId, StringComparison.Ordinal);
    }

    [Fact]
    public void DtdAndExternalEntitiesAreRejectedWithoutLeakingEntityTarget()
    {
        const string xml = "<!DOCTYPE rss [<!ENTITY xxe SYSTEM 'file:///sensitive/path'>]>" +
            "<rss version='2.0'><channel><title>&xxe;</title></channel></rss>";

        AppException error = Assert.Throws<AppException>(() => Parse(xml));

        Assert.Equal(AppErrorCode.InvalidRequest, error.Error.Code);
        Assert.DoesNotContain("sensitive", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<rss version='1.0'><channel /></rss>")]
    [InlineData("<feed xmlns='urn:not-atom'><title>x</title></feed>")]
    [InlineData("<rss version='2.0'><channel><item></channel></rss>")]
    public void UnsupportedOrMalformedDocumentsAreRejected(string xml)
    {
        AppException error = Assert.Throws<AppException>(() => Parse(xml));

        Assert.Equal(AppErrorCode.InvalidRequest, error.Error.Code);
    }

    [Fact]
    public void OversizedDocumentIsRejectedBeforeXmlMaterialization()
    {
        var parser = new FeedDocumentParser(new FeedParserOptions(1024, 100));
        string xml = "<rss version='2.0'><channel><title>" + new string('x', 1100) + "</title></channel></rss>";

        AppException error = Assert.Throws<AppException>(() => parser.Parse(
            FeedId,
            "https://example.com/feed.xml",
            Encoding.UTF8.GetBytes(xml),
            FetchedAt));

        Assert.Equal(AppErrorCode.InvalidRequest, error.Error.Code);
    }

    [Fact]
    public void EntryLimitIsRejectedBeforeEntriesAreParsed()
    {
        var parser = new FeedDocumentParser(new FeedParserOptions(4096, 1));
        const string xml = "<rss version='2.0'><channel><title>x</title><item/><item/></channel></rss>";

        AppException error = Assert.Throws<AppException>(() => parser.Parse(
            FeedId,
            "https://example.com/feed.xml",
            Encoding.UTF8.GetBytes(xml),
            FetchedAt));

        Assert.Equal(AppErrorCode.InvalidRequest, error.Error.Code);
    }

    [Fact]
    public void IdentifierAndContentHashesRemainCaseSensitive()
    {
        const string lower = "<rss version='2.0'><channel><title>x</title><item><guid>entry</guid><description>body</description></item></channel></rss>";
        const string upper = "<rss version='2.0'><channel><title>x</title><item><guid>ENTRY</guid><description>BODY</description></item></channel></rss>";

        FeedEntry lowerEntry = Assert.Single(Parse(lower).Entries);
        FeedEntry upperEntry = Assert.Single(Parse(upper).Entries);

        Assert.NotEqual(lowerEntry.Id, upperEntry.Id);
        Assert.NotEqual(lowerEntry.ContentHash, upperEntry.ContentHash);
    }

    [Fact]
    public void ExecutableBlocksAreRemovedFromTitlesAndContent()
    {
        const string xml = "<rss version='2.0'><channel><title>x</title><item><title><![CDATA[Before<script>titleBad()</script><发布>]]></title><description><![CDATA[Body<script>bodyBad()</script>]]></description></item></channel></rss>";

        FeedEntry entry = Assert.Single(Parse(xml).Entries);

        Assert.Equal("Before <发布>", entry.Title);
        Assert.DoesNotContain("titleBad", entry.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bodyBad", entry.SanitizedContent, StringComparison.OrdinalIgnoreCase);
    }

    private ParsedFeedDocument ParseFixture(string name, string feedUrl)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Feeds", name);
        return _parser.Parse(FeedId, feedUrl, File.ReadAllBytes(path), FetchedAt);
    }

    private ParsedFeedDocument Parse(
        string xml,
        string feedId = FeedId,
        DateTimeOffset? fetchedAt = null) =>
        _parser.Parse(
            feedId,
            "https://example.com/feeds/source.xml",
            Encoding.UTF8.GetBytes(xml),
            fetchedAt ?? FetchedAt);
}
