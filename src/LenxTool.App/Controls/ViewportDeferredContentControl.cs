using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LenxTool.App.Controls;

/// <summary>
/// 只在祖先滚动区的当前视口及缓冲区内创建重内容，离开缓冲区后释放视觉树。
/// </summary>
public sealed class ViewportDeferredContentControl : ContentControl
{
    private static readonly ConditionalWeakTable<
        ScrollViewer,
        ViewportCoordinator> Coordinators = new();

    private ViewportCoordinator? _coordinator;
    private double _measuredWidth;
    private double _measuredHeight;

    public static readonly DependencyProperty DeferredContentProperty =
        DependencyProperty.Register(
            nameof(DeferredContent),
            typeof(object),
            typeof(ViewportDeferredContentControl),
            new PropertyMetadata(null, OnDeferredContentChanged));

    public static readonly DependencyProperty DeferredContentTemplateProperty =
        DependencyProperty.Register(
            nameof(DeferredContentTemplate),
            typeof(DataTemplate),
            typeof(ViewportDeferredContentControl),
            new PropertyMetadata(null, OnDeferredContentChanged));

    public static readonly DependencyProperty EstimatedHeightProperty =
        DependencyProperty.Register(
            nameof(EstimatedHeight),
            typeof(double),
            typeof(ViewportDeferredContentControl),
            new PropertyMetadata(48d, OnEstimatedHeightChanged));

    public static readonly DependencyProperty PreloadViewportCountProperty =
        DependencyProperty.Register(
            nameof(PreloadViewportCount),
            typeof(double),
            typeof(ViewportDeferredContentControl),
            new PropertyMetadata(1d));

