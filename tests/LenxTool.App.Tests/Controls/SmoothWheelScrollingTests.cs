using LenxTool.App.Controls;

namespace LenxTool.App.Tests.Controls;

/// <summary>
/// 冻结全局滚轮采用的 Fluent 惯性模型、精确输入模型及合成视口刷新边界。
/// </summary>
public sealed class SmoothWheelScrollingTests
{
    [Fact]
    public void StandardMouseWheelUsesFluentInertiaAndProjectsItsLandingOffset()
    {
        WheelMotionPlan plan = SmoothWheelScrolling.CreateWheelMotionPlan(
            currentOffset: 100d,
            pendingTargetOffset: 100d,
            currentVelocity: 0d,
            scrollableHeight: 1000d,
            viewportHeight: 500d,
            wheelDelta: -120,
            systemWheelLines: 3,
            usesLogicalUnits: false,
            motionAllowed: true,
            WheelInputMode.Inertial);

        Assert.Equal(WheelInputMode.Inertial, plan.Mode);
        Assert.Equal(240d, plan.Velocity, precision: 6);
        Assert.Equal(215d, plan.TargetOffset, precision: 6);
        Assert.True(plan.ShouldAnimate);
    }

    [Fact]
    public void RepeatedMouseWheelAddsVelocityInsteadOfRestartingAFixedAnimation()
    {
        WheelMotionPlan plan = SmoothWheelScrolling.CreateWheelMotionPlan(
            currentOffset: 109.2d,
            pendingTargetOffset: 215d,
            currentVelocity: 220.8d,
            scrollableHeight: 1000d,
            viewportHeight: 500d,
            wheelDelta: -120,
            systemWheelLines: 3,
            usesLogicalUnits: false,
            motionAllowed: true,
            WheelInputMode.Inertial);

        Assert.Equal(460.8d, plan.Velocity, precision: 6);
        Assert.Equal(330d, plan.TargetOffset, precision: 6);
        Assert.True(plan.ShouldAnimate);
    }

    [Theory]
    [InlineData(-30, -120, 80, 1)]
    [InlineData(-120, -30, 80, 1)]
    [InlineData(-120, -30, 100, 0)]
    [InlineData(-120, -120, 20, 0)]
    public void HighResolutionDeltaSelectsPrecisionInputWithoutMisclassifyingMouseNotches(
        int wheelDelta,
        int previousWheelDelta,
        int elapsedMilliseconds,
        int expectedMode)
    {
        WheelInputMode actual = SmoothWheelScrolling.ClassifyWheelInput(
            wheelDelta,
            previousWheelDelta,
            TimeSpan.FromMilliseconds(elapsedMilliseconds));

        Assert.Equal((WheelInputMode)expectedMode, actual);
    }

    [Fact]
    public void PrecisionInputAccumulatesPendingDeltaAndClearsMouseVelocity()
    {
        WheelMotionPlan plan = SmoothWheelScrolling.CreateWheelMotionPlan(
            currentOffset: 100d,
            pendingTargetOffset: 112d,
            currentVelocity: 180d,
            scrollableHeight: 1000d,
            viewportHeight: 500d,
            wheelDelta: -30,
            systemWheelLines: 3,
            usesLogicalUnits: false,
            motionAllowed: true,
            WheelInputMode.Precision);

        Assert.Equal(142d, plan.TargetOffset, precision: 6);
        Assert.Equal(0d, plan.Velocity);
        Assert.True(plan.ShouldAnimate);
    }

    [Fact]
    public void OneReferenceFrameMatchesFluentMouseFrictionExactly()
    {
        WheelAnimationFrame frame = SmoothWheelScrolling.AdvanceInertialFrame(
            currentOffset: 100d,
            targetOffset: 215d,
            currentVelocity: 240d,
            TimeSpan.FromSeconds(1d / 144d));

        Assert.Equal(109.2d, frame.Offset, precision: 3);
        Assert.Equal(220.8d, frame.Velocity, precision: 3);
        Assert.False(frame.IsComplete);
    }

