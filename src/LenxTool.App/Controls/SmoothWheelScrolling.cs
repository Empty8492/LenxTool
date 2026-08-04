using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LenxTool.App.Controls;

/// <summary>
/// 为所有原生 ScrollViewer 提供统一的 Fluent 惯性滚轮，并在重页面优先使用合成层过渡。
/// </summary>
internal static class SmoothWheelScrolling
{
    internal const double DailyBriefingWheelMultiplier = 1.45d;
    // 手感参数参考 TwilightLemon/FluentScrollViewer；积分、边界与合成层接入均按 LenxTool 场景独立实现。
    // https://github.com/TwilightLemon/FluentScrollViewer/blob/63f07a972bfde3d9a517f5c0f13f105df5a64b34/MyScrollViewer.cs
    private const double FluentVelocityFactor = 2d;
    private const double FluentFriction = 0.92d;
    private const double FluentPrecisionLerpFactor = 0.5d;
    private const double FluentTargetFrameSeconds = 1d / 144d;
    private const double FluentVelocityStopThreshold = 0.1d;
    private const double FluentPrecisionStopDistance = 0.5d;
    private const double DefaultSystemWheelLines = 3d;
    private const double FluentTravelPerVelocityUnit =
        FluentFriction / (24d * (1d - FluentFriction));
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
    /// 区分标准鼠标滚轮与高分辨率触控板输入；短时间内跟随精确 delta 的整格事件仍归入同一触控板手势。
    /// </summary>
    internal static WheelInputMode ClassifyWheelInput(
        int wheelDelta,
        int previousWheelDelta,
        TimeSpan elapsedSincePreviousInput)
    {
        bool followsPrecisionInput =
            elapsedSincePreviousInput >= TimeSpan.Zero
            && elapsedSincePreviousInput
            < TimeSpan.FromMilliseconds(100d)
            && previousWheelDelta % Mouse.MouseWheelDeltaForOneLine != 0;
        return wheelDelta % Mouse.MouseWheelDeltaForOneLine != 0
               || followsPrecisionInput
            ? WheelInputMode.Precision
            : WheelInputMode.Inertial;
    }

    /// <summary>
    /// 将一次滚轮输入转换为可持续复用的运动状态；标准三行鼠标设置保持参考项目的 2.0 速度力度。
    /// </summary>
    internal static WheelMotionPlan CreateWheelMotionPlan(
        double currentOffset,
        double pendingTargetOffset,
        double currentVelocity,
        double scrollableHeight,
        double viewportHeight,
        int wheelDelta,
        int systemWheelLines,
        bool usesLogicalUnits,
        bool motionAllowed,
        WheelInputMode mode)
    {
        double maximumOffset = NormalizeNonNegative(scrollableHeight);
        double current = Math.Clamp(
            NormalizeNonNegative(currentOffset),
            0d,
            maximumOffset);
        if (wheelDelta == 0 || systemWheelLines == 0)
        {
            return new(mode, current, 0d, false);
        }

        if (mode == WheelInputMode.Precision)
        {
            double pendingTarget = double.IsFinite(pendingTargetOffset)
                ? Math.Clamp(pendingTargetOffset, 0d, maximumOffset)
                : current;
            double target = Math.Clamp(
                pendingTarget - wheelDelta,
                0d,
                maximumOffset);
            bool shouldAnimate = motionAllowed
                                 && Math.Abs(target - current)
                                 >= FluentPrecisionStopDistance;
            return new(mode, target, 0d, shouldAnimate);
        }

        double velocityImpulse = CreateVelocityImpulse(
            wheelDelta,
            systemWheelLines,
            usesLogicalUnits,
            viewportHeight);
        double inheritedVelocity = double.IsFinite(currentVelocity)
            ? currentVelocity
            : 0d;
        if (inheritedVelocity * velocityImpulse < 0d)
        {
            // 反向输入代表新的明确意图：丢弃旧方向动量，再从当前视觉位置施加一格新冲量。
            inheritedVelocity = 0d;
        }
        double velocity = inheritedVelocity + velocityImpulse;
        double targetOffset = Math.Clamp(
            current + velocity * FluentTravelPerVelocityUnit,
            0d,
            maximumOffset);
        bool canAnimate = motionAllowed
                          && Math.Abs(velocity)
                          >= FluentVelocityStopThreshold
                          && Math.Abs(targetOffset - current) >= 0.01d;
        return new(
            mode,
            targetOffset,
            canAnimate ? velocity : 0d,
            canAnimate);
    }

