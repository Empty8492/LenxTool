using LenxTool.App.Controls;

namespace LenxTool.App.Tests.Controls;

/// <summary>
/// 校准滚动帧遥测在 60Hz 与 120Hz 显示节奏下的统计和长帧识别。
/// </summary>
public sealed class ScrollFrameTelemetryTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void StableCadenceMeetsFrameBudget(int refreshRate)
    {
        var tracker = new ScrollFrameCadenceTracker();
        TimeSpan renderingTime = TimeSpan.Zero;
        TimeSpan interval = TimeSpan.FromSeconds(1d / refreshRate);

        for (int frame = 0; frame <= refreshRate; frame++)
        {
            tracker.RecordFrame(renderingTime);
            renderingTime += interval;
        }

        ScrollFrameTelemetrySnapshot snapshot =
            tracker.Complete(
                refreshRate,
                deferredViewportEvaluationCount: 42);

        Assert.InRange(
            snapshot.AverageFramesPerSecond,
            refreshRate - 1d,
            refreshRate + 1d);
        Assert.Equal(0, snapshot.LongFrameCount);
        Assert.True(snapshot.MeetsFrameBudget);
        Assert.Equal(refreshRate, snapshot.TargetRefreshRate);
        Assert.Equal(42, snapshot.DeferredViewportEvaluationCount);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void DelayedFramesAreReportedAgainstDisplayCadence(
        int refreshRate)
    {
        var tracker = new ScrollFrameCadenceTracker();
        TimeSpan renderingTime = TimeSpan.Zero;
        TimeSpan interval = TimeSpan.FromSeconds(1d / refreshRate);

        for (int frame = 0; frame < 40; frame++)
        {
            tracker.RecordFrame(renderingTime);
            renderingTime += frame == 20
                ? interval * 4d
                : interval;
        }

        ScrollFrameTelemetrySnapshot snapshot =
            tracker.Complete(refreshRate);

        Assert.True(snapshot.LongFrameCount >= 1);
        Assert.True(snapshot.WorstFrameInterval >= interval * 3d);
        Assert.False(snapshot.MeetsFrameBudget);
    }

    [Fact]
    public void InferredCadenceDoesNotClaimAnUnverifiedDisplayBudget()
    {
        var tracker = new ScrollFrameCadenceTracker();
        TimeSpan renderingTime = TimeSpan.Zero;
        TimeSpan interval = TimeSpan.FromSeconds(1d / 30d);

        for (int frame = 0; frame <= 30; frame++)
        {
            tracker.RecordFrame(renderingTime);
            renderingTime += interval;
        }

        ScrollFrameTelemetrySnapshot snapshot = tracker.Complete();

        Assert.Equal(30, snapshot.TargetRefreshRate);
        Assert.False(snapshot.HasExplicitFrameBudget);
        Assert.False(snapshot.MeetsFrameBudget);
    }
}
