using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LenxTool.App.Controls;

/// <summary>
/// 为应用内 ScrollViewer 接入 TwilightLemon/FluentScrollViewer 的原版滚轮运动模型。
/// </summary>
/// <remarks>
/// 输入判定、速度叠加、逐帧衰减/插值和停止阈值直接移植自：
/// https://github.com/TwilightLemon/FluentScrollViewer/blob/63f07a972bfde3d9a517f5c0f13f105df5a64b34/MyScrollViewer.cs
/// 上游 MIT 许可原文保存在同目录 FluentScrollViewer.LICENSE.txt。
/// </remarks>
internal static class SmoothWheelScrolling
{
    private const double VelocityFactor = 2d;
    private const double Friction = 0.92d;
    private const double LerpFactor = 0.5d;
    private const double TargetFrameTime = 1d / 144d;
    private const double VelocityStopThreshold = 0.1d;
    private const double PrecisionStopDistance = 0.5d;
    private static int _initialized;

    private static readonly DependencyProperty AnimationStateProperty =
        DependencyProperty.RegisterAttached(
            "AnimationState",
            typeof(WheelAnimationState),
            typeof(SmoothWheelScrolling),
            new PropertyMetadata(null));

    /// <summary>
    /// 注册一次类级滚轮入口，让显式控件和模板内部滚动区复用同一运动模型。
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
    /// 宿主策略只决定是否启用惯性；启用后的运动公式仍与上游一致。
    /// </summary>
    internal static bool ShouldUseUpstreamMotion(
        bool hasShiftModifier,
        bool clientAreaAnimation,
        int wheelScrollLines,
        bool reduceMotion) =>
        !hasShiftModifier
        && clientAreaAnimation
        && wheelScrollLines != 0
        && !reduceMotion;

    /// <summary>
    /// 按上游启发式区分标准鼠标滚轮与高分辨率触控板输入。
    /// </summary>
    internal static WheelInputMode ClassifyWheelInput(
        int wheelDelta,
        int previousWheelDelta,
        TimeSpan elapsedSincePreviousInput)
    {
        bool followsPrecisionInput =
            elapsedSincePreviousInput < TimeSpan.FromMilliseconds(100d)
            && previousWheelDelta % Mouse.MouseWheelDeltaForOneLine != 0;
        return wheelDelta % Mouse.MouseWheelDeltaForOneLine != 0
               || followsPrecisionInput
            ? WheelInputMode.Precision
            : WheelInputMode.Inertial;
    }

    /// <summary>
    /// 原样应用一次上游输入：鼠标只叠加速度，触控板则从当前真实偏移重设目标。
    /// </summary>
    internal static WheelMotionPlan ApplyUpstreamWheelInput(
        double currentOffset,
        double currentVelocity,
        double scrollableHeight,
        int wheelDelta,
        WheelInputMode mode)
    {
        double maximumOffset = NormalizeNonNegative(scrollableHeight);
        double current = Math.Clamp(
            NormalizeNonNegative(currentOffset),
            0d,
            maximumOffset);
        double velocity = double.IsFinite(currentVelocity)
            ? currentVelocity
            : 0d;

        if (mode == WheelInputMode.Precision)
        {
            return new(
                mode,
                Math.Clamp(current - wheelDelta, 0d, maximumOffset),
                0d,
                wheelDelta != 0);
        }

        return new(
            mode,
            current,
            velocity + (-wheelDelta * VelocityFactor),
            wheelDelta != 0);
    }

    /// <summary>
    /// 原样推进一帧鼠标惯性：先衰减速度，再用衰减后的速度更新真实逻辑偏移。
    /// </summary>
    internal static WheelAnimationFrame AdvanceUpstreamInertialFrame(
        double currentOffset,
        double currentVelocity,
        double scrollableHeight,
        TimeSpan frameInterval)
    {
        double current = double.IsFinite(currentOffset)
            ? currentOffset
            : 0d;
        double velocity = double.IsFinite(currentVelocity)
            ? currentVelocity
            : 0d;
        if (Math.Abs(velocity) < VelocityStopThreshold)
        {
            return new(current, 0d, true);
        }

        double timeFactor = frameInterval.TotalSeconds / TargetFrameTime;
        velocity *= Math.Pow(Friction, timeFactor);
        double nextOffset = Math.Clamp(
            current + velocity * (timeFactor / 24d),
            0d,
            NormalizeNonNegative(scrollableHeight));
        return new(nextOffset, velocity, false);
    }

