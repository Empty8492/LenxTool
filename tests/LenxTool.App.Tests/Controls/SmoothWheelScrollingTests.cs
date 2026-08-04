using LenxTool.App.Controls;

namespace LenxTool.App.Tests.Controls;

/// <summary>
/// 冻结全局滚轮与每日早报一致的灵敏度，以及即时提交边界。
/// </summary>
public sealed class SmoothWheelScrollingTests
{
    [Fact]
    public void PhysicalWheelStepMatchesDailyBriefingAndCommitsWithoutTransition()
    {
        WheelScrollPlan plan = SmoothWheelScrolling.CreateWheelPlan(
            currentOffset: 100d,
            targetOffset: 100d,
            scrollableHeight: 1000d,
            viewportHeight: 500d,
            wheelDelta: -120,
            systemWheelLines: 3,
            usesLogicalUnits: false,
            motionAllowed: true);

        Assert.Equal(169.6d, plan.TargetOffset, precision: 3);
        Assert.False(plan.ShouldAnimate);
        Assert.Equal(TimeSpan.Zero, plan.Duration);
    }

    [Fact]
    public void RepeatedWheelInputAccumulatesFromPendingTargetAndClamps()
    {
        WheelScrollPlan plan = SmoothWheelScrolling.CreateWheelPlan(
            currentOffset: 120d,
            targetOffset: 169.6d,
            scrollableHeight: 210d,
            viewportHeight: 500d,
            wheelDelta: -120,
            systemWheelLines: 3,
            usesLogicalUnits: false,
            motionAllowed: true);

        Assert.Equal(210d, plan.TargetOffset);
        Assert.False(plan.ShouldAnimate);
    }

    [Fact]
    public void LogicalScrollingKeepsTheSameMultiplierWithoutPixelAssumptions()
    {
        WheelScrollPlan plan = SmoothWheelScrolling.CreateWheelPlan(
            currentOffset: 10d,
            targetOffset: 10d,
            scrollableHeight: 100d,
            viewportHeight: 12d,
            wheelDelta: -120,
            systemWheelLines: 3,
            usesLogicalUnits: true,
            motionAllowed: true);

        Assert.Equal(14.35d, plan.TargetOffset, precision: 3);
    }

    [Fact]
    public void ReducedMotionKeepsSensitivityButAppliesTargetImmediately()
    {
        WheelScrollPlan plan = SmoothWheelScrolling.CreateWheelPlan(
            currentOffset: 100d,
            targetOffset: 100d,
            scrollableHeight: 1000d,
            viewportHeight: 500d,
            wheelDelta: -120,
            systemWheelLines: 3,
            usesLogicalUnits: false,
            motionAllowed: false);

        Assert.Equal(169.6d, plan.TargetOffset, precision: 3);
        Assert.False(plan.ShouldAnimate);
        Assert.Equal(TimeSpan.Zero, plan.Duration);
    }

    [Fact]
    public void ContinuousFrameEnginePreservesMomentumWhenWheelTargetExpands()
    {
        TimeSpan frameInterval = TimeSpan.FromMilliseconds(16d);
        TimeSpan responseDuration = TimeSpan.FromMilliseconds(180d);
        WheelAnimationFrame first = SmoothWheelScrolling.AdvanceFrame(
            currentOffset: 0d,
            targetOffset: 69.6d,
            currentVelocity: 0d,
            frameInterval,
            responseDuration);
        WheelAnimationFrame second = SmoothWheelScrolling.AdvanceFrame(
            first.Offset,
            targetOffset: 69.6d,
            first.Velocity,
            frameInterval,
            responseDuration);
        WheelAnimationFrame afterRetarget = SmoothWheelScrolling.AdvanceFrame(
            second.Offset,
            targetOffset: 139.2d,
            second.Velocity,
            frameInterval,
            responseDuration);

        Assert.True(first.Offset > 0d);
        Assert.True(second.Offset > first.Offset);
        Assert.True(afterRetarget.Offset > second.Offset);
        Assert.True(afterRetarget.Velocity >= second.Velocity);
    }

    [Fact]
    public void ContinuousFrameEngineIsStableAcrossDifferentRenderIntervals()
    {
        TimeSpan responseDuration = TimeSpan.FromMilliseconds(180d);
        WheelAnimationFrame sixtyHertz = AdvanceFrames(
            frameCount: 10,
            TimeSpan.FromMilliseconds(16d),
            responseDuration);
        WheelAnimationFrame oneHundredTwentyHertz = AdvanceFrames(
            frameCount: 20,
            TimeSpan.FromMilliseconds(8d),
            responseDuration);

        Assert.InRange(
            Math.Abs(sixtyHertz.Offset - oneHundredTwentyHertz.Offset),
            0d,
            0.5d);
        Assert.InRange(
            Math.Abs(sixtyHertz.Velocity - oneHundredTwentyHertz.Velocity),
            0d,
            5d);
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

    private static WheelAnimationFrame AdvanceFrames(
        int frameCount,
        TimeSpan frameInterval,
        TimeSpan responseDuration)
    {
        var frame = new WheelAnimationFrame(0d, 0d);
        for (int index = 0; index < frameCount; index++)
        {
            frame = SmoothWheelScrolling.AdvanceFrame(
                frame.Offset,
                targetOffset: 300d,
                frame.Velocity,
                frameInterval,
                responseDuration);
        }

        return frame;
    }
}
