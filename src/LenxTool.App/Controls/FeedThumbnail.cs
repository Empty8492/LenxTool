using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Controls;

public sealed class FeedThumbnail : Border
{
    private const int DecodeWidth = 360;
    private static IArticleImageStreamDownloader? _downloader;
    private readonly Image _image;
    private readonly TextBlock _status;
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;

    public static readonly DependencyProperty EntryIdProperty =
        DependencyProperty.Register(
            nameof(EntryId),
            typeof(string),
            typeof(FeedThumbnail),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty SourceUrlProperty =
        DependencyProperty.Register(
            nameof(SourceUrl),
            typeof(string),
            typeof(FeedThumbnail),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty ReferrerProperty =
        DependencyProperty.Register(
            nameof(Referrer),
            typeof(string),
            typeof(FeedThumbnail),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty AltTextProperty =
        DependencyProperty.Register(
            nameof(AltText),
            typeof(string),
            typeof(FeedThumbnail),
            new PropertyMetadata(null, OnAltTextChanged));

    public FeedThumbnail()
    {
        MinHeight = 150;
        ClipToBounds = true;
        CornerRadius = new(8);
        SetResourceReference(BackgroundProperty, "Brush.SurfaceMuted");
        _status = new()
        {
            Padding = new(14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "暂无可用缩略图",
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        _image = new()
        {
            Stretch = Stretch.UniformToFill,
            Visibility = Visibility.Collapsed
        };
        var host = new Grid();
        host.Children.Add(_status);
        host.Children.Add(_image);
        Child = host;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateAutomationName();
    }

    public string? EntryId
    {
        get => (string?)GetValue(EntryIdProperty);
        set => SetValue(EntryIdProperty, value);
    }

    public string? SourceUrl
    {
        get => (string?)GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    public string? Referrer
    {
        get => (string?)GetValue(ReferrerProperty);
        set => SetValue(ReferrerProperty, value);
    }

    public string? AltText
    {
        get => (string?)GetValue(AltTextProperty);
        set => SetValue(AltTextProperty, value);
    }

    internal static void Configure(IArticleImageStreamDownloader downloader)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        Volatile.Write(ref _downloader, downloader);
    }

    private static void OnSourceChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        var thumbnail = (FeedThumbnail)sender;
        if (thumbnail.IsLoaded)
        {
            thumbnail.RestartLoad();
        }
    }

    private static void OnAltTextChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((FeedThumbnail)sender).UpdateAutomationName();

    private void OnLoaded(object sender, RoutedEventArgs args) => RestartLoad();

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        CancelLoad();
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
    }

    private void RestartLoad()
    {
        CancelLoad();
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        _status.Visibility = Visibility.Visible;

        IArticleImageStreamDownloader? downloader = Volatile.Read(ref _downloader);
        if (downloader is null
            || string.IsNullOrWhiteSpace(EntryId)
            || string.IsNullOrWhiteSpace(SourceUrl))
        {
            _status.Text = "暂无可用缩略图";
            return;
        }

        _status.Text = "正在读取本地缩略图缓存…";
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        int generation = Interlocked.Increment(ref _loadGeneration);
        _ = LoadAsync(downloader, generation, cancellation.Token);
    }

    private async Task LoadAsync(
        IArticleImageStreamDownloader downloader,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var budget = new ArticleImageDownloadBudget(
                maximumResources: 1,
                maximumNetworkBytes: 12L * 1024 * 1024);
            await using ArticleImageStreamContent? content = await downloader.OpenAsync(
                EntryId!,
                SourceUrl!,
                Referrer,
                budget,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(generation)) return;
            if (content is null)
            {
                _status.Text = "缩略图暂时无法加载";
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = DecodeWidth;
            bitmap.StreamSource = content.Stream;
            bitmap.EndInit();
            bitmap.Freeze();
            if (!IsCurrent(generation)) return;

            _image.Source = bitmap;
            _image.Visibility = Visibility.Visible;
            _status.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (IsCurrent(generation))
            {
                _status.Text = "缩略图暂时无法加载";
            }
        }
    }

    private bool IsCurrent(int generation) =>
        IsLoaded
        && generation == Volatile.Read(ref _loadGeneration)
        && _loadCancellation is { IsCancellationRequested: false };

    private void CancelLoad()
    {
        Interlocked.Increment(ref _loadGeneration);
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    private void UpdateAutomationName() =>
        AutomationProperties.SetName(
            this,
            string.IsNullOrWhiteSpace(AltText) ? "资讯缩略图" : AltText);
}
