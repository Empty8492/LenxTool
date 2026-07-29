using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LenxTool.App.Controls;

public sealed class AnimatedScrollViewer : ScrollViewer
{
    private static readonly DependencyPropertyKey IsBackToTopVisiblePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsBackToTopVisible),
            typeof(bool),
            typeof(AnimatedScrollViewer),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ScrollResetKeyProperty =
        DependencyProperty.Register(
            nameof(ScrollResetKey),
            typeof(object),
            typeof(AnimatedScrollViewer),
            new PropertyMetadata(null, OnScrollResetKeyChanged));

    public static readonly DependencyProperty IsBackToTopVisibleProperty =
        IsBackToTopVisiblePropertyKey.DependencyProperty;

    public static readonly RoutedUICommand SmoothScrollToTopCommand = new(
        "平滑回到顶部",
        nameof(SmoothScrollToTopCommand),
        typeof(AnimatedScrollViewer));

    static AnimatedScrollViewer()
    {
        CommandManager.RegisterClassCommandBinding(
            typeof(AnimatedScrollViewer),
            new CommandBinding(
                SmoothScrollToTopCommand,
                ExecuteSmoothScrollToTop,
                CanExecuteSmoothScrollToTop));
    }

    public bool IsBackToTopVisible => (bool)GetValue(IsBackToTopVisibleProperty);

    public object? ScrollResetKey
    {
        get => GetValue(ScrollResetKeyProperty);
        set => SetValue(ScrollResetKeyProperty, value);
    }

    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);
        bool shouldShow = e.VerticalOffset > 180;
        if (shouldShow == IsBackToTopVisible) return;
        SetValue(IsBackToTopVisiblePropertyKey, shouldShow);
        CommandManager.InvalidateRequerySuggested();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        // 每日早报也复用全局滚轮计划，确保所有页面拥有同一灵敏度与过渡。
        if (SmoothWheelScrolling.TryHandleWheel(this, e)) return;
        base.OnPreviewMouseWheel(e);
    }

    private void SmoothScrollToTop()
    {
        if (VerticalOffset <= 0) return;
        // 回顶沿用滚轮合成路径：逻辑位置一次提交，过渡期间只变更内容渲染变换。
        SmoothWheelScrolling.ScrollToSmoothly(
            this,
            targetOffset: 0d,
            TimeSpan.FromMilliseconds(420d));
    }

    private static void OnScrollResetKeyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not AnimatedScrollViewer viewer
            || Equals(e.OldValue, e.NewValue))
        {
            return;
        }

        SmoothWheelScrolling.ScrollToImmediately(viewer, 0d);
    }

    private static void ExecuteSmoothScrollToTop(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is AnimatedScrollViewer viewer) viewer.SmoothScrollToTop();
        e.Handled = true;
    }

    private static void CanExecuteSmoothScrollToTop(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = sender is AnimatedScrollViewer viewer && viewer.VerticalOffset > 0;
        e.Handled = true;
    }
}