    [Fact]
    public void InertialIntegrationLandsConsistentlyAcrossRefreshRates()
    {
        WheelAnimationFrame sixtyHertz = AdvanceInertialFrames(
            frameCount: 36,
            TimeSpan.FromSeconds(1d / 60d));
        WheelAnimationFrame oneHundredFortyFourHertz = AdvanceInertialFrames(
            frameCount: 87,
            TimeSpan.FromSeconds(1d / 144d));

        Assert.InRange(
            Math.Abs(sixtyHertz.Offset - oneHundredFortyFourHertz.Offset),
            0d,
            0.05d);
        Assert.InRange(
            Math.Abs(sixtyHertz.Velocity - oneHundredFortyFourHertz.Velocity),
            0d,
            0.1d);
    }

    [Fact]
    public void PrecisionFrameRemovesHalfTheRemainingDistanceAt144Hertz()
    {
        WheelAnimationFrame frame = SmoothWheelScrolling.AdvancePrecisionFrame(
            currentOffset: 0d,
            targetOffset: 120d,
            TimeSpan.FromSeconds(1d / 144d));

        Assert.Equal(60d, frame.Offset, precision: 3);
        Assert.Equal(0d, frame.Velocity);
        Assert.False(frame.IsComplete);
    }

    [Fact]
    public void ReducedMotionKeepsTheFluentLandingDistanceButCommitsImmediately()
    {
        WheelMotionPlan plan = SmoothWheelScrolling.CreateWheelMotionPlan(
            currentOffset: 100d,
            pendingTargetOffset: 100d,
            currentVelocity: 0d,
            scrollableHeight: 1000d,
            viewportHeight: 500d,
            wheelDelta: -120,
            systemWheelLines: 3,
            usesLogicalUnits: false,
            motionAllowed: false,
            WheelInputMode.Inertial);

        Assert.Equal(215d, plan.TargetOffset, precision: 6);
        Assert.Equal(0d, plan.Velocity);
        Assert.False(plan.ShouldAnimate);
    }

    [Fact]
    public void OppositeMouseNotchDropsOldMomentumAndReversesImmediately()
    {
        WheelMotionPlan plan = SmoothWheelScrolling.CreateWheelMotionPlan(
            currentOffset: 300d,
            pendingTargetOffset: 530d,
            currentVelocity: 480d,
            scrollableHeight: 1000d,
            viewportHeight: 500d,
            wheelDelta: 120,
            systemWheelLines: 3,
            usesLogicalUnits: false,
            motionAllowed: true,
            WheelInputMode.Inertial);

        Assert.Equal(185d, plan.TargetOffset, precision: 6);
        Assert.Equal(-240d, plan.Velocity, precision: 6);
        Assert.True(plan.ShouldAnimate);
    }

    [Fact]
    public void LogicalScrollingKeepsSystemLineSensitivityWithoutPixelImpulse()
    {
        WheelMotionPlan plan = SmoothWheelScrolling.CreateWheelMotionPlan(
            currentOffset: 10d,
            pendingTargetOffset: 10d,
            currentVelocity: 0d,
            scrollableHeight: 100d,
            viewportHeight: 12d,
            wheelDelta: -120,
            systemWheelLines: 3,
            usesLogicalUnits: true,
            motionAllowed: true,
            WheelInputMode.Inertial);

        Assert.Equal(14.35d, plan.TargetOffset, precision: 3);
        Assert.True(plan.ShouldAnimate);
    }

    [Fact]
    public void InertialFrameStopsExactlyAtClampedBoundary()
    {
        WheelAnimationFrame frame = SmoothWheelScrolling.AdvanceInertialFrame(
            currentOffset: 98d,
            targetOffset: 100d,
            currentVelocity: 240d,
            TimeSpan.FromSeconds(1d / 144d));

        Assert.Equal(100d, frame.Offset);
        Assert.Equal(0d, frame.Velocity);
        Assert.True(frame.IsComplete);
    }

