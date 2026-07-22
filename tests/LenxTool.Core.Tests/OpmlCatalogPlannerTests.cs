using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests;

public sealed class OpmlCatalogPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PreviewClassifiesNewDuplicateInvalidAndMetadataConflict()
    {
        FeedCatalogSnapshot catalog = Snapshot();
        var document = new OpmlDocument(
            "导入",
            [
                new("全新源", "https://new.example/feed.xml", "https://new.example/", ["技术", "开发"]),
                new("全新源", "https://new.example/feed.xml", "https://new.example/", ["技术", "开发"]),
                new("已有源", "https://existing.example/feed.xml", null, ["技术"]),
                new("不同标题", "https://existing.example/feed.xml", null, ["技术"]),
                new("不安全", "http://unsafe.example/feed.xml", null, [])
            ]);

        IReadOnlyList<OpmlCatalogPreviewItem> items = OpmlCatalogPlanner.CreatePreview(document, catalog);

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal(OpmlCatalogItemStatus.New, item.Status);
                Assert.True(item.IsSelected);
                Assert.Equal("技术 / 开发", item.CategoryName);
            },
            item =>
            {
                Assert.Equal(OpmlCatalogItemStatus.Duplicate, item.Status);
                Assert.False(item.IsSelected);
            },
            item => Assert.Equal(OpmlCatalogItemStatus.Duplicate, item.Status),
            item => Assert.Equal(OpmlCatalogItemStatus.Conflict, item.Status),
            item =>
            {
                Assert.Equal(OpmlCatalogItemStatus.Invalid, item.Status);
                Assert.Contains("HTTPS", item.Message, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void PreviewReusesUnicodeNormalizedCategoryAndRejectsOversizedFlattenedPath()
    {
        FeedCatalogSnapshot catalog = Snapshot() with
        {
            Categories = [Category("10000000-0000-4000-8000-000000000010", "ＡＢＣ", "abc")]
        };
        var document = new OpmlDocument(
            "导入",
            [
                new("规范化分类", "https://one.example/feed.xml", null, ["abc"]),
                new("过长分类", "https://two.example/feed.xml", null, [new string('分', 81)])
            ]);

        IReadOnlyList<OpmlCatalogPreviewItem> items = OpmlCatalogPlanner.CreatePreview(document, catalog);

        Assert.Equal("10000000-0000-4000-8000-000000000010", items[0].CategoryId);
        Assert.Equal(OpmlCatalogItemStatus.New, items[0].Status);
        Assert.Equal(OpmlCatalogItemStatus.Invalid, items[1].Status);
        Assert.False(items[1].IsSelected);
    }

    private static FeedCatalogSnapshot Snapshot() => new(
        new(5, FeedCatalogScope.All, Now, Now),
        [Category("10000000-0000-4000-8000-000000000010", "技术", "技术")],
        [new(
            "10000000-0000-4000-8000-000000000020",
            "https://existing.example/feed.xml",
            "https://existing.example/feed.xml",
            "已有源",
            null,
            "10000000-0000-4000-8000-000000000010",
            FeedViewKind.Article,
            60,
            100,
            true,
            5,
            Now,
            Now)]);

    private static FeedCategory Category(string id, string name, string normalizedName) => new(
        id, name, normalizedName, 100, true, 5, Now, Now);
}
