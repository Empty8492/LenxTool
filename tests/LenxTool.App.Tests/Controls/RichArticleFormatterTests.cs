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
}
