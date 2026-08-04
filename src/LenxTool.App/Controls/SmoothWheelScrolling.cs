using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LenxTool.App.Controls;

/// <summary>
/// 为所有原生 ScrollViewer 提供统一的滚轮灵敏度和目标累计；滚轮位置立即提交。
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

        // 鼠标滚轮必须跟随输入立即落位；保留目标累计和灵敏度，但不再制造视觉拖尾。
        return new(target, TimeSpan.Zero);
    }

    /// <summary>
    /// 按一个真实渲染帧推进持久滚轮动画；临界阻尼使连续输入扩展目标时速度保持连续。
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

        WheelAnimationState? activeState =
            GetIsAnimationActive(viewer)
                ? GetAnimationState(viewer)
                : null;
        double pendingTarget = GetTargetVerticalOffset(viewer);
        double currentOffset = viewer.VerticalOffset;
        if (activeState is not null)
        {
            double visualOffset =
                activeState.GetCurrentVisualOffset();
            // 目标可以连续累计，但响应时长必须按屏幕当前真实位置计算，否则每次输入都会错误地当成全新单格。
            currentOffset = visualOffset;
            int incomingDirection = Math.Sign(-eventArgs.Delta);
            int activeDirection = Math.Sign(
                pendingTarget - visualOffset);
            if (incomingDirection != 0
                && activeDirection != 0
                && incomingDirection != activeDirection)
            {
                // 反向滚轮从屏幕当前真实位置重新规划，不能继续沿用旧目标累计。
                Cancel(viewer);
                // WPF 可能延后应用 ScrollTo；反向接管仅发生一次，这里同步提交可避免新动画从旧目标起步。
                viewer.UpdateLayout();
                currentOffset = visualOffset;
                pendingTarget = visualOffset;
            }
        }
        WheelScrollPlan plan = CreateWheelPlan(
            currentOffset,
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
    /// 程序化定位与滚轮保持一致，直接提交目标位置。
    /// </summary>
    internal static void ScrollToSmoothly(
        ScrollViewer viewer,
        double targetOffset,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        double normalizedTarget = Math.Clamp(
            NormalizeNonNegative(targetOffset),
            0d,
            NormalizeNonNegative(viewer.ScrollableHeight));
        // 阅读页不再使用自定义滚动补间；调用方保留参数以兼容现有命令入口。
        TimeSpan normalizedDuration = TimeSpan.Zero;
        ApplyPlan(
            viewer,
            new WheelScrollPlan(
                normalizedTarget,
                normalizedDuration));
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
            GetAnimationState(viewer)?.StopAndCommitVisualOffset();
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
        // 即时滚动在下一次布局前可能尚未更新 VerticalOffset，使用同步维护的目标位置判断方向。
        double effectiveOffset = GetTargetVerticalOffset(viewer);
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
        private readonly EventHandler _telemetryRenderingHandler;
        private readonly ScrollFrameCadenceTracker _cadenceTracker = new();
        private bool _isRunning;
        private bool _isTelemetryRunning;
        private long _lastFrameTimestamp;
        private long _completionTimestamp;
        private double _targetOffset;
        private double _velocity;
        private TimeSpan _responseDuration;
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

        internal void StartOrRetarget(
            double targetOffset,
            TimeSpan responseDuration)
        {
            if (TryStartOrRetargetComposited(
                    targetOffset,
                    responseDuration))
            {
                return;
            }

            if (_usesCompositedTransition)
            {
                double visualOffset = StopCompositedTransition();
                _viewer.ScrollToVerticalOffset(visualOffset);
                // 跨出虚拟缓存后只发生一次同步交接，确保逐帧路径从当前画面继续。
                _viewer.UpdateLayout();
                StopTelemetry();
            }

            long now = Stopwatch.GetTimestamp();
            double currentOffset = _viewer.VerticalOffset;
            if (_isRunning
                && (targetOffset - currentOffset) * _velocity < 0d)
            {
                // 反向输入立即清除旧方向动量，避免画面继续偏离用户的新目标。
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
            StartTelemetry();
            CompositionTarget.Rendering += _renderingHandler;
        }

        internal double StopAndCommitVisualOffset()
        {
            if (_usesCompositedTransition)
            {
                double visualOffset = StopCompositedTransition();
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
            RestoreOriginalRenderTransform();
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
                ViewportDeferredContentControl
                    .RefreshCompositedViewport(_viewer);
                _lastDeferredViewportRefreshOffset = _targetOffset;
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
            double currentVisualOffset = GetCurrentVisualOffset();
            WheelAnimationFrame frame = AdvanceFrame(
                currentVisualOffset,
                _targetOffset,
                _velocity,
                elapsed,
                _responseDuration);
            _velocity = frame.Velocity;
            double nextVisualOffset = Math.Clamp(
                frame.Offset,
                0d,
                maximumOffset);

            // 每帧只改合成变换，不触发布局；同一状态中的速度会自然跨越连续滚轮输入。
            transform.Y = _viewer.VerticalOffset - nextVisualOffset;
            if (Math.Abs(nextVisualOffset - _targetOffset) < 0.05d)
            {
                Complete();
            }
        }

        private bool TryStartOrRetargetComposited(
            double targetOffset,
            TimeSpan responseDuration)
        {
            double currentVisualOffset = GetCurrentVisualOffset();
            if (!TryGetCompositedTransform(
                    currentVisualOffset,
                    targetOffset,
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
            if (!continuesExistingSession)
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
            double startingTranslation = targetOffset
                                         - currentVisualOffset;
            transform.Y = startingTranslation;
            long now = Stopwatch.GetTimestamp();
            if (continuesExistingSession
                && (targetOffset - currentVisualOffset) * _velocity < 0d)
            {
                // 反向目标不能沿用旧方向速度，否则第一帧会继续背离用户输入。
                _velocity = 0d;
            }
            _targetOffset = targetOffset;
            _responseDuration = responseDuration;
            _completionTimestamp = now
                                   + (long)(responseDuration.TotalSeconds
                                            * Stopwatch.Frequency);
            _isRunning = true;
            _usesCompositedTransition = true;
            if (!continuesExistingSession)
            {
                _velocity = 0d;
                _lastFrameTimestamp = now;
                CompositionTarget.Rendering += _renderingHandler;
                StartTelemetry();
            }

            // 先一次性提交真实滚动位置，再只动画内容的渲染变换。
            // RenderTransform 不触发布局，复杂页面不会再为每个动画帧重排视觉树。
            _viewer.ScrollToVerticalOffset(targetOffset);
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
            double directionalCache =
                targetOffset >= currentVisualOffset
                    ? cacheLength.CacheBeforeViewport
                    : cacheLength.CacheAfterViewport;
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
                    _lastDeferredViewportRefreshOffset =
                        currentVisualOffset;
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
/// 描述单次滚轮输入的最终目标与过渡时长。
/// </summary>
internal readonly record struct WheelScrollPlan(
    double TargetOffset,
    TimeSpan Duration)
{
    public bool ShouldAnimate => Duration > TimeSpan.Zero;
}

/// <summary>
/// 描述单个真实渲染帧产生的偏移与速度。
/// </summary>
internal readonly record struct WheelAnimationFrame(
    double Offset,
    double Velocity);
