using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using LenxTool.Core.Models;

namespace LenxTool.App.Controls;

public sealed class RichArticleView : UserControl
{
    public static readonly DependencyProperty ArticleProperty = DependencyProperty.Register(
        nameof(Article),
        typeof(NewsArticle),
        typeof(RichArticleView),
        new PropertyMetadata(null, OnArticleChanged));

    private readonly FlowDocumentScrollViewer _viewer;

    public RichArticleView()
    {
        _viewer = new FlowDocumentScrollViewer
        {
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _viewer.SetResourceReference(ForegroundProperty, "Brush.TextPrimary");
        Content = _viewer;
        Rebuild();
    }

    public NewsArticle? Article
    {
        get => (NewsArticle?)GetValue(ArticleProperty);
        set => SetValue(ArticleProperty, value);
    }

    private static void OnArticleChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is RichArticleView view) view.Rebuild();
    }

    private void Rebuild()
    {
        var document = new FlowDocument
        {
            PagePadding = new(28, 22, 28, 36),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = new("Segoe UI, Microsoft YaHei UI"),
            FontSize = 15,
            LineHeight = 25
        };

        NewsArticle? article = Article;
        if (article is null)
        {
            document.Blocks.Add(new Paragraph(new Run("当天暂无早报。")));
            _viewer.Document = document;
            return;
        }

        RichArticleDocument content = RichArticleFormatter.Parse(
            string.IsNullOrWhiteSpace(article.RichContent) ? article.Content : article.RichContent,
            article.Url);
        if (content.HeroImageUrl is not null) AddHero(document, content.HeroImageUrl);

        if (content.Blocks.Count == 0 || !content.Blocks.Any(block => block.Kind == RichArticleBlockKind.Heading))
        {
            document.Blocks.Add(CreateParagraph(
                new RichArticleBlock(RichArticleBlockKind.Heading, [new(article.Title)])));
        }

        var meta = new Paragraph { Margin = new(0, 2, 0, 18), FontSize = 13 };
        meta.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
        meta.Inlines.Add(new Run($"{article.Source}  ·  {article.PublishedDate:yyyy-MM-dd}  ·  "));
        AddLink(meta, "查看网页原文 ↗", article.Url);
        document.Blocks.Add(meta);

        foreach (RichArticleBlock block in content.Blocks)
        {
            document.Blocks.Add(CreateParagraph(block));
        }

        _viewer.Document = document;
    }

    private static void AddHero(FlowDocument document, string imageUrl)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new(imageUrl, UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnDemand;
        bitmap.EndInit();

        var image = new Image
        {
            Source = bitmap,
            Height = 290,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var frame = new Border
        {
            Height = 290,
            Margin = new(0, 0, 0, 24),
            CornerRadius = new(10),
            ClipToBounds = true,
            Child = image
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceMuted");
        image.ImageFailed += (_, _) => frame.Visibility = Visibility.Collapsed;
        document.Blocks.Add(new BlockUIContainer(frame));
    }

    private static Paragraph CreateParagraph(RichArticleBlock block)
    {
        var paragraph = new Paragraph();
        switch (block.Kind)
        {
            case RichArticleBlockKind.Heading:
                paragraph.FontSize = 27;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.LineHeight = 36;
                paragraph.Margin = new(0, 12, 0, 14);
                break;
            case RichArticleBlockKind.Subheading:
                paragraph.FontSize = 19;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new(0, 18, 0, 8);
                break;
            case RichArticleBlockKind.Bullet:
                paragraph.Margin = new(20, 5, 0, 5);
                paragraph.TextIndent = -16;
                paragraph.Inlines.Add(new Run("•  "));
                break;
            default:
                paragraph.Margin = new(0, 5, 0, 10);
                break;
        }

        foreach (RichArticleInline inline in block.Inlines)
        {
            if (inline.Url is null) paragraph.Inlines.Add(new Run(inline.Text));
            else AddLink(paragraph, inline.Text, inline.Url);
        }

        return paragraph;
    }

    private static void AddLink(Paragraph paragraph, string text, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            paragraph.Inlines.Add(new Run(text));
            return;
        }

        var hyperlink = new Hyperlink(new Run(text)) { NavigateUri = uri };
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent");
        hyperlink.RequestNavigate += OpenLink;
        paragraph.Inlines.Add(hyperlink);
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