    /// <summary>
    /// 以 144Hz 下每帧保留 92% 速度为基准推进惯性，并用解析积分消除不同刷新率造成的落点漂移。
    /// </summary>
    internal static WheelAnimationFrame AdvanceInertialFrame(
        double currentOffset,
        double targetOffset,
        double currentVelocity,
        TimeSpan frameInterval)
    {
        double current = double.IsFinite(currentOffset) ? currentOffset : 0d;
        double target = double.IsFinite(targetOffset) ? targetOffset : current;
        double velocity = double.IsFinite(currentVelocity)
            ? currentVelocity
            : 0d;
        double deltaSeconds = Math.Clamp(
            frameInterval.TotalSeconds,
            0d,
            MaximumFrameIntervalSeconds);
        if (Math.Abs(target - current) < 0.01d
            || Math.Abs(velocity) < FluentVelocityStopThreshold)
        {
            return new(target, 0d, true);
        }
        if (deltaSeconds <= 0d)
        {
            return new(current, velocity, false);
        }

        double timeFactor = deltaSeconds / FluentTargetFrameSeconds;
        double decay = Math.Pow(FluentFriction, timeFactor);
        double nextVelocity = velocity * decay;
        double nextOffset = current
                            + velocity
                            * (1d - decay)
                            * FluentTravelPerVelocityUnit;
        bool crossedTarget = target > current
            ? nextOffset >= target
            : nextOffset <= target;
        bool isComplete = crossedTarget
                          || Math.Abs(target - nextOffset) < 0.01d
                          || Math.Abs(nextVelocity)
                          < FluentVelocityStopThreshold;
        return isComplete
            ? new(target, 0d, true)
            : new(nextOffset, nextVelocity, false);
    }

    /// <summary>
    /// 高分辨率输入每个 144Hz 基准帧消除一半剩余距离，连续 delta 只扩展目标而不重启动画时钟。
    /// </summary>
    internal static WheelAnimationFrame AdvancePrecisionFrame(
        double currentOffset,
        double targetOffset,
        TimeSpan frameInterval)
    {
        double current = double.IsFinite(currentOffset) ? currentOffset : 0d;
        double target = double.IsFinite(targetOffset) ? targetOffset : current;
        double remaining = target - current;
        if (Math.Abs(remaining) < FluentPrecisionStopDistance)
        {
            return new(target, 0d, true);
        }

        double deltaSeconds = Math.Clamp(
            frameInterval.TotalSeconds,
            0d,
            MaximumFrameIntervalSeconds);
        if (deltaSeconds <= 0d)
        {
            return new(current, 0d, false);
        }

        double timeFactor = deltaSeconds / FluentTargetFrameSeconds;
        double lerpAmount = 1d
                            - Math.Pow(
                                1d - FluentPrecisionLerpFactor,
                                timeFactor);
        double nextOffset = current + remaining * lerpAmount;
        return Math.Abs(target - nextOffset)
               < FluentPrecisionStopDistance
            ? new(target, 0d, true)
            : new(nextOffset, 0d, false);
    }

    private static double CreateVelocityImpulse(
        int wheelDelta,
        int systemWheelLines,
        bool usesLogicalUnits,
        double viewportHeight)
    {
        if (systemWheelLines > 0 && !usesLogicalUnits)
        {
            // 默认三行设置与参考实现完全一致；其他系统行数按比例缩放，尊重用户的滚轮偏好。
            return -wheelDelta
                   * FluentVelocityFactor
                   * systemWheelLines
                   / DefaultSystemWheelLines;
        }

        double desiredTravel = systemWheelLines < 0
            ? -wheelDelta / 120d
              * Math.Max(1d, NormalizeNonNegative(viewportHeight))
            : -wheelDelta / 120d
              * Math.Max(1d, systemWheelLines)
              * DailyBriefingWheelMultiplier;
        return desiredTravel / FluentTravelPerVelocityUnit;
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

        WheelAnimationState state = GetOrCreateAnimationState(viewer);
        bool hasActiveMotion = GetIsAnimationActive(viewer);
        double currentOffset = hasActiveMotion
            ? state.GetCurrentVisualOffset()
            : viewer.VerticalOffset;
        WheelInputMode mode = state.ClassifyInput(
            eventArgs.Delta,
            eventArgs.Timestamp);
        double pendingTarget = hasActiveMotion
                               && mode == WheelInputMode.Precision
                               && state.Mode == WheelInputMode.Precision
            ? GetTargetVerticalOffset(viewer)
            : currentOffset;
        double currentVelocity = hasActiveMotion
                                 && mode == WheelInputMode.Inertial
                                 && state.Mode == WheelInputMode.Inertial
            ? state.Velocity
            : 0d;
        WheelMotionPlan plan = CreateWheelMotionPlan(
            currentOffset,
            pendingTarget,
            currentVelocity,
            viewer.ScrollableHeight,
            viewer.ViewportHeight,
            eventArgs.Delta,
            SystemParameters.WheelScrollLines,
            viewer.CanContentScroll && !UsesPixelScrollUnit(viewer),
            IsMotionAllowed(),
            mode);
        ApplyWheelPlan(viewer, state, plan);
        eventArgs.Handled = true;
        return true;
    }