    private static readonly DependencyPropertyKey IsContentRealizedPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsContentRealized),
            typeof(bool),
            typeof(ViewportDeferredContentControl),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsContentRealizedProperty =
        IsContentRealizedPropertyKey.DependencyProperty;

    public ViewportDeferredContentControl()
    {
        MinHeight = EstimatedHeight;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public object? DeferredContent
    {
        get => GetValue(DeferredContentProperty);
        set => SetValue(DeferredContentProperty, value);
    }

    public DataTemplate? DeferredContentTemplate
    {
        get => (DataTemplate?)GetValue(DeferredContentTemplateProperty);
        set => SetValue(DeferredContentTemplateProperty, value);
    }

    public double EstimatedHeight
    {
        get => (double)GetValue(EstimatedHeightProperty);
        set => SetValue(EstimatedHeightProperty, value);
    }

    public double PreloadViewportCount
    {
        get => (double)GetValue(PreloadViewportCountProperty);
        set => SetValue(PreloadViewportCountProperty, value);
    }

    public bool IsContentRealized =>
        (bool)GetValue(IsContentRealizedProperty);

    /// <summary>
    /// 供代码构建的早报块延迟创建视觉；XAML 场景使用 DeferredContentTemplate。
    /// </summary>
    internal Func<FrameworkElement>? ContentFactory { get; set; }

    /// <summary>
    /// 重内容离开缓冲视口时释放其后台工作，例如取消早报图片下载与解码。
    /// </summary>
    internal Action? ContentReleased { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        ScrollViewer? viewer = FindAncestorScrollViewer(this);
        if (viewer is null)
        {
            Realize();
            return;
        }

        _coordinator = Coordinators.GetValue(
            viewer,
            static scrollViewer => new ViewportCoordinator(scrollViewer));
        _coordinator.Register(this);
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_coordinator is { } coordinator)
        {
            _coordinator = null;
            if (coordinator.Unregister(this))
            {
                coordinator.Dispose();
                Coordinators.Remove(coordinator.Viewer);
            }
        }
        Unrealize();
    }

    private void OnSizeChanged(
        object sender,
        SizeChangedEventArgs eventArgs)
    {
        if (IsContentRealized
            && eventArgs.NewSize.Width > 0d
            && eventArgs.NewSize.Height > 0d)
        {
            _measuredWidth = eventArgs.NewSize.Width;
            _measuredHeight = eventArgs.NewSize.Height;
        }
        _coordinator?.QueueUpdate();
    }

    private void UpdateViewportRealization(ScrollViewer viewer)
    {
        if (!IsLoaded || !IsDescendantOf(viewer))
        {
            return;
        }

        double viewportHeight = Math.Max(
            viewer.ViewportHeight,
            viewer.ActualHeight);
        double buffer = viewportHeight
                        * Math.Max(0d, PreloadViewportCount);
        Rect bounds;
        try
        {
            bounds = TransformToAncestor(viewer).TransformBounds(
                new Rect(
                    0d,
                    0d,
                    Math.Max(1d, ActualWidth),
                    Math.Max(EstimatedHeight, ActualHeight)));
        }
        catch (InvalidOperationException)
        {
            Realize();
            return;
        }

        if (bounds.Bottom >= -buffer
            && bounds.Top <= viewportHeight + buffer)
        {
            Realize();
        }
        else
        {
            Unrealize();
        }
    }

    private void Realize()
    {
        if (IsContentRealized) return;
        bool canReuseMeasuredHeight =
            _measuredWidth > 0d
            && Math.Abs(ActualWidth - _measuredWidth) <= 1d;
        MinHeight = Math.Max(
            EstimatedHeight,
            canReuseMeasuredHeight ? _measuredHeight : 0d);
        ContentTemplate = DeferredContentTemplate;
        Content = ContentFactory?.Invoke() ?? DeferredContent;
        SetValue(IsContentRealizedPropertyKey, true);
    }

    private void Unrealize()
    {
        if (!IsContentRealized) return;
        if (ActualWidth > 0d && ActualHeight > 0d)
        {
            _measuredWidth = ActualWidth;
            _measuredHeight = ActualHeight;
        }
        MinHeight = Math.Max(
            EstimatedHeight,
            _measuredHeight);
        ContentReleased?.Invoke();
        Content = null;
        ContentTemplate = null;
        SetValue(IsContentRealizedPropertyKey, false);
    }

    private static void OnDeferredContentChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not ViewportDeferredContentControl control
            || !control.IsContentRealized)
        {
            return;
        }

        control.ContentTemplate = control.DeferredContentTemplate;
        control.Content =
            control.ContentFactory?.Invoke()
            ?? control.DeferredContent;
    }

    private static void OnEstimatedHeightChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is ViewportDeferredContentControl control
            && eventArgs.NewValue is double value
            && double.IsFinite(value))
        {
            control.MinHeight = Math.Max(0d, value);
        }
    }

    private static ScrollViewer? FindAncestorScrollViewer(
        DependencyObject child)
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(child);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer viewer) return viewer;
        }
        return null;
    }

    /// <summary>
    /// 合成滚动不产生逐帧 ScrollChanged，由滚动区唯一的渲染回调统一刷新延迟内容。
    /// </summary>
    internal static void RefreshCompositedViewport(
        ScrollViewer viewer)
    {
        if (Coordinators.TryGetValue(
                viewer,
                out ViewportCoordinator? coordinator))
        {
            coordinator.UpdateNow();
        }
    }

    private sealed class ViewportCoordinator : IDisposable
    {
        private readonly HashSet<ViewportDeferredContentControl> _controls =
            [];
        private bool _updateQueued;
        private bool _disposed;

        internal ViewportCoordinator(ScrollViewer viewer)
        {
            Viewer = viewer;
            Viewer.ScrollChanged += OnViewportChanged;
            Viewer.SizeChanged += OnViewportSizeChanged;
        }

        internal ScrollViewer Viewer { get; }

        internal void Register(
            ViewportDeferredContentControl control)
        {
            if (_disposed) return;
            _controls.Add(control);
            QueueUpdate();
        }

        internal bool Unregister(
            ViewportDeferredContentControl control)
        {
            _controls.Remove(control);
            return _controls.Count == 0;
        }

        internal void QueueUpdate()
        {
            if (_disposed || _updateQueued) return;
            _updateQueued = true;
            Viewer.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() =>
                {
                    _updateQueued = false;
                    UpdateNow();
                }));
        }

        internal void UpdateNow()
        {
            if (_disposed || !Viewer.IsLoaded) return;
            foreach (ViewportDeferredContentControl control
                     in _controls)
            {
                control.UpdateViewportRealization(Viewer);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Viewer.ScrollChanged -= OnViewportChanged;
            Viewer.SizeChanged -= OnViewportSizeChanged;
            _controls.Clear();
        }

        private void OnViewportChanged(
            object sender,
            ScrollChangedEventArgs eventArgs) =>
            QueueUpdate();

        private void OnViewportSizeChanged(
            object sender,
            SizeChangedEventArgs eventArgs) =>
            QueueUpdate();
    }
}
