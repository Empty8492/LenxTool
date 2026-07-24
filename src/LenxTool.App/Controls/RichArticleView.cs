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
        _contentPanel.Children.Clear();

        NewsArticle? article = Article;
        if (article is null)
        {
            _contentPanel.Children.Add(CreateTextBlock(
                new RichArticleBlock(RichArticleBlockKind.Body, [new("当天暂无早报。")])));
            return;
        }

        RichArticleDocument content = RichArticleFormatter.Parse(
            string.IsNullOrWhiteSpace(article.RichContent) ? article.Content : article.RichContent,
            article.Url);
        var imageBudget = new ArticleImageDownloadBudget(
            MaximumImagesPerArticle,
            MaximumImageNetworkBytesPerArticle);
        if (content.Blocks.Count == 0 || !content.Blocks.Any(block => block.Kind == RichArticleBlockKind.Heading))
        {
            _contentPanel.Children.Add(CreateTextBlock(
                new RichArticleBlock(RichArticleBlockKind.Heading, [new(article.Title)])));
        }

        var meta = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 18),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        meta.Inlines.Add(new Run($"{article.Source}  ·  {article.PublishedDate:yyyy-MM-dd}  ·  "));
        AddLink(meta.Inlines, "查看网页原文 ↗", article.Url);
        _contentPanel.Children.Add(meta);

        foreach (RichArticleBlock block in content.Blocks)
        {
            if (block.Kind == RichArticleBlockKind.Image && block.ImageUrl is not null)
            {
                _contentPanel.Children.Add(ArticleImageBlockFactory.Create(
                    article.Id,
                    block.ImageUrl,
                    block.Text,
                    article.Url,
                    imageBudget,
                    _imageLoadCancellation.Token));
            }
            else
            {
                _contentPanel.Children.Add(CreateTextBlock(block));
            }
        }
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
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
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