    /// <summary>
    /// 外部滚动操作开始前终止未完成的滚轮动画，保留屏幕当前真实位置。
    /// </summary>
    internal static void Cancel(ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        double currentOffset =
            GetAnimationState(viewer)?.StopAndCommitVisualOffset()
            ?? viewer.VerticalOffset;
        viewer.ScrollToVerticalOffset(currentOffset);
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
    /// 供正文视口协调器识别“逻辑位置已提交、画面仍由 RenderTransform 过渡”的短窗口。
    /// </summary>
    internal static bool HasActiveCompositedTransition(
        ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return GetAnimationState(viewer)
                   ?.UsesCompositedTransition
               == true;
    }

    /// <summary>
    /// 暴露当前动画状态标识，供实窗验收确认连续滚轮复用同一会话。
    /// </summary>
    internal static object? GetActiveAnimationSession(ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return GetIsAnimationActive(viewer)
            ? GetAnimationState(viewer)
            : null;
    }

    /// <summary>
    /// 视口协调器每次真正完成扫描后统一回报位置，覆盖合成、逐帧、程序化定位和尺寸变化等所有入口。
    /// </summary>
    internal static void RecordDeferredViewportRefresh(
        ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        WheelAnimationState? state = GetAnimationState(viewer);
        if (state is null) return;

        double refreshOffset = GetIsAnimationActive(viewer)
                               && state.UsesCompositedTransition
            ? state.GetCurrentVisualOffset()
            : viewer.VerticalOffset;
        state.RecordDeferredViewportRefresh(refreshOffset);
    }

    private static void OnPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs eventArgs)
    {
        // AnimatedScrollViewer 由重写入口调用共享处理，避免类处理器和派生控件重复消费同一事件。
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

    private static void ApplyWheelPlan(
        ScrollViewer viewer,
        WheelAnimationState state,
        WheelMotionPlan plan)
    {
        SetTargetVerticalOffset(viewer, plan.TargetOffset);
        if (!plan.ShouldAnimate)
        {
            state.StopAndCommitVisualOffset();
            SetIsAnimationActive(viewer, false);
            viewer.ScrollToVerticalOffset(plan.TargetOffset);
            return;
        }

        SetIsAnimationActive(viewer, true);
        state.StartOrRetarget(plan);
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
        // 合成滚动会预提交逻辑目标，嵌套路由必须看屏幕当前视觉位置；没有会话时只信任原生偏移，避免陈旧附加目标反向跳动。
        WheelAnimationState? state = GetAnimationState(viewer);
        bool hasActiveMotion = GetIsAnimationActive(viewer)
                               && state is not null;
        double effectiveOffset = hasActiveMotion
            && state is not null
                ? state.GetCurrentVisualOffset()
                : viewer.VerticalOffset;
        return CanRouteWheel(
            viewer.ScrollableHeight,
            effectiveOffset,
            wheelDelta,
            hasActiveMotion);
    }

    /// <summary>
    /// 活动会话即使视觉位置仍贴着边界，也必须先接收反向输入以取消旧动量；会话结束后才恢复原生边界冒泡。
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

    /// <summary>
    /// 合成层先把逻辑视口提交到目标；向下动画显示目标之前的来路，必须使用前缓存，向上则使用后缓存。
    /// </summary>
    internal static double GetDirectionalCacheLength(
        VirtualizationCacheLength cacheLength,
        double currentVisualOffset,
        double targetOffset) =>
        targetOffset >= currentVisualOffset
            ? cacheLength.CacheBeforeViewport
            : cacheLength.CacheAfterViewport;

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
        private readonly EventHandler _telemetryRenderingHandler;
        private readonly ScrollFrameCadenceTracker _cadenceTracker = new();
        private bool _isRunning;
        private bool _isTelemetryRunning;
        private long _lastFrameTimestamp;
        private double _targetOffset;
        private double _velocity;
        private WheelInputMode _mode;
        private bool _hasWheelInput;
        private int _lastWheelDelta;
        private int _lastWheelTimestamp;
        private long _deferredViewportEvaluationBaseline;
        private double _lastDeferredViewportRefreshOffset = double.NaN;
        private UIElement? _compositedContent;
        private Transform? _originalRenderTransform;
        private TranslateTransform? _scrollTransform;
        private bool _usesCompositedTransition;

        internal WheelAnimationState(ScrollViewer viewer)
        {
            _viewer = viewer;
            _renderingHandler = OnRendering;
            _telemetryRenderingHandler = OnTelemetryRendering;
            _viewer.Unloaded += OnViewerUnloaded;
        }

        internal bool UsesCompositedTransition =>
            _isRunning && _usesCompositedTransition;

        internal WheelInputMode Mode => _mode;

        internal double Velocity => _velocity;

        internal WheelInputMode ClassifyInput(
            int wheelDelta,
            int timestamp)
        {
            TimeSpan elapsed = _hasWheelInput
                ? TimeSpan.FromMilliseconds(
                    unchecked((uint)(timestamp - _lastWheelTimestamp)))
                : TimeSpan.MaxValue;
            WheelInputMode mode = ClassifyWheelInput(
                wheelDelta,
                _lastWheelDelta,
                elapsed);
            _hasWheelInput = true;
            _lastWheelDelta = wheelDelta;
            _lastWheelTimestamp = timestamp;
            return mode;
        }

        internal void StartOrRetarget(WheelMotionPlan plan)
        {
            if (TryStartOrRetargetComposited(plan))
            {
                return;
            }

            if (_usesCompositedTransition)
            {
                StopAndCommitVisualOffset();
                // 跨出合成缓存后只做一次逻辑交接，后续仍由同一 Fluent 会话逐帧推进。
            }

            long now = Stopwatch.GetTimestamp();
            _targetOffset = plan.TargetOffset;
            _velocity = plan.Velocity;
            _mode = plan.Mode;
            if (_isRunning) return;

            _lastFrameTimestamp = now;
            _isRunning = true;
            StartTelemetry();
            CompositionTarget.Rendering += _renderingHandler;
        }

        internal double StopAndCommitVisualOffset()
        {
            if (_usesCompositedTransition)
            {
                double visualOffset = StopCompositedTransition();
                _viewer.ScrollToVerticalOffset(visualOffset);
                if (_viewer.IsLoaded)
                {
                    // ScrollViewer 可能延后应用命令；刷新视口前必须让逻辑偏移与刚清除的合成变换一致。
                    _viewer.UpdateLayout();
                }
                // 先提交屏幕真实位置并合并待处理刷新，再结束遥测；安全位移内仍不额外扫描全文。
                CompleteDeferredViewport(visualOffset);
                StopTelemetry();
                return visualOffset;
            }

            if (_isRunning)
            {
                CompositionTarget.Rendering -= _renderingHandler;
                _isRunning = false;
            }

            _velocity = 0d;
            StopTelemetry();
            return _viewer.VerticalOffset;
        }

        internal double GetCurrentVisualOffset()
        {
            double translation =
                _usesCompositedTransition
                && _compositedContent is not null
                && ReferenceEquals(
                    _compositedContent.RenderTransform,
                    _scrollTransform)
                    ? _scrollTransform?.Y ?? 0d
                    : 0d;
            return Math.Clamp(
                _viewer.VerticalOffset - translation,
                0d,
                NormalizeNonNegative(_viewer.ScrollableHeight));
        }

        private void OnRendering(object? sender, EventArgs eventArgs)
        {
            if (!_isRunning) return;

            if (_usesCompositedTransition)
            {
                AdvanceCompositedFrame();
                return;
            }

            long now = Stopwatch.GetTimestamp();
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
            WheelAnimationFrame frame = AdvanceCurrentFrame(
                _viewer.VerticalOffset,
                elapsed);
            _velocity = frame.Velocity;
            double nextOffset = Math.Clamp(
                frame.Offset,
                0d,
                maximumOffset);
            _viewer.ScrollToVerticalOffset(nextOffset);
            if (frame.IsComplete
                || Math.Abs(nextOffset - _targetOffset) < 0.05d)
            {
                Complete();
            }
        }

        private void OnViewerUnloaded(
            object sender,
            RoutedEventArgs eventArgs)
        {
            Cancel(_viewer);
            RestoreOriginalRenderTransform();
            _hasWheelInput = false;
        }

        private void Complete()
        {
            if (_usesCompositedTransition)
            {
                CompositionTarget.Rendering -= _renderingHandler;
                if (_compositedContent is not null
                    && _scrollTransform is not null
                    && ReferenceEquals(
                        _compositedContent.RenderTransform,
                        _scrollTransform))
                {
                    _scrollTransform.BeginAnimation(
                        TranslateTransform.YProperty,
                        null);
                    _scrollTransform.Y = 0d;
                }

                _isRunning = false;
                _usesCompositedTransition = false;
                _velocity = 0d;
                CompleteDeferredViewport(_targetOffset);
                StopTelemetry();
                SetTargetVerticalOffset(_viewer, _targetOffset);
                SetIsAnimationActive(_viewer, false);
                return;
            }

            StopOffsetTransition();
            StopTelemetry();
            _viewer.ScrollToVerticalOffset(_targetOffset);
            SetTargetVerticalOffset(_viewer, _targetOffset);
            SetIsAnimationActive(_viewer, false);
        }

        private void CompleteDeferredViewport(double visualOffset)
        {
            bool travelRequiresRefresh =
                ViewportDeferredContentControl
                    .ShouldRefreshCompositedViewport(
                        _lastDeferredViewportRefreshOffset,
                        visualOffset,
                        _viewer.ViewportHeight);
            ViewportDeferredContentControl.CompleteCompositedViewport(
                _viewer,
                travelRequiresRefresh);
        }

        private void AdvanceCompositedFrame()
        {
            TranslateTransform? transform = _scrollTransform;
            if (_compositedContent is null
                || transform is null
                || !ReferenceEquals(
                    _compositedContent.RenderTransform,
                    transform))
            {
                Complete();
                return;
            }

            long now = Stopwatch.GetTimestamp();
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
            double currentVisualOffset = GetCurrentVisualOffset();
            WheelAnimationFrame frame = AdvanceCurrentFrame(
                currentVisualOffset,
                elapsed);
            _velocity = frame.Velocity;
            double nextVisualOffset = Math.Clamp(
                frame.Offset,
                0d,
                maximumOffset);

            // 每帧只改合成变换，不触发布局；同一状态中的速度会自然跨越连续滚轮输入。
            transform.Y = _viewer.VerticalOffset - nextVisualOffset;
            if (frame.IsComplete
                || Math.Abs(nextVisualOffset - _targetOffset) < 0.05d)
            {
                Complete();
            }
        }

        private WheelAnimationFrame AdvanceCurrentFrame(
            double currentOffset,
            TimeSpan elapsed) =>
            _mode == WheelInputMode.Precision
                ? AdvancePrecisionFrame(
                    currentOffset,
                    _targetOffset,
                    elapsed)
                : AdvanceInertialFrame(
                    currentOffset,
                    _targetOffset,
                    _velocity,
                    elapsed);

        internal void RecordDeferredViewportRefresh(double visualOffset)
        {
            if (!double.IsFinite(visualOffset)) return;
            _lastDeferredViewportRefreshOffset = Math.Clamp(
                visualOffset,
                0d,
                NormalizeNonNegative(_viewer.ScrollableHeight));
        }

        private bool TryStartOrRetargetComposited(
            WheelMotionPlan plan)
        {
            double currentVisualOffset = GetCurrentVisualOffset();
            if (!TryGetCompositedTransform(
                    currentVisualOffset,
                    plan.TargetOffset,
                    out TranslateTransform transform))
            {
                return false;
            }

            if (_isRunning && !_usesCompositedTransition)
            {
                StopOffsetTransition();
            }

            double currentTranslation = _usesCompositedTransition
                ? transform.Y
                : 0d;
            bool continuesExistingSession =
                _isRunning && _usesCompositedTransition;
            if (!continuesExistingSession
                && !double.IsFinite(
                    _lastDeferredViewportRefreshOffset))
            {
                _lastDeferredViewportRefreshOffset =
                    currentVisualOffset;
            }
            currentVisualOffset = Math.Clamp(
                _viewer.VerticalOffset - currentTranslation,
                0d,
                NormalizeNonNegative(_viewer.ScrollableHeight));
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                null);
            transform.Y = plan.TargetOffset - currentVisualOffset;

            long now = Stopwatch.GetTimestamp();
            _targetOffset = plan.TargetOffset;
            _velocity = plan.Velocity;
            _mode = plan.Mode;
            _isRunning = true;
            _usesCompositedTransition = true;
            if (!continuesExistingSession)
            {
                _lastFrameTimestamp = now;
                CompositionTarget.Rendering += _renderingHandler;
                StartTelemetry();
            }

            // 逻辑位置预提交到解析得到的落点，后续每帧只调整合成位移，不重排长篇早报。
            _viewer.ScrollToVerticalOffset(plan.TargetOffset);
            return true;
        }

