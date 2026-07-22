using System.Text;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedFixtureCorpusTests
{
    private const string FeedId = "20000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset FetchedAt = new(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> FixtureNames() =>
        GetFixturePaths().Select(path => new object[] { Path.GetFileName(path) });

    [Fact]
    public void CorpusContainsAtLeastTwentyIndependentRssAndAtomFixtures()
    {
        string[] fixtureNames = GetFixturePaths().Select(Path.GetFileName).ToArray()!;

        Assert.True(fixtureNames.Length >= 20, $"Expected at least 20 fixtures, found {fixtureNames.Length}.");
        Assert.True(fixtureNames.Count(name => name.StartsWith("rss-", StringComparison.Ordinal)) >= 11);
        Assert.True(fixtureNames.Count(name => name.StartsWith("atom-", StringComparison.Ordinal)) >= 9);
        Assert.Contains("rss-chinese.xml", fixtureNames);
        Assert.Contains("atom-chinese.xml", fixtureNames);
        Assert.Contains("rss-iso-8859-1.xml", fixtureNames);
        Assert.Contains("rss-utf16le-template.xml", fixtureNames);
        Assert.Contains("atom-utf16be-template.xml", fixtureNames);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void EveryCorpusFixtureParsesIntoSafeEntries(string fixtureName)
    {
        var parser = new FeedDocumentParser();

        ParsedFeedDocument document = parser.Parse(
            FeedId,
            "https://fixtures.example/base/feed.xml",
            ReadFixture(fixtureName),
            FetchedAt);

        FeedDocumentKind expectedKind = fixtureName.StartsWith("rss-", StringComparison.Ordinal)
            ? FeedDocumentKind.Rss20
            : FeedDocumentKind.Atom;
        Assert.Equal(expectedKind, document.Kind);
        Assert.NotEmpty(document.Title);
        Assert.NotEmpty(document.Entries);
        Assert.All(document.Entries, entry =>
        {
            Assert.NotEmpty(entry.Id);
            Assert.NotEmpty(entry.ExternalId);
            Assert.NotEmpty(entry.Title);
            Assert.NotEmpty(entry.ContentHash);
            Assert.DoesNotContain("<script", entry.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<script", entry.SanitizedContent, StringComparison.OrdinalIgnoreCase);
        });

        switch (fixtureName)
        {
            case "rss-duplicate-guid.xml":
                Assert.Single(document.Entries);
                break;
            case "rss-signed-query.xml":
                Assert.Contains("utm_source=rss", document.Entries[0].NormalizedUrl, StringComparison.Ordinal);
                Assert.Contains("X-Amz-Signature=abc", document.Entries[0].NormalizedUrl, StringComparison.Ordinal);
                break;
            case "rss-iso-8859-1.xml":
                Assert.Equal("Caf\u00E9 RSS", document.Title);
                Assert.Equal("Cr\u00E8me br\u00FBl\u00E9e", document.Entries[0].Title);
                break;
            case "rss-utf16le-template.xml":
                Assert.StartsWith("UTF-16 LE", document.Title, StringComparison.Ordinal);
                break;
            case "atom-utf16be-template.xml":
                Assert.StartsWith("UTF-16 BE", document.Title, StringComparison.Ordinal);
                break;
        }
    }

    private static string[] GetFixturePaths()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Feeds");
        return Directory.GetFiles(directory, "*.xml", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static byte[] ReadFixture(string fixtureName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Feeds", fixtureName);
        if (fixtureName == "rss-utf16le-template.xml")
        {
            return EncodeWithPreamble(File.ReadAllText(path, Encoding.UTF8), Encoding.Unicode);
        }

        if (fixtureName == "atom-utf16be-template.xml")
        {
            return EncodeWithPreamble(File.ReadAllText(path, Encoding.UTF8), Encoding.BigEndianUnicode);
        }

        return File.ReadAllBytes(path);
    }

    private static byte[] EncodeWithPreamble(string text, Encoding encoding) =>
        [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
}
