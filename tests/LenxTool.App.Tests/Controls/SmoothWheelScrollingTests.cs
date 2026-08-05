using LenxTool.App.Controls;

namespace LenxTool.App.Tests.Controls;

/// <summary>
/// 冻结 TwilightLemon/FluentScrollViewer 的实际滚轮公式与状态语义。
/// </summary>
public sealed class SmoothWheelScrollingTests
{
    private static readonly TimeSpan ReferenceFrame =
        TimeSpan.FromSeconds(1d / 144d);

    [Theory]
    [InlineData(-30, -120, 80, 1)]
    [InlineData(-120, -30, 80, 1)]
    [InlineData(-120, -30, 100, 0)]
    [InlineData(-120, -120, 20, 0)]
    [InlineData(-120, -30, -1, 1)]
    public void TouchpadHeuristicMatchesUpstreamExactly(
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

    [Theory]
    [InlineData(false, true, 3, false, true)]
    [InlineData(true, true, 3, false, false)]
    [InlineData(false, false, 3, false, false)]
    [InlineData(false, true, 0, false, false)]
    [InlineData(false, true, 3, true, false)]
    public void HostPolicyOnlyEnablesUpstreamMotionWhenAnimationIsAllowed(
        bool hasShiftModifier,
        bool clientAreaAnimation,
        int wheelScrollLines,
        bool reduceMotion,
        bool expected)
    {
        Assert.Equal(
            expected,
            SmoothWheelScrolling.ShouldUseUpstreamMotion(
                hasShiftModifier,
                clientAreaAnimation,
                wheelScrollLines,
                reduceMotion));
    }

    [Fact]
    public void StandardMouseNotchAddsTheUpstreamVelocityImpulse()
    {
        WheelMotionPlan plan = SmoothWheelScrolling.ApplyUpstreamWheelInput(
            currentOffset: 100d,
            currentVelocity: 0d,
            scrollableHeight: 1000d,
            wheelDelta: -120,
            WheelInputMode.Inertial);

        Assert.Equal(WheelInputMode.Inertial, plan.Mode);
        Assert.Equal(100d, plan.TargetOffset);
        Assert.Equal(240d, plan.Velocity);
        Assert.True(plan.ShouldAnimate);
    }

    [Fact]
    public void RepeatedMouseNotchAddsVelocityWithoutProjectingALandingOffset()
    {
        WheelMotionPlan plan = SmoothWheelScrolling.ApplyUpstreamWheelInput(
            currentOffset: 109.2d,
            currentVelocity: 220.8d,
            scrollableHeight: 1000d,
            wheelDelta: -120,
            WheelInputMode.Inertial);

        Assert.Equal(109.2d, plan.TargetOffset);
        Assert.Equal(460.8d, plan.Velocity, precision: 6);
    }

    [Fact]
    public void OppositeNotchFirstCancelsExistingMomentum()
    {
        WheelMotionPlan plan = SmoothWheelScrolling.ApplyUpstreamWheelInput(
            currentOffset: 300d,
            currentVelocity: 480d,
            scrollableHeight: 1000d,
            wheelDelta: 120,
            WheelInputMode.Inertial);

        Assert.Equal(240d, plan.Velocity, precision: 6);
    }

    [Fact]
    public void PrecisionInputUsesTheLiveOffsetInsteadOfAccumulatingOldTarget()
    {
        WheelMotionPlan plan = SmoothWheelScrolling.ApplyUpstreamWheelInput(
            currentOffset: 100d,
            currentVelocity: 180d,
            scrollableHeight: 1000d,
            wheelDelta: -30,
            WheelInputMode.Precision);

        Assert.Equal(130d, plan.TargetOffset);
        Assert.Equal(0d, plan.Velocity);
    }

    [Fact]
    public void OneReferenceFrameMatchesUpstreamMouseFormula()
    {
        WheelAnimationFrame frame =
            SmoothWheelScrolling.AdvanceUpstreamInertialFrame(
                currentOffset: 100d,
                currentVelocity: 240d,
                scrollableHeight: 1000d,
                ReferenceFrame);

        Assert.Equal(109.2d, frame.Offset, precision: 3);
        Assert.Equal(220.8d, frame.Velocity, precision: 3);
        Assert.False(frame.IsComplete);
    }

    [Fact]
    public void LongFrameIsNotArtificiallyCapped()
    {
        TimeSpan frameInterval = TimeSpan.FromSeconds(0.1d);
        double timeFactor = frameInterval.TotalSeconds * 144d;
        double expectedVelocity = 240d * Math.Pow(0.92d, timeFactor);
        double expectedOffset =
            100d + expectedVelocity * (timeFactor / 24d);

        WheelAnimationFrame frame =
            SmoothWheelScrolling.AdvanceUpstreamInertialFrame(
                currentOffset: 100d,
                currentVelocity: 240d,
                scrollableHeight: 1000d,
                frameInterval);

        Assert.Equal(expectedVelocity, frame.Velocity, precision: 6);
        Assert.Equal(expectedOffset, frame.Offset, precision: 6);
        Assert.False(frame.IsComplete);
    }

    [Fact]
    public void MouseVelocityKeepsDecayingAtTheBoundary()
    {
        WheelAnimationFrame frame =
            SmoothWheelScrolling.AdvanceUpstreamInertialFrame(
                currentOffset: 98d,
                currentVelocity: 240d,
                scrollableHeight: 100d,
                ReferenceFrame);

        Assert.Equal(100d, frame.Offset);
        Assert.Equal(220.8d, frame.Velocity, precision: 3);
        Assert.False(frame.IsComplete);
    }

    [Fact]
    public void MouseSessionStopsBeforeAdvancingWhenVelocityIsBelowThreshold()
    {
        WheelAnimationFrame frame =
            SmoothWheelScrolling.AdvanceUpstreamInertialFrame(
                currentOffset: 100d,
                currentVelocity: 0.09d,
                scrollableHeight: 1000d,
                ReferenceFrame);

        Assert.Equal(100d, frame.Offset);
        Assert.Equal(0d, frame.Velocity);
        Assert.True(frame.IsComplete);
    }

    [Fact]
    public void PrecisionFrameClosesHalfTheDistanceAt144Hertz()
    {
        WheelAnimationFrame frame =
            SmoothWheelScrolling.AdvanceUpstreamPrecisionFrame(
                currentOffset: 100d,
                targetOffset: 130d,
                ReferenceFrame);

        Assert.Equal(115d, frame.Offset, precision: 3);
        Assert.Equal(0d, frame.Velocity);
        Assert.False(frame.IsComplete);
    }

    [Fact]
    public void PrecisionFrameSnapsWhenThePostLerpGapIsBelowHalfAPixel()
    {
        WheelAnimationFrame frame =
            SmoothWheelScrolling.AdvanceUpstreamPrecisionFrame(
                currentOffset: 129.2d,
                targetOffset: 130d,
                ReferenceFrame);

        Assert.Equal(130d, frame.Offset);
        Assert.True(frame.IsComplete);
    }

    [Fact]
    public void PagedListKeepsAPrefetchBufferAfterSwitchingToPixelUnits()
    {
        Assert.InRange(
            PagedListBox.DefaultLoadMoreThreshold,
            160d,
            320d);
    }
}