        private bool TryGetCompositedTransform(
            double currentVisualOffset,
            double targetOffset,
            out TranslateTransform transform)
        {
            transform = null!;
            if (_viewer.Content is not UIElement content
                || !CanUseCompositedTransition(
                    currentVisualOffset,
                    targetOffset))
            {
                return false;
            }

            if (ReferenceEquals(content, _compositedContent)
                && _scrollTransform is not null
                && ReferenceEquals(
                    content.RenderTransform,
                    _scrollTransform))
            {
                transform = _scrollTransform;
                return true;
            }

            RestoreOriginalRenderTransform();
            Transform originalTransform = content.RenderTransform;
            if (!ReferenceEquals(originalTransform, Transform.Identity))
            {
                return false;
            }

            _compositedContent = content;
            _originalRenderTransform = originalTransform;
            _scrollTransform = new TranslateTransform();
            content.RenderTransform = _scrollTransform;
            transform = _scrollTransform;
            return true;
        }

        private bool CanUseCompositedTransition(
            double currentVisualOffset,
            double targetOffset)
        {
            // 页面级物理滚动区始终可合成；模板内滚动区只对带前后缓存的像素虚拟列表启用，
            // 确保一次提交目标位置后，动画经过的项目仍处于已实现缓冲区中。
            if (!_viewer.CanContentScroll)
            {
                return _viewer.TemplatedParent is null;
            }

            ItemsControl? itemsControl = FindOwningItemsControl(_viewer);
            if (itemsControl is null
                || !VirtualizingPanel.GetIsVirtualizing(itemsControl)
                || VirtualizingPanel.GetVirtualizationMode(itemsControl)
                    != VirtualizationMode.Recycling
                || VirtualizingPanel.GetScrollUnit(itemsControl)
                    != ScrollUnit.Pixel)
            {
                return false;
            }

            VirtualizationCacheLength cacheLength =
                VirtualizingPanel.GetCacheLength(itemsControl);
            double directionalCache = GetDirectionalCacheLength(
                cacheLength,
                currentVisualOffset,
                targetOffset);
            double cacheCapacity =
                VirtualizingPanel.GetCacheLengthUnit(itemsControl) switch
                {
                    VirtualizationCacheLengthUnit.Page =>
                        directionalCache * Math.Max(
                            1d,
                            NormalizeNonNegative(_viewer.ViewportHeight)),
                    VirtualizationCacheLengthUnit.Pixel =>
                        directionalCache,
                    _ => 0d
                };
            return Math.Abs(targetOffset - currentVisualOffset)
                   <= cacheCapacity + 0.5d;
        }