    [Fact]
    public void PrecommittedVirtualizedViewportKeepsTheIncomingCacheDirection()
    {
        var cacheLength = new System.Windows.Controls.VirtualizationCacheLength(
            cacheBeforeViewport: 1d,
            cacheAfterViewport: 4d);

        Assert.Equal(
            1d,
            SmoothWheelScrolling.GetDirectionalCacheLength(
                cacheLength,
                currentVisualOffset: 100d,
                targetOffset: 200d));
        Assert.Equal(
            4d,
            SmoothWheelScrolling.GetDirectionalCacheLength(
                cacheLength,
                currentVisualOffset: 200d,
                targetOffset: 100d));
    }

    [Theory]
    [InlineData(1000d, 0d, 120, false, false)]
    [InlineData(1000d, 1000d, -120, false, false)]
    [InlineData(1000d, 0d, -120, false, true)]
    [InlineData(1000d, 1000d, 120, false, true)]
    [InlineData(1000d, 0d, 120, true, true)]
    [InlineData(1000d, 1000d, -120, true, true)]
    public void ActiveMotionConsumesBoundaryInputBeforeNativeBubblingResumes(
        double scrollableHeight,
        double effectiveOffset,
        int wheelDelta,
        bool hasActiveMotion,
        bool expected)
    {
        Assert.Equal(
            expected,
            SmoothWheelScrolling.CanRouteWheel(
                scrollableHeight,
                effectiveOffset,
                wheelDelta,
                hasActiveMotion));
    }

    [Theory]
    [InlineData(0d, 119.99d, 520d, false)]
    [InlineData(0d, 120d, 520d, true)]
    [InlineData(0d, 49.99d, 200d, false)]
    [InlineData(0d, 50d, 200d, true)]
    [InlineData(200d, 150.01d, 200d, false)]
    [InlineData(200d, 150d, 200d, true)]
    public void CompositedViewportRefreshUsesQuarterViewportCappedAt120Pixels(
        double lastRefreshOffset,
        double currentVisualOffset,
        double viewportHeight,
        bool expected)
    {
        bool actual = ViewportDeferredContentControl
            .ShouldRefreshCompositedViewport(
                lastRefreshOffset,
                currentVisualOffset,
                viewportHeight);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void InvalidCompositedOffsetsForceAViewportRefresh()
    {
        Assert.True(ViewportDeferredContentControl
            .ShouldRefreshCompositedViewport(
                double.NaN,
                currentVisualOffset: 20d,
                viewportHeight: 520d));
        Assert.True(ViewportDeferredContentControl
            .ShouldRefreshCompositedViewport(
                lastRefreshOffset: 0d,
                double.PositiveInfinity,
                viewportHeight: 520d));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void EmptyOrInvalidViewportUsesOnePixelRefreshFloor(
        double viewportHeight)
    {
        Assert.False(ViewportDeferredContentControl
            .ShouldRefreshCompositedViewport(
                lastRefreshOffset: 0d,
                currentVisualOffset: 0.99d,
                viewportHeight));
        Assert.True(ViewportDeferredContentControl
            .ShouldRefreshCompositedViewport(
                lastRefreshOffset: 0d,
                currentVisualOffset: 1d,
                viewportHeight));
    }

    [Fact]
    public void PagedListKeepsAPrefetchBufferAfterSwitchingToPixelUnits()
    {
        Assert.InRange(
            PagedListBox.DefaultLoadMoreThreshold,
            160d,
            320d);
    }

    private static WheelAnimationFrame AdvanceInertialFrames(
        int frameCount,
        TimeSpan frameInterval)
    {
        var frame = new WheelAnimationFrame(100d, 240d);
        for (int index = 0; index < frameCount; index++)
        {
            frame = SmoothWheelScrolling.AdvanceInertialFrame(
                frame.Offset,
                targetOffset: 215d,
                frame.Velocity,
                frameInterval);
        }

        return frame;
    }
}
