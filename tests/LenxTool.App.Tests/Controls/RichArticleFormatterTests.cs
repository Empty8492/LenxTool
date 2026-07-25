using LenxTool.App.Controls;
using LenxTool.Core.Models;

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

    [Fact]
    public void FromExtractedContentPreservesStructuredOrderAndSafeLinks()
    {
        ArticleContentResult article = new(
            "https://example.com/original",
            "https://example.com/posts/reader",
            "Reader title",
            "Author",
            new DateTimeOffset(2026, 7, 25, 8, 30, 0, TimeSpan.Zero),
            [
                new(
                    ArticleContentBlockKind.Heading,
                    "Overview",
                    null,
                    1,
                    []),
                new(
                    ArticleContentBlockKind.Paragraph,
                    "Read the safe source and ignore the unsafe source and credential source.",
                    null,
                    null,
                    [
                        new("https://source.example/story", "safe source"),
                        new("javascript:alert(1)", "unsafe source"),
                        new("https://user:password@source.example/story", "credential source")
                    ]),
                new(
                    ArticleContentBlockKind.Quote,
                    "Quoted text",
                    null,
                    null,
                    []),
                new(
                    ArticleContentBlockKind.ListItem,
                    "List item",
                    null,
                    null,
                    []),
                new(
                    ArticleContentBlockKind.Image,
                    "Diagram",
                    "/images/diagram.png",
                    null,
                    [])
            ],
            [],
            "readability-v1");

        RichArticleDocument document = RichArticleFormatter.FromExtractedContent(article);

        Assert.Equal("https://example.com/images/diagram.png", document.HeroImageUrl);
        Assert.Collection(
            document.Blocks,
            block => Assert.Equal((RichArticleBlockKind.Heading, "Overview"), (block.Kind, block.Text)),
            block =>
            {
                Assert.Equal(RichArticleBlockKind.Body, block.Kind);
                Assert.Contains(
                    block.Inlines,
                    inline => inline is { Text: "safe source", Url: "https://source.example/story" });
                Assert.Contains(
                    block.Inlines,
                    inline => inline is { Text: "unsafe source", Url: null });
                Assert.Contains(
                    block.Inlines,
                    inline => inline is { Text: "credential source", Url: null });
            },
            block => Assert.Equal((RichArticleBlockKind.Quote, "Quoted text"), (block.Kind, block.Text)),
            block => Assert.Equal((RichArticleBlockKind.Bullet, "List item"), (block.Kind, block.Text)),
            block =>
            {
                Assert.Equal(RichArticleBlockKind.Image, block.Kind);
                Assert.Equal("Diagram", block.Text);
                Assert.Equal("https://example.com/images/diagram.png", block.ImageUrl);
            });
    }

    [Fact]
    public void ExtractedContentCleansReaderMarkersAndKeepsSafeEnclosuresInOrder()
    {
        ArticleContentResult article = new(
            "https://example.com/original",
            "https://example.com/posts/reader",
            null,
            null,
            null,
            [
                new(
                    ArticleContentBlockKind.Paragraph,
                    "Qwen{3.8|三点八} released",
                    null,
                    null,
                    []),
                new(
                    ArticleContentBlockKind.Paragraph,
                    "视频版：YouTube",
                    null,
                    null,
                    [])
            ],
            [],
            "readability-v1");
        FeedEnclosure[] enclosures =
        [
            new("/media/audio.mp3", "audio/mpeg", 100, "Audio"),
            new("javascript:alert(1)", "text/html", null, "Unsafe"),
            new("https://cdn.example/video.mp4", "video/mp4", 200, "Video")
        ];

        RichArticleDocument document = RichArticleFormatter.WithEnclosures(
            RichArticleFormatter.FromExtractedContent(article),
            enclosures,
            article.FinalUrl);

        Assert.Contains(document.Blocks, block => block.Text == "Qwen3.8 released");
        Assert.DoesNotContain(document.Blocks, block => block.Text.StartsWith("视频版", StringComparison.Ordinal));
        RichArticleBlock[] attachmentBlocks = document.Blocks
            .SkipWhile(block => block.Text != "附件")
            .ToArray();
        Assert.Collection(
            attachmentBlocks,
            block => Assert.Equal((RichArticleBlockKind.Subheading, "附件"), (block.Kind, block.Text)),
            block => Assert.Contains(
                block.Inlines,
                inline => inline is
                {
                    Text: "Audio",
                    Url: "https://example.com/media/audio.mp3"
                }),
            block => Assert.Contains(
                block.Inlines,
                inline => inline is
                {
                    Text: "Video",
                    Url: "https://cdn.example/video.mp4"
                }));
    }

    [Fact]
    public void TranslationSourceAddsTitleAndSnapshotsTextBlocksWithoutImages()
    {
        var source = new RichArticleDocument(
            "https://example.com/cover.png",
            [
                new(
                    RichArticleBlockKind.Body,
                    [
                        new RichArticleInline("Read "),
                        new RichArticleInline("safe source", "https://example.com/story")
                    ]),
                new(
                    RichArticleBlockKind.Image,
                    [new RichArticleInline("Diagram")],
                    "https://example.com/diagram.png"),
                new(
                    RichArticleBlockKind.Quote,
                    [new RichArticleInline("Quoted text")])
            ]);

        RichArticleTranslationSource translationSource =
            RichArticleFormatter.CreateTranslationSource(source, "Article title");

        Assert.Equal(
            ["Article title", "Read safe source", "Diagram", "Quoted text"],
            translationSource.Document.Blocks.Select(block => block.Text));
        Assert.Collection(
            translationSource.Blocks,
            block =>
            {
                Assert.Equal((0, FeedAiTranslationBlockKind.Title, "Article title"),
                    (block.Sequence, block.Kind, block.Text));
                Assert.Empty(block.Links);
            },
            block =>
            {
                Assert.Equal((1, FeedAiTranslationBlockKind.Paragraph, "Read safe source"),
                    (block.Sequence, block.Kind, block.Text));
                ArticleContentLink link = Assert.Single(block.Links);
                Assert.Equal(("safe source", "https://example.com/story"), (link.Text, link.Url));
            },
            block =>
            {
                Assert.Equal((3, FeedAiTranslationBlockKind.Quote, "Quoted text"),
                    (block.Sequence, block.Kind, block.Text));
                Assert.Empty(block.Links);
            });
    }

    [Fact]
    public void ApplyTranslationPreservesImageOrderAndUsesOnlyOriginalLinkTargets()
    {
        var source = new RichArticleDocument(
            "https://example.com/cover.png",
            [
                new(
                    RichArticleBlockKind.Body,
                    [
                        new RichArticleInline("Read "),
                        new RichArticleInline("safe source", "https://example.com/story")
                    ]),
                new(
                    RichArticleBlockKind.Image,
                    [new RichArticleInline("Diagram")],
                    "https://example.com/diagram.png")
            ]);
        RichArticleTranslationSource translationSource =
            RichArticleFormatter.CreateTranslationSource(source, "Article title");
        DateTimeOffset now = new(2026, 7, 25, 4, 0, 0, TimeSpan.Zero);
        var key = new FeedAiCacheKey(
            "entry-1",
            new string('a', 64),
            FeedAiTaskType.Translation,
            "简体中文",
            FeedAiTranslationOptions.Default.Model,
            FeedAiTranslationOptions.Default.PromptVersion);
        var cache = new FeedAiResult(
            "translation-1",
            key,
            "Article title",
            "{}",
            1,
            10,
            5,
            15,
            100,
            null,
            now,
            now);
        var result = new FeedAiTranslationResult(
            cache,
            [
                new(
                    0,
                    FeedAiTranslationBlockKind.Title,
                    "Article title",
                    "文章标题",
                    null,
                    1,
                    []),
                new(
                    1,
                    FeedAiTranslationBlockKind.Paragraph,
                    "Read safe source",
                    "<script>模型文本</script> [恶意](javascript:alert(1))",
                    null,
                    null,
                    [new("https://example.com/story", "safe source")])
            ]);

        RichArticleDocument translated = RichArticleFormatter.ApplyTranslation(
            translationSource,
            result,
            bilingual: false);
        RichArticleDocument bilingual = RichArticleFormatter.ApplyTranslation(
            translationSource,
            result,
            bilingual: true);

        Assert.Equal(
            ["文章标题", "<script>模型文本</script> [恶意](javascript:alert(1))\n原文链接：safe source", "Diagram"],
            translated.Blocks.Select(block => block.Text));
        RichArticleInline translatedLink = Assert.Single(
            translated.Blocks[1].Inlines,
            inline => inline.Url is not null);
        Assert.Equal(("safe source", "https://example.com/story"),
            (translatedLink.Text, translatedLink.Url));
        Assert.DoesNotContain(
            translated.Blocks.SelectMany(block => block.Inlines),
            inline => inline.Url?.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) == true);

        Assert.Equal(
            [
                RichArticleBlockKind.Heading,
                RichArticleBlockKind.Translation,
                RichArticleBlockKind.Body,
                RichArticleBlockKind.Translation,
                RichArticleBlockKind.Image
            ],
            bilingual.Blocks.Select(block => block.Kind));
        Assert.Single(bilingual.Blocks, block => block.Kind == RichArticleBlockKind.Image);
    }

    [Fact]
    public void TranslationSourceSplitsLongParagraphWithoutLosingTextOrLinkTarget()
    {
        string prefix = new('a', FeedAiTranslationOptions.Default.MaximumBlockCharacters - 20);
        string linkedText = new('b', 80);
        string suffix = new('c', 120);
        var document = new RichArticleDocument(
            null,
            [
                new(
                    RichArticleBlockKind.Body,
                    [
                        new RichArticleInline(prefix),
                        new RichArticleInline(linkedText, "https://example.com/long-story"),
                        new RichArticleInline(suffix)
                    ])
            ]);

        RichArticleTranslationSource source =
            RichArticleFormatter.CreateTranslationSource(document, "Long article");

        FeedAiTranslationBlock[] bodyBlocks = source.Blocks
            .Where(block => block.Kind == FeedAiTranslationBlockKind.Paragraph)
            .ToArray();
        Assert.True(bodyBlocks.Length > 1);
        Assert.All(
            bodyBlocks,
            block => Assert.InRange(
                block.Text.Length,
                1,
                FeedAiTranslationOptions.Default.MaximumBlockCharacters));
        Assert.Equal(prefix + linkedText + suffix, string.Concat(bodyBlocks.Select(block => block.Text)));
        Assert.Contains(
            bodyBlocks.SelectMany(block => block.Links),
            link => link.Url == "https://example.com/long-story"
                && linkedText.Contains(link.Text, StringComparison.Ordinal));
    }
}