        private static ItemsControl? FindOwningItemsControl(
            DependencyObject child)
        {
            for (DependencyObject? current = child;
                 current is not null;
                 current = GetParent(current))
            {
                if (current is ItemsControl itemsControl)
                {
                    return itemsControl;
                }
            }
            return null;
        }

        private double StopCompositedTransition()
        {
            TranslateTransform? transform = _scrollTransform;
            bool ownsActiveTransform =
                _compositedContent is not null
                && transform is not null
                && ReferenceEquals(
                    _compositedContent.RenderTransform,
                    transform);
            double currentTranslation =
                ownsActiveTransform ? transform!.Y : 0d;
            double currentVisualOffset = Math.Clamp(
                _viewer.VerticalOffset - currentTranslation,
                0d,
                NormalizeNonNegative(_viewer.ScrollableHeight));
            if (_isRunning)
            {
                CompositionTarget.Rendering -= _renderingHandler;
            }
            if (ownsActiveTransform)
            {
                transform!.BeginAnimation(
                    TranslateTransform.YProperty,
                    null);
                transform.Y = 0d;
            }

            _isRunning = false;
            _usesCompositedTransition = false;
            _velocity = 0d;
            return currentVisualOffset;
        }

        private void StopOffsetTransition()
        {
            if (_isRunning && !_usesCompositedTransition)
            {
                CompositionTarget.Rendering -= _renderingHandler;
            }

            _isRunning = false;
            _velocity = 0d;
        }