    /// <summary>
    /// 原样推进一帧触控板精确滚动，并在插值后的距离小于半像素时吸附。
    /// </summary>
    internal static WheelAnimationFrame AdvanceUpstreamPrecisionFrame(
        double currentOffset,
        double targetOffset,
        TimeSpan frameInterval)
    {
        double current = double.IsFinite(currentOffset)
            ? currentOffset
            : 0d;
        double target = double.IsFinite(targetOffset)
            ? targetOffset
            : current;
        double timeFactor = frameInterval.TotalSeconds / TargetFrameTime;
        double lerpAmount =
            1d - Math.Pow(1d - LerpFactor, timeFactor);
        current += (target - current) * lerpAmount;
        if (Math.Abs(target - current) < PrecisionStopDistance)
        {
            return new(target, 0d, true);
        }
        return new(current, 0d, false);
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
        bool reduceMotion =
            Application.Current?.Resources["LenxTool.ReduceMotion"]
            is true;
        if (eventArgs.Handled || eventArgs.Delta == 0)
        {
            return false;
        }

        if (!ShouldUseUpstreamMotion(
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                SystemParameters.ClientAreaAnimation,
                SystemParameters.WheelScrollLines,
                reduceMotion))
        {
            Cancel(viewer);
            return false;
        }

        ScrollViewer? target = FindScrollTarget(
            eventArgs.OriginalSource as DependencyObject,
            eventArgs.Delta);
        if (!ReferenceEquals(target, viewer)) return false;

        WheelAnimationState state = GetOrCreateAnimationState(viewer);
        WheelInputMode mode = state.ClassifyInput(
            eventArgs.Delta,
            eventArgs.Timestamp);
        WheelMotionPlan plan = ApplyUpstreamWheelInput(
            viewer.VerticalOffset,
            state.Velocity,
            viewer.ScrollableHeight,
            eventArgs.Delta,
            mode);

        // 上游在自己的 OnMouseWheel 入口无条件接管事件；全局接入只额外保留嵌套滚动区的目标选择。
        eventArgs.Handled = true;
        state.StartOrRetarget(plan);
        return true;
    }

    /// <summary>
    /// 外部明确定位前终止滚轮会话，保留当前真实逻辑位置。
    /// </summary>
    internal static void Cancel(ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        GetAnimationState(viewer)?.StopAndReset();
    }

