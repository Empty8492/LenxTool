using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LenxTool.App.Controls;

public sealed class SafeHtmlView : UserControl, IDisposable
{
    public static readonly DependencyProperty HtmlProperty = DependencyProperty.Register(
        nameof(Html),
        typeof(string),
        typeof(SafeHtmlView),
        new PropertyMetadata(string.Empty, OnHtmlChanged));

    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.Register(
        nameof(FallbackText),
        typeof(string),
        typeof(SafeHtmlView),
        new PropertyMetadata(string.Empty, OnFallbackTextChanged));

    private readonly WebView2 _webView = new();
    private readonly Border _fallback;
    private readonly TextBlock _fallbackText;
    private bool _ready;
    private bool _disposed;

    public SafeHtmlView()
    {
        _fallbackText = new TextBlock
        {
            Margin = new(24),
            TextWrapping = TextWrapping.Wrap
        };
        _fallbackText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        _fallback = new Border
        {
            Child = new ScrollViewer
            {
                Content = _fallbackText,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
        _fallback.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        var layout = new Grid();
        layout.Children.Add(_webView);
        layout.Children.Add(_fallback);
        Content = layout;
        Loaded += OnLoaded;
    }

    public string Html
    {
        get => (string)GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    public string FallbackText
    {
        get => (string)GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Loaded -= OnLoaded;
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }
        _webView.Dispose();
        _disposed = true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready) return;
        try
        {
            await _webView.EnsureCoreWebView2Async().ConfigureAwait(true);
            CoreWebView2Settings settings = _webView.CoreWebView2.Settings;
            settings.IsScriptEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _ready = true;
            Navigate();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _webView.Visibility = Visibility.Collapsed;
            _fallbackText.Text = "未检测到 WebView2 Runtime。以下为安全纯文本内容。\n\n" + FallbackText;
        }
    }

    private static void OnHtmlChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is SafeHtmlView view && view._ready) view.Navigate();
    }

    private static void OnFallbackTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is SafeHtmlView view)
        {
            view._fallbackText.Text = e.NewValue as string ?? string.Empty;
        }
    }

    private void Navigate()
    {
        _fallback.Visibility = Visibility.Visible;
        _webView.CoreWebView2?.NavigateToString(Html ?? string.Empty);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        try
        {
            string lengthJson = await _webView.CoreWebView2
                .ExecuteScriptAsync("document.body?.innerText?.trim().length ?? 0")
                .ConfigureAwait(true);
            if (int.TryParse(lengthJson, out int length) && length > 0)
            {
                _fallback.Visibility = Visibility.Collapsed;
            }
        }
        catch (InvalidOperationException)
        {
            // Keep the native fallback visible when WebView2 cannot expose rendered text.
        }
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
    }
}
