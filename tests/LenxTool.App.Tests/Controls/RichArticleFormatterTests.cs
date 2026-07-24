using LenxTool.App.Controls;

namespace LenxTool.App.Tests.Controls;

public sealed class RichArticleFormatterTests
{
    [Fact]
    public void ParseExtractsHeroHeadingsBulletsAndLinks()
    {
        const string html = """
            <article>
              <img src="https://daily.example/cover.png" alt="AI 早报封面">
              <h2>概览</h2><h3>要闻</h3>
              <ul><li><a href="https://example.com/story">Qwen{3.8|三点八} 发布</a> #1</li></ul>
            </article>
            """;

        RichArticleDocument document = RichArticleFormatter.Parse(html);

        Assert.Equal("https://daily.example/cover.png", document.HeroImageUrl);
        Assert.Contains(document.Blocks, block => block.Kind == RichArticleBlockKind.Heading && block.Text == "概览");
        Assert.Contains(document.Blocks, block => block.Kind == RichArticleBlockKind.Subheading && block.Text == "要闻");
        RichArticleBlock bullet = Assert.Single(document.Blocks, block => block.Kind == RichArticleBlockKind.Bullet);
        Assert.Contains(bullet.Inlines, inline => inline.Text == "Qwen3.8 发布" && inline.Url == "https://example.com/story");
    }

    [Fact]
    public void ParseOmitsVideoEditionPromotionLine()
    {
        const string html = """
            <article>
              <h1>AI 早报 2026-07-24</h1>
              <p>视频版：<a href="https://bilibili.example/video">哔哩哔哩</a> | <a href="https://youtube.example/video">YouTube</a></p>
              <h2>概览</h2>
              <p>正常正文</p>
            </article>
            """;

        RichArticleDocument document = RichArticleFormatter.Parse(html);

        Assert.DoesNotContain(
            document.Blocks,
            block => block.Text.StartsWith("视频版", StringComparison.Ordinal));
        Assert.Contains(document.Blocks, block => block.Text == "正常正文");
    }

    [Fact]
    public void ParsePreservesHtmlAndMarkdownImagesInDocumentOrder()
    {
        const string content = """
            <p>第一段</p>
            <img src="data:image/gif;base64,placeholder" data-src="/images/benchmark.png" alt="模型评测表">
            <p>第二段</p>
            ![趋势图](https://cdn.example/trend.png)
            ![相对路径图](../images/relative.png)
            <img src="javascript:alert(1)" alt="不安全图片">
            """;

        RichArticleDocument document = RichArticleFormatter.Parse(content, "https://daily.example/posts/42");

        RichArticleBlock[] visibleBlocks = document.Blocks
            .Where(block => block.Kind is RichArticleBlockKind.Body or RichArticleBlockKind.Image)
            .ToArray();

        Assert.Collection(
            visibleBlocks,
            block => Assert.Equal("第一段", block.Text),
            block =>
            {
                Assert.Equal(RichArticleBlockKind.Image, block.Kind);
                Assert.Equal("模型评测表", block.Text);
                Assert.Equal("https://daily.example/images/benchmark.png", block.ImageUrl);
            },
            block => Assert.Equal("第二段", block.Text),
            block =>
            {
                Assert.Equal(RichArticleBlockKind.Image, block.Kind);
                Assert.Equal("趋势图", block.Text);
                Assert.Equal("https://cdn.example/trend.png", block.ImageUrl);
            },
            block =>
            {
                Assert.Equal(RichArticleBlockKind.Image, block.Kind);
                Assert.Equal("相对路径图", block.Text);
                Assert.Equal("https://daily.example/images/relative.png", block.ImageUrl);
            });
    }
}
