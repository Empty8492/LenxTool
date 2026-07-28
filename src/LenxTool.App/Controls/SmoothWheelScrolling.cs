using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LenxTool.App.Controls;

/// <summary>
/// 为所有原生 ScrollViewer 提供统一的滚轮灵敏度、目标累计和缓出过渡。
/// </summary>
internal static class SmoothWheelScrolling
{
    internal const double DailyBriefingWheelMultiplier = 1.45d;
    private const double PhysicalLineHeight = 16d;
    private const double SettleTimeFactor = 3.3d;
    private const double MaximumFrameIntervalSeconds = 0.05d;
    private static int _initialized;

    private static readonly DependencyProperty AnimationStateProperty =
        DependencyProperty.RegisterAttached(
            "AnimationState",
            typeof(WheelAnimationState),
            typeof(SmoothWheelScrolling),
            new PropertyMetadata(null));

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
    /// Advances the persistent wheel animation by one rendered frame.
    /// A critically damped response keeps velocity continuous when burst input extends the target.
    /// </summary>
    internal static WheelAnimationFrame AdvanceFrame(
        double currentOffset,
        double targetOffset,
        double currentVelocity,
        TimeSpan frameInterval,
        TimeSpan responseDuration)
    {
        double current = double.IsFinite(currentOffset) ? currentOffset : 0d;
        double target = double.IsFinite(targetOffset) ? targetOffset : current;
        double velocity = double.IsFinite(currentVelocity) ? currentVelocity : 0d;
        double deltaSeconds = Math.Clamp(
            frameInterval.TotalSeconds,
            0d,
            MaximumFrameIntervalSeconds);
        if (deltaSeconds <= 0d || Math.Abs(target - current) < 0.001d)
        {
            return new(
                Math.Abs(target - current) < 0.001d ? target : current,
                Math.Abs(target - current) < 0.001d ? 0d : velocity);
        }

        double durationSeconds = Math.Max(
            responseDuration.TotalSeconds,
            0.001d);
        double smoothTime = durationSeconds / SettleTimeFactor;
        double omega = 2d / smoothTime;
        double scaledInterval = omega * deltaSeconds;
        double decay = 1d
                       / (1d
                          + scaledInterval
                          + 0.48d * scaledInterval * scaledInterval
                          + 0.235d * scaledInterval * scaledInterval
                          * scaledInterval);
        double displacement = current - target;
        double momentum = (velocity + omega * displacement) * deltaSeconds;
        double nextVelocity = (velocity - omega * momentum) * decay;
        double nextOffset = target + (displacement + momentum) * decay;

        bool crossedTarget = target > current
            ? nextOffset > target
            : nextOffset < target;
        return crossedTarget
            ? new(target, 0d)
            : new(nextOffset, nextVelocity);
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
        GetAnimationState(viewer)?.Stop();
        SetTargetVerticalOffset(viewer, currentOffset);
        SetIsAnimationActive(viewer, false);
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

    /// <summary>
    /// Returns the active state identity used by runtime acceptance tests to verify burst coalescing.
    /// </summary>
    internal static object? GetActiveAnimationSession(ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return GetIsAnimationActive(viewer)
            ? GetAnimationState(viewer)
            : null;
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
        SetTargetVerticalOffset(viewer, plan.TargetOffset);
        if (!plan.ShouldAnimate)
        {
            GetAnimationState(viewer)?.Stop();
            SetIsAnimationActive(viewer, false);
            viewer.ScrollToVerticalOffset(plan.TargetOffset);
            return;
        }

        WheelAnimationState state = GetOrCreateAnimationState(viewer);
        SetIsAnimationActive(viewer, true);
        state.StartOrRetarget(plan.TargetOffset, plan.Duration);
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

    private static WheelAnimationState? GetAnimationState(
        DependencyObject target) =>
        (WheelAnimationState?)target.GetValue(AnimationStateProperty);

    private static WheelAnimationState GetOrCreateAnimationState(
        ScrollViewer viewer)
    {
        WheelAnimationState? state = GetAnimationState(viewer);
        if (state is not null) return state;

        state = new WheelAnimationState(viewer);
        viewer.SetValue(AnimationStateProperty, state);
        return state;
    }

    private sealed class WheelAnimationState
    {
        private readonly ScrollViewer _viewer;
        private readonly EventHandler _renderingHandler;
        private bool _isRunning;
        private long _lastFrameTimestamp;
        private long _completionTimestamp;
        private double _targetOffset;
        private double _velocity;
        private TimeSpan _responseDuration;

        internal WheelAnimationState(ScrollViewer viewer)
        {
            _viewer = viewer;
            _renderingHandler = OnRendering;
            _viewer.Unloaded += OnViewerUnloaded;
        }

        internal void StartOrRetarget(
            double targetOffset,
            TimeSpan responseDuration)
        {
            long now = Stopwatch.GetTimestamp();
            double currentOffset = _viewer.VerticalOffset;
            if (_isRunning
                && (targetOffset - currentOffset) * _velocity < 0d)
            {
                // A direction reversal should respond immediately instead of
                // carrying momentum away from the user's new target.
                _velocity = 0d;
            }

            _targetOffset = targetOffset;
            _responseDuration = responseDuration;
            _completionTimestamp = now
                                   + (long)(responseDuration.TotalSeconds
                                            * Stopwatch.Frequency);
            if (_isRunning) return;

            _velocity = 0d;
            _lastFrameTimestamp = now;
            _isRunning = true;
            CompositionTarget.Rendering += _renderingHandler;
        }

        internal void Stop()
        {
            if (_isRunning)
            {
                CompositionTarget.Rendering -= _renderingHandler;
                _isRunning = false;
            }

            _velocity = 0d;
        }

        private void OnRendering(object? sender, EventArgs eventArgs)
        {
            if (!_isRunning) return;

            long now = Stopwatch.GetTimestamp();
            if (now >= _completionTimestamp)
            {
                Complete();
                return;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(
                _lastFrameTimestamp,
                now);
            _lastFrameTimestamp = now;
            double maximumOffset = NormalizeNonNegative(
                _viewer.ScrollableHeight);
            _targetOffset = Math.Clamp(
                _targetOffset,
                0d,
                maximumOffset);
            WheelAnimationFrame frame = AdvanceFrame(
                _viewer.VerticalOffset,
                _targetOffset,
                _velocity,
                elapsed,
                _responseDuration);
            _velocity = frame.Velocity;
            double nextOffset = Math.Clamp(
                frame.Offset,
                0d,
                maximumOffset);
            _viewer.ScrollToVerticalOffset(nextOffset);
            if (Math.Abs(nextOffset - _targetOffset) < 0.05d)
            {
                Complete();
            }
        }

        private void OnViewerUnloaded(
            object sender,
            RoutedEventArgs eventArgs)
        {
            Cancel(_viewer);
        }

        private void Complete()
        {
            Stop();
            _viewer.ScrollToVerticalOffset(_targetOffset);
            SetTargetVerticalOffset(_viewer, _targetOffset);
            SetIsAnimationActive(_viewer, false);
        }
    }
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

/// <summary>
/// Describes the offset and velocity produced for a single rendered frame.
/// </summary>
internal readonly record struct WheelAnimationFrame(
    double Offset,
    double Velocity);
