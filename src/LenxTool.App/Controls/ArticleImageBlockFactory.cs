using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LenxTool.App.Controls;

internal static class ArticleImageBlockFactory
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly ArticleImageDownloader Downloader = new(HttpClient);
    private static readonly SemaphoreSlim DownloadSlots = new(4, 4);

    public static FrameworkElement Create(
        string imageUrl,
        string altText,
        string referrer,
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

        _ = LoadAsync(image, status, imageUrl, referrer, cancellationToken);
        return frame;
    }

    private static async Task LoadAsync(
        Image image,
        TextBlock status,
        string imageUrl,
        string referrer,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await DownloadAsync(imageUrl, referrer, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes, writable: false);
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
        catch (Exception exception) when (
            exception is HttpRequestException
                or InvalidDataException
                or NotSupportedException
                or IOException
                or FormatException
                or InvalidOperationException)
        {
            status.Text = "图片暂时无法加载，可通过“查看网页原文”打开。";
        }
    }

    private static async Task<byte[]> DownloadAsync(
        string imageUrl,
        string referrer,
        CancellationToken cancellationToken)
    {
        await DownloadSlots.WaitAsync(cancellationToken);
        try
        {
            return await Downloader.DownloadAsync(imageUrl, referrer, cancellationToken);
        }
        finally
        {
            DownloadSlots.Release();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }
}