        private void StartTelemetry()
        {
            if (_isTelemetryRunning) return;
            _cadenceTracker.Reset();
            _deferredViewportEvaluationBaseline =
                ViewportDeferredContentControl
                    .GetDeferredViewportEvaluationCount(_viewer);
            _isTelemetryRunning = true;
            CompositionTarget.Rendering +=
                _telemetryRenderingHandler;
        }

        private void OnTelemetryRendering(
            object? sender,
            EventArgs eventArgs)
        {
            if (!_isTelemetryRunning) return;
            if (_usesCompositedTransition)
            {
                double currentVisualOffset = GetCurrentVisualOffset();
                if (ViewportDeferredContentControl
                    .ShouldRefreshCompositedViewport(
                        _lastDeferredViewportRefreshOffset,
                        currentVisualOffset,
                        _viewer.ViewportHeight))
                {
                    // 长距离滚动按安全位移采样，避免 Rendering 每帧遍历整篇早报。
                    ViewportDeferredContentControl
                        .RefreshCompositedViewport(_viewer);
                }
            }
            TimeSpan renderingTime =
                eventArgs is RenderingEventArgs renderingEventArgs
                    ? renderingEventArgs.RenderingTime
                    : Stopwatch.GetElapsedTime(0L);
            _cadenceTracker.RecordFrame(renderingTime);
        }