    /// <summary>
    /// 程序恢复阅读位置前终止旧滚轮动量，再立即提交新位置。
    /// </summary>
    internal static void ScrollToImmediately(
        ScrollViewer viewer,
        double targetOffset)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        Cancel(viewer);
        viewer.ScrollToVerticalOffset(
            Math.Clamp(
                NormalizeNonNegative(targetOffset),
                0d,
                NormalizeNonNegative(viewer.ScrollableHeight)));
    }

    internal static bool HasActiveAnimation(ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return GetAnimationState(viewer)?.IsRunning == true;
    }

    /// <summary>
    /// 精确上游路径不再使用 RenderTransform 合成过渡。
    /// </summary>
    internal static bool HasActiveCompositedTransition(
        ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return false;
    }

    internal static object? GetActiveAnimationSession(
        ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        WheelAnimationState? state = GetAnimationState(viewer);
        return state?.IsRunning == true ? state : null;
    }

    /// <summary>
    /// 延迟正文完成一次真实视口刷新后，记录本次逻辑偏移。
    /// </summary>
    internal static void RecordDeferredViewportRefresh(
        ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        GetAnimationState(viewer)?.RecordDeferredViewportRefresh(
            viewer.VerticalOffset);
    }

    private static void OnPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs eventArgs)
    {
        // AnimatedScrollViewer 由重写入口调用，避免同一事件被处理两次。
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
            && HasActiveAnimation(viewer))
        {
            Cancel(viewer);
        }
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
                && CanRouteWheel(
                    viewer.ScrollableHeight,
                    viewer.VerticalOffset,
                    wheelDelta,
                    HasActiveAnimation(viewer)))
            {
                return viewer;
            }
        }
        return null;
    }

    /// <summary>
    /// 全局接入保留嵌套 ScrollViewer 的边界冒泡；运动会话内部仍按上游持续衰减。
    /// </summary>
    internal static bool CanRouteWheel(
        double scrollableHeight,
        double effectiveOffset,
        int wheelDelta,
        bool hasActiveMotion)
    {
        if (wheelDelta == 0) return false;
        if (hasActiveMotion) return true;

        double maximumOffset = NormalizeNonNegative(scrollableHeight);
        if (maximumOffset <= 0.01d) return false;
        double offset = Math.Clamp(
            NormalizeNonNegative(effectiveOffset),
            0d,
            maximumOffset);
        return wheelDelta < 0
            ? offset < maximumOffset - 0.01d
            : offset > 0.01d;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            DependencyObject? visualParent =
                VisualTreeHelper.GetParent(child);
            if (visualParent is not null) return visualParent;
        }

        return child switch
        {
            FrameworkContentElement contentElement =>
                contentElement.Parent,
            FrameworkElement element => element.Parent,
            _ => LogicalTreeHelper.GetParent(child)
        };
    }

    private static double NormalizeNonNegative(double value) =>
        double.IsFinite(value) ? Math.Max(0d, value) : 0d;

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
        private double _targetOffset;
        private double _velocity;
        private WheelInputMode _mode;
        private int _lastWheelDelta;
        private int _lastWheelTimestamp;
        private double _lastDeferredViewportRefreshOffset = double.NaN;

        internal WheelAnimationState(ScrollViewer viewer)
        {
            _viewer = viewer;
            _renderingHandler = OnRendering;
            _viewer.IsManipulationEnabled = true;
            _viewer.PanningMode = PanningMode.VerticalOnly;
            _viewer.Unloaded += OnViewerUnloaded;
        }

        internal bool IsRunning => _isRunning;

        internal double Velocity => _velocity;

        internal WheelInputMode ClassifyInput(
            int wheelDelta,
            int eventTimestamp)
        {
            int elapsedMilliseconds = unchecked(
                Environment.TickCount - _lastWheelTimestamp);
            WheelInputMode mode = ClassifyWheelInput(
                wheelDelta,
                _lastWheelDelta,
                TimeSpan.FromMilliseconds(elapsedMilliseconds));
            _lastWheelDelta = wheelDelta;
            // 与上游一致：比较时读取 Environment.TickCount，保存时使用事件 Timestamp。
            _lastWheelTimestamp = eventTimestamp;
            return mode;
        }

        internal void StartOrRetarget(WheelMotionPlan plan)
        {
            if (!plan.ShouldAnimate) return;

            _targetOffset = plan.TargetOffset;
            _velocity = plan.Velocity;
            _mode = plan.Mode;
            if (_isRunning) return;

            _lastFrameTimestamp = Stopwatch.GetTimestamp();
            if (!double.IsFinite(
                    _lastDeferredViewportRefreshOffset))
            {
                _lastDeferredViewportRefreshOffset =
                    _viewer.VerticalOffset;
            }
            _isRunning = true;
            CompositionTarget.Rendering += _renderingHandler;
        }

        internal void StopAndReset()
        {
            bool wasRunning = _isRunning;
            if (_isRunning)
            {
                CompositionTarget.Rendering -= _renderingHandler;
                _isRunning = false;
            }
            _velocity = 0d;
            _targetOffset = _viewer.VerticalOffset;
            _mode = WheelInputMode.Inertial;
            if (wasRunning)
            {
                CompleteDeferredViewport();
            }
        }

        internal void RecordDeferredViewportRefresh(double offset)
        {
            if (double.IsFinite(offset))
            {
                _lastDeferredViewportRefreshOffset = offset;
            }
        }

        private void OnRendering(object? sender, EventArgs eventArgs)
        {
            if (!_isRunning) return;

            long currentTimestamp = Stopwatch.GetTimestamp();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(
                _lastFrameTimestamp,
                currentTimestamp);
            _lastFrameTimestamp = currentTimestamp;

            if (_mode == WheelInputMode.Precision)
            {
                WheelAnimationFrame precisionFrame =
                    AdvanceUpstreamPrecisionFrame(
                        _viewer.VerticalOffset,
                        _targetOffset,
                        elapsed);
                _velocity = 0d;
                if (precisionFrame.IsComplete)
                {
                    // 上游会先注销 Rendering 再提交吸附位置；状态标识保留到提交之后，
                    // 避免最后一个 ScrollChanged 绕过长文视口节流。
                    CompositionTarget.Rendering -= _renderingHandler;
                    _viewer.ScrollToVerticalOffset(
                        precisionFrame.Offset);
                    RefreshDeferredViewportIfNeeded();
                    _isRunning = false;
                    CompleteDeferredViewport();
                    return;
                }
                _viewer.ScrollToVerticalOffset(precisionFrame.Offset);
                RefreshDeferredViewportIfNeeded();
                return;
            }

            WheelAnimationFrame inertialFrame =
                AdvanceUpstreamInertialFrame(
                    _viewer.VerticalOffset,
                    _velocity,
                    _viewer.ScrollableHeight,
                    elapsed);
            _velocity = inertialFrame.Velocity;
            if (inertialFrame.IsComplete)
            {
                StopRendering();
                return;
            }

            _viewer.ScrollToVerticalOffset(inertialFrame.Offset);
            RefreshDeferredViewportIfNeeded();
        }

        private void RefreshDeferredViewportIfNeeded()
        {
            if (!ViewportDeferredContentControl
                .ShouldRefreshAnimatedViewport(
                    _lastDeferredViewportRefreshOffset,
                    _viewer.VerticalOffset,
                    _viewer.ViewportHeight))
            {
                return;
            }

            ViewportDeferredContentControl.RefreshAnimatedViewport(
                _viewer);
        }

        private void StopRendering()
        {
            if (!_isRunning) return;
            CompositionTarget.Rendering -= _renderingHandler;
            _isRunning = false;
            CompleteDeferredViewport();
        }

        private void CompleteDeferredViewport()
        {
            bool travelRequiresRefresh =
                ViewportDeferredContentControl
                    .ShouldRefreshAnimatedViewport(
                        _lastDeferredViewportRefreshOffset,
                        _viewer.VerticalOffset,
                        _viewer.ViewportHeight);
            ViewportDeferredContentControl.CompleteAnimatedViewport(
                _viewer,
                travelRequiresRefresh);
        }

        private void OnViewerUnloaded(
            object sender,
            RoutedEventArgs eventArgs)
        {
            if (_isRunning)
            {
                CompositionTarget.Rendering -= _renderingHandler;
                _isRunning = false;
            }
            _targetOffset = _viewer.VerticalOffset;
            _velocity = 0d;
            _mode = WheelInputMode.Inertial;
            _lastWheelDelta = 0;
            _lastWheelTimestamp = 0;
            _lastDeferredViewportRefreshOffset = double.NaN;
        }
    }
}

internal readonly record struct WheelAnimationFrame(
    double Offset,
    double Velocity,
    bool IsComplete = false);

internal enum WheelInputMode
{
    Inertial,
    Precision
}

/// <summary>
/// 输入合并后的上游状态；TargetOffset 仅在 Precision 模式下作为追踪目标。
/// </summary>
internal readonly record struct WheelMotionPlan(
    WheelInputMode Mode,
    double TargetOffset,
    double Velocity,
    bool ShouldAnimate);
