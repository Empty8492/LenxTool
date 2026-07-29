using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using LenxTool.Core.Models;

namespace LenxTool.App.Controls;

public sealed class RichArticleView : UserControl, IDisposable
{
    private const int MaximumImagesPerArticle = 24;
    private const long MaximumImageNetworkBytesPerArticle = 48L * 1024 * 1024;

    public static readonly DependencyProperty ArticleProperty = DependencyProperty.Register(
        nameof(Article),
        typeof(NewsArticle),
        typeof(RichArticleView),
        new PropertyMetadata(null, OnArticleChanged));

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document),
        typeof(RichArticleDocument),
        typeof(RichArticleView),
        new PropertyMetadata(null, OnArticleChanged));

    public static readonly DependencyProperty ContentSourceLabelProperty = DependencyProperty.Register(
        nameof(ContentSourceLabel),
        typeof(string),
        typeof(RichArticleView),
        new PropertyMetadata(string.Empty, OnArticleChanged));

    public static readonly DependencyProperty ExtractedAtProperty = DependencyProperty.Register(
        nameof(ExtractedAt),
        typeof(DateTimeOffset?),
        typeof(RichArticleView),
        new PropertyMetadata(null, OnArticleChanged));

    private readonly StackPanel _contentPanel;
    private CancellationTokenSource? _imageLoadCancellation;

    public RichArticleView()
    {
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 15;
        SetResourceReference(ForegroundProperty, "Brush.TextPrimary");

        _contentPanel = new StackPanel();
        Content = new Border
        {
            Padding = new Thickness(28, 18, 28, 36),
            Child = _contentPanel
        };

        Loaded += (_, _) =>
        {
            if (_imageLoadCancellation is null) Rebuild();
        };
        Unloaded += (_, _) => CancelImageLoads();
        Rebuild();
    }

    public NewsArticle? Article
    {
        get => (NewsArticle?)GetValue(ArticleProperty);
        set => SetValue(ArticleProperty, value);
    }

    public RichArticleDocument? Document
    {
        get => (RichArticleDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public string ContentSourceLabel
    {
        get => (string)GetValue(ContentSourceLabelProperty);
        set => SetValue(ContentSourceLabelProperty, value);
    }

    public DateTimeOffset? ExtractedAt
    {
        get => (DateTimeOffset?)GetValue(ExtractedAtProperty);
        set => SetValue(ExtractedAtProperty, value);
    }

    public void Dispose()
    {
        CancelImageLoads();
        GC.SuppressFinalize(this);
    }

    private static void OnArticleChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is RichArticleView view) view.Rebuild();
    }

    private void Rebuild()
    {
        CancelImageLoads();
        _imageLoadCancellation = new CancellationTokenSource();
        CancellationToken imageLoadToken =
            _imageLoadCancellation.Token;
        _contentPanel.Children.Clear();

        NewsArticle? article = Article;
        if (article is null)
        {
            AddDeferredBlock(
                new RichArticleBlock(
                    RichArticleBlockKind.Body,
                    [new("当天暂无早报。")]),
                CreateTextBlock);
            return;
        }

        RichArticleDocument content = Document ?? RichArticleFormatter.Parse(
            string.IsNullOrWhiteSpace(article.RichContent) ? article.Content : article.RichContent,
            article.Url);
        var imageBudget = new ArticleImageDownloadBudget(
            MaximumImagesPerArticle,
            MaximumImageNetworkBytesPerArticle);
        if (content.Blocks.Count == 0 || !content.Blocks.Any(block => block.Kind == RichArticleBlockKind.Heading))
        {
            AddDeferredBlock(
                new RichArticleBlock(
                    RichArticleBlockKind.Heading,
                    [new(article.Title)]),
                CreateTextBlock);
        }

        AddDeferred(
            estimatedHeight: 48d,
            () => CreateMetaBlock(article));

        foreach (RichArticleBlock block in content.Blocks)
        {
            RichArticleBlock currentBlock = block;
            if (currentBlock.Kind == RichArticleBlockKind.Image
                && currentBlock.ImageUrl is not null)
            {
                AddDeferredImage(
                    article,
                    currentBlock,
                    imageBudget,
                    imageLoadToken);
            }
            else
            {
                AddDeferredBlock(currentBlock, CreateTextBlock);
            }
        }
    }

    private TextBlock CreateMetaBlock(NewsArticle article)
    {
        var meta = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 18),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        meta.Inlines.Add(new Run($"{article.Source}  ·  {article.PublishedDate:yyyy-MM-dd}"));
        if (!string.IsNullOrWhiteSpace(ContentSourceLabel))
        {
            meta.Inlines.Add(new Run($"  ·  内容来源：{ContentSourceLabel}"));
        }
        if (ExtractedAt is { } extractedAt)
        {
            meta.Inlines.Add(new Run(
                $"  ·  提取于 {extractedAt.ToLocalTime():yyyy-MM-dd HH:mm}"));
        }
        return meta;
    }

    private void AddDeferredBlock(
        RichArticleBlock block,
        Func<RichArticleBlock, TextBlock> factory)
    {
        AddDeferred(
            EstimateBlockHeight(block),
            () => factory(block));
    }

    private void AddDeferred(
        double estimatedHeight,
        Func<FrameworkElement> factory)
    {
        _contentPanel.Children.Add(
            new ViewportDeferredContentControl
            {
                EstimatedHeight = estimatedHeight,
                PreloadViewportCount = 1d,
                ContentFactory = factory
            });
    }

    private void AddDeferredImage(
        NewsArticle article,
        RichArticleBlock block,
        ArticleImageDownloadBudget imageBudget,
        CancellationToken articleToken)
    {
        CancellationTokenSource? blockCancellation = null;
        var deferred = new ViewportDeferredContentControl
        {
            EstimatedHeight = 210d,
            PreloadViewportCount = 1d
        };
        deferred.ContentFactory = () =>
        {
            blockCancellation?.Dispose();
            blockCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(articleToken);
            return ArticleImageBlockFactory.Create(
                article.Id,
                block.ImageUrl!,
                block.Text,
                article.Url,
                imageBudget,
                blockCancellation.Token);
        };
        deferred.ContentReleased = () =>
        {
            blockCancellation?.Cancel();
            blockCancellation?.Dispose();
            blockCancellation = null;
        };
        _contentPanel.Children.Add(deferred);
    }

    private static double EstimateBlockHeight(RichArticleBlock block)
    {
        if (block.Kind == RichArticleBlockKind.Image) return 210d;
        double lineCount = Math.Max(
            1d,
            Math.Ceiling(block.Text.Length / 52d));
        double textHeight = lineCount * 25d;
        return block.Kind switch
        {
            RichArticleBlockKind.Heading => Math.Max(70d, textHeight + 36d),
            RichArticleBlockKind.Subheading => Math.Max(50d, textHeight + 26d),
            RichArticleBlockKind.Translation => textHeight + 28d,
            _ => textHeight + 20d
        };
    }

    private void CancelImageLoads()
    {
        _imageLoadCancellation?.Cancel();
        _imageLoadCancellation?.Dispose();
        _imageLoadCancellation = null;
    }

    private static TextBlock CreateTextBlock(RichArticleBlock block)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 25
        };
        switch (block.Kind)
        {
            case RichArticleBlockKind.Heading:
                textBlock.FontSize = 27;
                textBlock.FontWeight = FontWeights.SemiBold;
                textBlock.LineHeight = 36;
                textBlock.Margin = new Thickness(0, 10, 0, 14);
                break;
            case RichArticleBlockKind.Subheading:
                textBlock.FontSize = 19;
                textBlock.FontWeight = FontWeights.SemiBold;
                textBlock.Margin = new Thickness(0, 18, 0, 8);
                break;
            case RichArticleBlockKind.Bullet:
                textBlock.Margin = new Thickness(20, 5, 0, 5);
                textBlock.Inlines.Add(new Run("• "));
                break;
            case RichArticleBlockKind.Quote:
                textBlock.Margin = new Thickness(18, 8, 8, 12);
                textBlock.FontStyle = FontStyles.Italic;
                textBlock.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "Brush.TextSecondary");
                textBlock.Inlines.Add(new Run("“"));
                break;
            case RichArticleBlockKind.Translation:
                textBlock.Margin = new Thickness(14, 2, 0, 12);
                textBlock.Padding = new Thickness(10, 7, 10, 7);
                textBlock.FontSize = 14;
                textBlock.SetResourceReference(
                    TextBlock.BackgroundProperty,
                    "Brush.SurfaceMuted");
                textBlock.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "Brush.TextSecondary");
                textBlock.Inlines.Add(new Run("译文  ")
                {
                    FontWeight = FontWeights.SemiBold
                });
                break;
            default:
                textBlock.Margin = new Thickness(0, 5, 0, 10);
                break;
        }

        foreach (RichArticleInline inline in block.Inlines)
        {
            if (inline.Url is null) textBlock.Inlines.Add(new Run(inline.Text));
            else AddLink(textBlock.Inlines, inline.Text, inline.Url);
        }

        return textBlock;
    }

    private static void AddLink(InlineCollection inlines, string text, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            inlines.Add(new Run(text));
            return;
        }

        var hyperlink = new Hyperlink(new Run(text)) { NavigateUri = uri };
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent");
        hyperlink.RequestNavigate += OpenLink;
        inlines.Add(hyperlink);
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