        private void StopTelemetry()
        {
            if (!_isTelemetryRunning) return;
            CompositionTarget.Rendering -=
                _telemetryRenderingHandler;
            _isTelemetryRunning = false;
            long currentEvaluationCount =
                ViewportDeferredContentControl
                    .GetDeferredViewportEvaluationCount(_viewer);
            long evaluationDelta = Math.Max(
                0L,
                currentEvaluationCount
                - _deferredViewportEvaluationBaseline);
            int sessionEvaluationCount = evaluationDelta >= int.MaxValue
                ? int.MaxValue
                : (int)evaluationDelta;
            ScrollFrameTelemetry.Publish(
                _viewer,
                _cadenceTracker.Complete(
                    deferredViewportEvaluationCount:
                        sessionEvaluationCount));
        }

        private void RestoreOriginalRenderTransform()
        {
            StopTelemetry();
            if (_usesCompositedTransition)
            {
                StopCompositedTransition();
            }
            else
            {
                StopOffsetTransition();
            }

            if (_compositedContent is not null
                && _scrollTransform is not null
                && ReferenceEquals(
                    _compositedContent.RenderTransform,
                    _scrollTransform))
            {
                _scrollTransform.BeginAnimation(
                    TranslateTransform.YProperty,
                    null);
                _compositedContent.RenderTransform =
                    _originalRenderTransform ?? Transform.Identity;
            }

            _compositedContent = null;
            _originalRenderTransform = null;
            _scrollTransform = null;
            _isRunning = false;
            _usesCompositedTransition = false;
            _velocity = 0d;
            _lastDeferredViewportRefreshOffset = double.NaN;
        }
    }
}

/// <summary>
/// 描述单个真实渲染帧产生的偏移与速度。
/// </summary>
internal readonly record struct WheelAnimationFrame(
    double Offset,
    double Velocity,
    bool IsComplete = false);

/// <summary>
/// 描述当前滚轮手势采用鼠标惯性还是触控板精确跟随。
/// </summary>
internal enum WheelInputMode
{
    Inertial,
    Precision
}

/// <summary>
/// 描述输入合并后的运动状态；TargetOffset 同时是合成层预提交位置和最终精确落点。
/// </summary>
internal readonly record struct WheelMotionPlan(
    WheelInputMode Mode,
    double TargetOffset,
    double Velocity,
    bool ShouldAnimate);
