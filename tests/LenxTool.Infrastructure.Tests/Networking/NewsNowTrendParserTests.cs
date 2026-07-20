using System.Globalization;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class NewsNowTrendParserTests
{
    [Fact]
    public void DefaultCatalogIncludesTrendRadarPlatformsAndExistingTechSources()
    {
        string[] ids = TrendSourceCatalog.Default.Select(source => source.Id).ToArray();

        Assert.Equal(13, ids.Length);
        Assert.Contains("zhihu", ids);
        Assert.Contains("douyin", ids);
        Assert.Contains("bilibili-hot-search", ids);
        Assert.Contains("wallstreetcn-hot", ids);
        Assert.Contains("tieba", ids);
        Assert.Contains("baidu", ids);
        Assert.Contains("cls-hot", ids);
        Assert.Contains("thepaper", ids);
        Assert.Contains("ifeng", ids);
        Assert.Contains("toutiao", ids);
        Assert.Contains("weibo", ids);
        Assert.Contains("github-trending-today", ids);
        Assert.Contains("hackernews", ids);
    }

    [Fact]
    public void ParseCreatesRankedItemsAndUsesSourceDomainPolicy()
    {
        const string json = """
            {
              "status": "success",
              "items": [
                { "id": "a", "title": "第一条热点", "url": "https://www.zhihu.com/question/1", "extra": { "info": "521 万热度" } },
                { "id": "b", "title": "第二条热点", "url": "https://zhihu.com/question/2" }
              ]
            }
            """;
        TrendSourceDefinition source = TrendSourceCatalog.Default.Single(item => item.Id == "zhihu");
        DateTimeOffset capturedAt = DateTimeOffset.Parse(
            "2026-07-21T09:00:00+08:00",
            CultureInfo.InvariantCulture);

        IReadOnlyList<TrendItem> items = NewsNowTrendParser.Parse(json, source, capturedAt, 10);

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal(1, item.Rank);
                Assert.Equal("521 万热度", item.Heat);
                Assert.Equal("第一条热点", item.Title);
            },
            item =>
            {
                Assert.Equal(2, item.Rank);
                Assert.Empty(item.Heat);
            });
    }

    [Fact]
    public void ParseRejectsUnexpectedOrInsecureDomains()
    {
        const string json = """
            { "status": "cache", "items": [
              { "title": "劫持链接", "url": "https://zhihu.com.evil.example/item" }
            ] }
            """;
        TrendSourceDefinition source = TrendSourceCatalog.Default.Single(item => item.Id == "zhihu");

        Assert.Throws<InvalidDataException>(() =>
            NewsNowTrendParser.Parse(json, source, DateTimeOffset.UtcNow, 10));
    }
}
