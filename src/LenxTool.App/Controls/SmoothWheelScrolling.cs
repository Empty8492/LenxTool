using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LenxTool.App.Controls;

/// <summary>
/// 为所有原生 ScrollViewer 提供统一的滚轮灵敏度、目标累计和缓出过渡。
/// </summary>
internal static class SmoothWheelScrolling
{
    internal const double DailyBriefingWheelMultiplier = 1.45d;
    private const double PhysicalLineHeight = 16d;
    private static int _initialized;

    private static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedVerticalOffset",
            typeof(double),
            typeof(SmoothWheelScrolling),
            new PropertyMetadata(0d, OnAnimatedVerticalOffsetChanged));

    private static readonly DependencyProperty TargetVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "TargetVerticalOffset",
            typeof(double),
            typeof(SmoothWheelScrolling),
            new PropertyMetadata(0d));

    private static readonly DependencyProperty IsAnimationActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsAnimationActive",
            typeof(bool),
            typeof(SmoothWheelScrolling),
            new PropertyMetadata(false));

    private static readonly DependencyProperty AnimationGenerationProperty =
        DependencyProperty.RegisterAttached(
            "AnimationGeneration",
            typeof(int),
            typeof(SmoothWheelScrolling),
            new PropertyMetadata(0));

    /// <summary>
    /// 注册一次 ScrollViewer 类级事件，让显式控件和模板内部滚动区使用同一行为。
    /// </summary>
    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnDirectScrollInput));
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnDirectScrollInput));
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(OnDirectScrollInput));
    }

    /// <summary>
    /// 计算独立于 WPF 动画时钟的滚动计划，便于冻结灵敏度和降级规则。
    /// </summary>
    internal static WheelScrollPlan CreateWheelPlan(
        double currentOffset,
        double targetOffset,
        double scrollableHeight,
        double viewportHeight,
        int wheelDelta,
        int systemWheelLines,
        bool usesLogicalUnits,
        bool motionAllowed)
    {
        double maximumOffset = NormalizeNonNegative(scrollableHeight);
        double current = Math.Clamp(
            NormalizeNonNegative(currentOffset),
            0d,
            maximumOffset);
        double pendingTarget = double.IsFinite(targetOffset)
            ? Math.Clamp(targetOffset, 0d, maximumOffset)
            : current;
        if (wheelDelta == 0 || systemWheelLines == 0)
        {
            return new(current, TimeSpan.Zero);
        }

        double baseDistance = systemWheelLines < 0
            ? Math.Max(1d, NormalizeNonNegative(viewportHeight))
            : Math.Max(1d, systemWheelLines)
              * (usesLogicalUnits ? 1d : PhysicalLineHeight);
        double requestedTarget = pendingTarget
            - wheelDelta / 120d
            * baseDistance
            * DailyBriefingWheelMultiplier;
        double target = Math.Clamp(requestedTarget, 0d, maximumOffset);
        double distance = Math.Abs(target - current);
        if (!motionAllowed || distance < 0.01d)
        {
            return new(target, TimeSpan.Zero);
        }

        // 短距离保持迅捷，连续滚轮累积为更远目标时适度延长但不产生拖沓感。
        double durationMilliseconds = Math.Clamp(
            140d + distance * 0.3d,
            160d,
            220d);
        return new(
            target,
            TimeSpan.FromMilliseconds(durationMilliseconds));
    }

    /// <summary>
    /// 供带“回到顶部”能力的派生控件复用全局滚轮处理。
    /// </summary>
    internal static bool TryHandleWheel(
        ScrollViewer viewer,
        MouseWheelEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(eventArgs);
        if (eventArgs.Handled
            || eventArgs.Delta == 0
            || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return false;
        }

        ScrollViewer? target = FindScrollTarget(
            eventArgs.OriginalSource as DependencyObject,
            eventArgs.Delta);
        if (!ReferenceEquals(target, viewer)) return false;

        double pendingTarget = GetIsAnimationActive(viewer)
            ? GetTargetVerticalOffset(viewer)
            : viewer.VerticalOffset;
        WheelScrollPlan plan = CreateWheelPlan(
            viewer.VerticalOffset,
            pendingTarget,
            viewer.ScrollableHeight,
            viewer.ViewportHeight,
            eventArgs.Delta,
            SystemParameters.WheelScrollLines,
            viewer.CanContentScroll && !UsesPixelScrollUnit(viewer),
            IsMotionAllowed());
        ApplyPlan(viewer, plan);
        eventArgs.Handled = true;
        return true;
    }

    /// <summary>
    /// 外部滚动操作开始前终止未完成的滚轮动画，保留屏幕当前真实位置。
    /// </summary>
    internal static void Cancel(ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        double currentOffset = viewer.VerticalOffset;
        viewer.BeginAnimation(AnimatedVerticalOffsetProperty, null);
        SetAnimatedVerticalOffset(viewer, currentOffset);
        SetTargetVerticalOffset(viewer, currentOffset);
        SetIsAnimationActive(viewer, false);
        SetAnimationGeneration(
            viewer,
            unchecked(GetAnimationGeneration(viewer) + 1));
    }

    /// <summary>
    /// 程序恢复阅读位置前先终止旧动画，防止旧文章目标覆盖新文章进度。
    /// </summary>
    internal static void ScrollToImmediately(
        ScrollViewer viewer,
        double targetOffset)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        Cancel(viewer);
        double normalizedTarget = Math.Clamp(
            NormalizeNonNegative(targetOffset),
            0d,
            NormalizeNonNegative(viewer.ScrollableHeight));
        SetAnimatedVerticalOffset(viewer, normalizedTarget);
        SetTargetVerticalOffset(viewer, normalizedTarget);
        viewer.ScrollToVerticalOffset(normalizedTarget);
    }

    /// <summary>
    /// 暴露只读动画状态，供派生控件协作与真实 WPF 验收使用。
    /// </summary>
    internal static bool HasActiveAnimation(ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return GetIsAnimationActive(viewer);
    }

    private static void OnPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs eventArgs)
    {
        // 派生控件需要先取消自己的“回到顶部”动画，再由其重写入口调用共享计划。
        if (sender is ScrollViewer viewer
            && viewer is not AnimatedScrollViewer)
        {
            TryHandleWheel(viewer, eventArgs);
        }
    }

    private static void OnDirectScrollInput(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is ScrollViewer viewer
            && GetIsAnimationActive(viewer))
        {
            // 拖动滚动条、键盘翻页或触控开始时，以用户直接操作为最高优先级。
            Cancel(viewer);
        }
    }

    private static void ApplyPlan(
        ScrollViewer viewer,
        WheelScrollPlan plan)
    {
        Cancel(viewer);
        int animationGeneration = GetAnimationGeneration(viewer);
        SetTargetVerticalOffset(viewer, plan.TargetOffset);
        if (!plan.ShouldAnimate)
        {
            SetAnimatedVerticalOffset(viewer, plan.TargetOffset);
            viewer.ScrollToVerticalOffset(plan.TargetOffset);
            return;
        }

        SetIsAnimationActive(viewer, true);
        SetAnimatedVerticalOffset(viewer, viewer.VerticalOffset);
        var animation = new DoubleAnimation
        {
            From = viewer.VerticalOffset,
            To = plan.TargetOffset,
            Duration = new Duration(plan.Duration),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            // 被取消的旧时钟即使已排队触发 Completed，也不能覆盖新的程序化位置。
            if (GetAnimationGeneration(viewer) != animationGeneration) return;
            viewer.BeginAnimation(AnimatedVerticalOffsetProperty, null);
            SetAnimatedVerticalOffset(viewer, plan.TargetOffset);
            viewer.ScrollToVerticalOffset(plan.TargetOffset);
            SetIsAnimationActive(viewer, false);
        };
        viewer.BeginAnimation(
            AnimatedVerticalOffsetProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static ScrollViewer? FindScrollTarget(
        DependencyObject? source,
        int wheelDelta)
    {
        for (DependencyObject? current = source;
             current is not null;
             current = GetParent(current))
        {
            if (current is ScrollViewer viewer
                && CanScroll(viewer, wheelDelta))
            {
                return viewer;
            }
        }
        return null;
    }

    private static bool CanScroll(ScrollViewer viewer, int wheelDelta)
    {
        double effectiveOffset = GetIsAnimationActive(viewer)
            ? GetTargetVerticalOffset(viewer)
            : viewer.VerticalOffset;
        return viewer.ScrollableHeight > 0.01d
               && (wheelDelta < 0
                   ? effectiveOffset < viewer.ScrollableHeight - 0.01d
                   : effectiveOffset > 0.01d);
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            DependencyObject? visualParent = VisualTreeHelper.GetParent(child);
            if (visualParent is not null) return visualParent;
        }

        return child switch
        {
            FrameworkContentElement contentElement => contentElement.Parent,
            FrameworkElement element => element.Parent,
            _ => LogicalTreeHelper.GetParent(child)
        };
    }

    private static bool UsesPixelScrollUnit(ScrollViewer viewer)
    {
        for (DependencyObject? current = viewer;
             current is not null;
             current = GetParent(current))
        {
            if (current is ItemsControl itemsControl)
            {
                return VirtualizingPanel.GetScrollUnit(itemsControl)
                    == ScrollUnit.Pixel;
            }
        }
        return false;
    }

    private static bool IsMotionAllowed()
    {
        if (!SystemParameters.ClientAreaAnimation) return false;
        object? reduceMotion =
            Application.Current?.Resources["LenxTool.ReduceMotion"];
        return reduceMotion is not true;
    }

    private static double NormalizeNonNegative(double value) =>
        double.IsFinite(value) ? Math.Max(0d, value) : 0d;

    private static void OnAnimatedVerticalOffsetChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is ScrollViewer viewer
            && eventArgs.NewValue is double offset)
        {
            viewer.ScrollToVerticalOffset(offset);
        }
    }

    private static double GetTargetVerticalOffset(DependencyObject target) =>
        (double)target.GetValue(TargetVerticalOffsetProperty);

    private static void SetTargetVerticalOffset(
        DependencyObject target,
        double value) =>
        target.SetValue(TargetVerticalOffsetProperty, value);

    private static bool GetIsAnimationActive(DependencyObject target) =>
        (bool)target.GetValue(IsAnimationActiveProperty);

    private static void SetIsAnimationActive(
        DependencyObject target,
        bool value) =>
        target.SetValue(IsAnimationActiveProperty, value);

    private static int GetAnimationGeneration(DependencyObject target) =>
        (int)target.GetValue(AnimationGenerationProperty);

    private static void SetAnimationGeneration(
        DependencyObject target,
        int value) =>
        target.SetValue(AnimationGenerationProperty, value);

    private static void SetAnimatedVerticalOffset(
        DependencyObject target,
        double value) =>
        target.SetValue(AnimatedVerticalOffsetProperty, value);
}

/// <summary>
/// 描述单次滚轮输入的最终目标与过渡时长。
/// </summary>
internal readonly record struct WheelScrollPlan(
    double TargetOffset,
    TimeSpan Duration)
{
    public bool ShouldAnimate => Duration > TimeSpan.Zero;
}
