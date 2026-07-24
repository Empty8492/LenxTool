using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace LenxTool.App.Controls;

public sealed class AnimatedScrollViewer : ScrollViewer
{
    private static readonly DependencyPropertyKey IsBackToTopVisiblePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsBackToTopVisible),
            typeof(bool),
            typeof(AnimatedScrollViewer),
            new PropertyMetadata(false));

    private static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.Register(
            nameof(AnimatedVerticalOffset),
            typeof(double),
            typeof(AnimatedScrollViewer),
            new PropertyMetadata(0d, OnAnimatedVerticalOffsetChanged));

    public static readonly DependencyProperty WheelScrollMultiplierProperty =
        DependencyProperty.Register(
            nameof(WheelScrollMultiplier),
            typeof(double),
            typeof(AnimatedScrollViewer),
            new FrameworkPropertyMetadata(1d));

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

    public double WheelScrollMultiplier
    {
        get => NormalizeWheelScrollMultiplier(
            (double)GetValue(WheelScrollMultiplierProperty));
        set => SetValue(
            WheelScrollMultiplierProperty,
            NormalizeWheelScrollMultiplier(value));
    }

    public object? ScrollResetKey
    {
        get => GetValue(ScrollResetKeyProperty);
        set => SetValue(ScrollResetKeyProperty, value);
    }

    private double AnimatedVerticalOffset
    {
        get => (double)GetValue(AnimatedVerticalOffsetProperty);
        set => SetValue(AnimatedVerticalOffsetProperty, value);
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
        CancelScrollAnimation();
        if (!e.Handled && WheelScrollMultiplier > 1d)
        {
            double systemLines = SystemParameters.WheelScrollLines;
            if (systemLines == 0)
            {
                base.OnPreviewMouseWheel(e);
                return;
            }
            double baseDistance = systemLines < 0
                ? Math.Max(1d, ViewportHeight)
                : Math.Max(1d, systemLines) * 16d;
            double requestedOffset = VerticalOffset
                - e.Delta / 120d * baseDistance * WheelScrollMultiplier;
            ScrollToVerticalOffset(Math.Clamp(requestedOffset, 0d, ScrollableHeight));
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseWheel(e);
    }

    private void SmoothScrollToTop()
    {
        if (VerticalOffset <= 0) return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            ScrollToTop();
            return;
        }

        CancelScrollAnimation();
        AnimatedVerticalOffset = VerticalOffset;
        var animation = new DoubleAnimation
        {
            From = VerticalOffset,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            BeginAnimation(AnimatedVerticalOffsetProperty, null);
            ScrollToTop();
        };
        BeginAnimation(AnimatedVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void CancelScrollAnimation()
    {
        double currentOffset = VerticalOffset;
        BeginAnimation(AnimatedVerticalOffsetProperty, null);
        AnimatedVerticalOffset = currentOffset;
    }

    private static void OnAnimatedVerticalOffsetChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is AnimatedScrollViewer viewer && e.NewValue is double offset)
        {
            viewer.ScrollToVerticalOffset(offset);
        }
    }

    private static double NormalizeWheelScrollMultiplier(double multiplier) =>
        double.IsFinite(multiplier)
            ? Math.Clamp(multiplier, 1d, 3d)
            : 1d;

    private static void OnScrollResetKeyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not AnimatedScrollViewer viewer
            || Equals(e.OldValue, e.NewValue))
        {
            return;
        }

        viewer.CancelScrollAnimation();
        viewer.ScrollToTop();
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
