using LenxTool.App.Controls;

namespace LenxTool.App.Tests.Controls;

/// <summary>
/// 冻结全局滚轮与每日早报一致的灵敏度，以及平滑过渡的降级边界。
/// </summary>
public sealed class SmoothWheelScrollingTests
{
    [Fact]
    public void PhysicalWheelStepMatchesDailyBriefingAndUsesModernTransition()
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
        Assert.True(plan.ShouldAnimate);
        Assert.InRange(plan.Duration.TotalMilliseconds, 160d, 220d);
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
        Assert.True(plan.ShouldAnimate);
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
    public void PagedListKeepsAPrefetchBufferAfterSwitchingToPixelUnits()
    {
        Assert.InRange(
            PagedListBox.DefaultLoadMoreThreshold,
            160d,
            320d);
    }
}
