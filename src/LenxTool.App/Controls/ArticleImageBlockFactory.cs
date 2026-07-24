using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Controls;

internal static class ArticleImageBlockFactory
{
    private static IArticleImageDownloader? _downloader;

    internal static void Configure(IArticleImageDownloader downloader)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        Volatile.Write(ref _downloader, downloader);
    }

    public static FrameworkElement Create(
        string entryId,
        string imageUrl,
        string altText,
        string referrer,
        ArticleImageDownloadBudget budget,
        CancellationToken cancellationToken)
    {
        var image = new Image
        {
            MaxHeight = 520,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(altText) ? "资讯配图" : altText);

        var status = new TextBlock
        {
            Padding = new(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "正在加载图片…"
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        var imageHost = new Grid { MinHeight = 160 };
        imageHost.Children.Add(status);
        imageHost.Children.Add(image);
        var frame = new Border
        {
            Margin = new(0, 8, 0, 22),
            Padding = new(4),
            CornerRadius = new(10),
            ClipToBounds = true,
            BorderThickness = new(1),
            Child = imageHost
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceRaised");
        frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        IArticleImageDownloader? downloader = Volatile.Read(ref _downloader);
        if (downloader is null)
        {
            status.Text = "图片缓存服务尚未就绪，可通过“查看网页原文”打开。";
            return frame;
        }

        _ = LoadAsync(
            downloader,
            image,
            status,
            entryId,
            imageUrl,
            referrer,
            budget,
            cancellationToken);
        return frame;
    }

    private static async Task LoadAsync(
        IArticleImageDownloader downloader,
        Image image,
        TextBlock status,
        string entryId,
        string imageUrl,
        string referrer,
        ArticleImageDownloadBudget budget,
        CancellationToken cancellationToken)
    {
        try
        {
            ArticleImageContent? content = await downloader.GetAsync(
                entryId,
                imageUrl,
                referrer,
                budget,
                cancellationToken);
            if (content is null)
            {
                status.Text = "图片暂时无法加载，可通过“查看网页原文”打开。";
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(content.Bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 1600;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            image.Source = bitmap;
            image.Visibility = Visibility.Visible;
            status.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            status.Text = "图片加载超时，可通过“查看网页原文”打开。";
        }
        catch (Exception)
        {
            status.Text = "图片暂时无法加载，可通过“查看网页原文”打开。";
        }
    }
}
