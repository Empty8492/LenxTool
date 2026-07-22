using System.Text;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.Infrastructure.Tests.SystemServices;

public sealed class OpmlCodecTests
{
    private readonly OpmlCodec _codec = new();

    [Fact]
    public async Task ParsePreservesNestedGroupsChineseTitlesAndFeedAttributes()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <opml version="2.0">
              <head><title>我的订阅</title></head>
              <body>
                <outline text="技术">
                  <outline title="开发">
                    <outline text="示例源" type="rss" xmlUrl="https://example.com/feed.xml" htmlUrl="https://example.com/" />
                  </outline>
                </outline>
                <outline text="未分组" xmlUrl="not a valid url" />
              </body>
            </opml>
            """;

        OpmlDocument document = await _codec.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)),
            CancellationToken.None);

        Assert.Equal("我的订阅", document.Title);
        Assert.Collection(
            document.Feeds,
            feed =>
            {
                Assert.Equal("示例源", feed.Title);
                Assert.Equal("https://example.com/feed.xml", feed.XmlUrl);
                Assert.Equal("https://example.com/", feed.HtmlUrl);
                Assert.Equal(["技术", "开发"], feed.GroupPath);
            },
            feed =>
            {
                Assert.Equal("未分组", feed.Title);
                Assert.Equal("not a valid url", feed.XmlUrl);
                Assert.Empty(feed.GroupPath);
            });
    }

    [Theory]
    [InlineData("<opml><body><outline text='broken'></body></opml>")]
    [InlineData("<!DOCTYPE opml [<!ENTITY xxe SYSTEM 'file:///c:/windows/win.ini'>]><opml><body><outline text='&xxe;' xmlUrl='https://example.com/feed.xml'/></body></opml>")]
    public async Task ParseRejectsMalformedXmlAndDocumentTypes(string xml)
    {
        AppException error = await Assert.ThrowsAsync<AppException>(() => _codec.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)),
            CancellationToken.None));

        Assert.Equal(AppErrorCode.InvalidRequest, error.Error.Code);
        Assert.DoesNotContain("win.ini", error.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseRejectsFilesAboveTheByteLimit()
    {
        byte[] oversized = new byte[(2 * 1024 * 1024) + 1];

        AppException error = await Assert.ThrowsAsync<AppException>(() => _codec.ParseAsync(
            new MemoryStream(oversized),
            CancellationToken.None));

        Assert.Equal(AppErrorCode.InvalidRequest, error.Error.Code);
        Assert.Contains("2 MiB", error.Error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteProducesEscapedNestedOpmlWithoutPrivateFields()
    {
        var document = new OpmlDocument(
            "共享目录",
            [
                new("A & B", "https://example.com/feed.xml?a=1&b=2", "https://example.com/", ["技术", "开发"]),
                new("独立", "https://other.example/atom.xml", null, [])
            ]);
        await using var output = new MemoryStream();

        await _codec.WriteAsync(output, document, CancellationToken.None);
        string xml = Encoding.UTF8.GetString(output.ToArray());
        output.Position = 0;
        OpmlDocument roundTrip = await _codec.ParseAsync(output, CancellationToken.None);

        Assert.Contains("&amp;", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("password", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh", xml, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(document.Title, roundTrip.Title);
        Assert.Collection(
            roundTrip.Feeds,
            feed =>
            {
                Assert.Equal(document.Feeds[0].Title, feed.Title);
                Assert.Equal(document.Feeds[0].XmlUrl, feed.XmlUrl);
                Assert.Equal(document.Feeds[0].HtmlUrl, feed.HtmlUrl);
                Assert.Equal(document.Feeds[0].GroupPath, feed.GroupPath);
            },
            feed =>
            {
                Assert.Equal(document.Feeds[1].Title, feed.Title);
                Assert.Equal(document.Feeds[1].XmlUrl, feed.XmlUrl);
                Assert.Null(feed.HtmlUrl);
                Assert.Empty(feed.GroupPath);
            });
    }

    [Fact]
    public async Task ParseAcceptsUtf16WithBom()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?><opml version=\"2.0\"><head><title>中文</title></head><body><outline text=\"源\" xmlUrl=\"https://example.com/feed.xml\" /></body></opml>";
        byte[] preamble = Encoding.Unicode.GetPreamble();
        byte[] content = Encoding.Unicode.GetBytes(xml);
        byte[] bytes = [.. preamble, .. content];

        OpmlDocument document = await _codec.ParseAsync(new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal("中文", document.Title);
        Assert.Equal("源", Assert.Single(document.Feeds).Title);
    }
}
