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
        "回到顶部",
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
        // 每日早报复用全局 Fluent 运动会话，确保所有页面拥有一致的滚轮手感。
        if (SmoothWheelScrolling.TryHandleWheel(this, e)) return;
        base.OnPreviewMouseWheel(e);
    }

    private void SmoothScrollToTop()
    {
        if (VerticalOffset <= 0) return;
        // 回顶属于明确定位命令，立即提交并终止残余滚轮动量，避免长文跨越大量延迟块时制造额外扫描。
        SmoothWheelScrolling.ScrollToImmediately(this, 0d);
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
