using System.Windows;
using System.Windows.Controls;

namespace LenxTool.App.Controls;

/// <summary>
/// 汇总一次真实滚动动画会话的显示帧节奏与延迟视口评估量。
/// </summary>
internal sealed record ScrollFrameTelemetrySnapshot(
    int FrameCount,
    TimeSpan Duration,
    double AverageFramesPerSecond,
    TimeSpan P95FrameInterval,
    TimeSpan WorstFrameInterval,
    int LongFrameCount,
    int DeferredViewportEvaluationCount,
    int TargetRefreshRate,
    bool HasExplicitFrameBudget,
    bool MeetsFrameBudget);

/// <summary>
/// 使用固定容量数组记录短滚动动画，避免在每个渲染帧分配对象。
/// </summary>
internal sealed class ScrollFrameCadenceTracker
{
    private const int MaximumIntervals = 512;
    private readonly long[] _intervalTicks = new long[MaximumIntervals];
    private int _intervalCount;
    private TimeSpan? _lastRenderingTime;

    internal void Reset()
    {
        _intervalCount = 0;
        _lastRenderingTime = null;
    }

    internal void RecordFrame(TimeSpan renderingTime)
    {
        if (_lastRenderingTime is not { } previous)
        {
            _lastRenderingTime = renderingTime;
            return;
        }

        long intervalTicks = (renderingTime - previous).Ticks;
        _lastRenderingTime = renderingTime;
        if (intervalTicks <= 0) return;

        if (_intervalCount < MaximumIntervals)
        {
            _intervalTicks[_intervalCount++] = intervalTicks;
        }
    }

    internal ScrollFrameTelemetrySnapshot Complete(
        int? targetRefreshRate = null,
        int deferredViewportEvaluationCount = 0)
    {
        int viewportEvaluationCount = Math.Max(
            0,
            deferredViewportEvaluationCount);
        if (_intervalCount == 0)
        {
            int emptyTarget = targetRefreshRate.GetValueOrDefault(60);
            return new(
                FrameCount: _lastRenderingTime.HasValue ? 1 : 0,
                Duration: TimeSpan.Zero,
                AverageFramesPerSecond: 0d,
                P95FrameInterval: TimeSpan.Zero,
                WorstFrameInterval: TimeSpan.Zero,
                LongFrameCount: 0,
                DeferredViewportEvaluationCount:
                    viewportEvaluationCount,
                TargetRefreshRate: emptyTarget,
                HasExplicitFrameBudget: targetRefreshRate.HasValue,
                MeetsFrameBudget: false);
        }

        long[] sortedIntervals = _intervalTicks
            .AsSpan(0, _intervalCount)
            .ToArray();
        Array.Sort(sortedIntervals);
        long totalTicks = 0L;
        for (int index = 0; index < _intervalCount; index++)
        {
            totalTicks += _intervalTicks[index];
        }

        long medianTicks =
            sortedIntervals[sortedIntervals.Length / 2];
        int target = targetRefreshRate
                     ?? InferRefreshRate(medianTicks);
        double targetIntervalTicks =
            TimeSpan.TicksPerSecond / (double)target;
        int longFrames = 0;
        for (int index = 0; index < _intervalCount; index++)
        {
            if (_intervalTicks[index] > targetIntervalTicks * 1.75d)
            {
                longFrames++;
            }
        }
        int p95Index = Math.Clamp(
            (int)Math.Ceiling(sortedIntervals.Length * 0.95d) - 1,
            0,
            sortedIntervals.Length - 1);
        long p95Ticks = sortedIntervals[p95Index];
        bool meetsBudget =
            targetRefreshRate.HasValue
            && longFrames == 0
            && p95Ticks <= targetIntervalTicks * 1.5d;
        var duration = TimeSpan.FromTicks(totalTicks);
        return new(
            FrameCount: _intervalCount + 1,
            Duration: duration,
            AverageFramesPerSecond:
                _intervalCount / duration.TotalSeconds,
            P95FrameInterval: TimeSpan.FromTicks(p95Ticks),
            WorstFrameInterval:
                TimeSpan.FromTicks(sortedIntervals[^1]),
            LongFrameCount: longFrames,
            DeferredViewportEvaluationCount:
                viewportEvaluationCount,
            TargetRefreshRate: target,
            HasExplicitFrameBudget: targetRefreshRate.HasValue,
            MeetsFrameBudget: meetsBudget);
    }

    private static int InferRefreshRate(long medianTicks)
    {
        double measuredRate =
            TimeSpan.TicksPerSecond / (double)medianTicks;
        int[] commonRates = [30, 60, 90, 120, 144, 165, 240];
        return commonRates.MinBy(rate =>
            Math.Abs(rate - measuredRate));
    }
}

/// <summary>
/// 保存每个滚动区最近一次实窗帧会话，供诊断与回归测试读取。
/// </summary>
internal static class ScrollFrameTelemetry
{
    private static readonly DependencyProperty LatestSnapshotProperty =
        DependencyProperty.RegisterAttached(
            "LatestSnapshot",
            typeof(ScrollFrameTelemetrySnapshot),
            typeof(ScrollFrameTelemetry),
            new PropertyMetadata(null));

    internal static ScrollFrameTelemetrySnapshot? GetLatestSnapshot(
        ScrollViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return (ScrollFrameTelemetrySnapshot?)
            viewer.GetValue(LatestSnapshotProperty);
    }

    internal static void Publish(
        ScrollViewer viewer,
        ScrollFrameTelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(snapshot);
        viewer.SetValue(LatestSnapshotProperty, snapshot);
    }
}
