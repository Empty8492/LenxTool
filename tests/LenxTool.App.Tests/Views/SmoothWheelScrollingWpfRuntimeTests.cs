using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LenxTool.App.Controls;
using LenxTool.App.Services;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 在真实 WPF 事件路由中验证普通页面与每日早报控件共享滚轮手感。
/// </summary>
[Collection(WpfRuntimeGroup.Name)]
public sealed class SmoothWheelScrollingWpfRuntimeTests
{
    [Fact]
    public void PlainAndDailyBriefingScrollViewersShareSmoothWheelBehavior()
    {
        Exception? failure = null;
        string stage = "starting";
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                var themeService = new ThemeService();
                try
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    (ScrollViewer plain, Border plainContent) =
                        CreateScrollViewer(animated: false);
                    (ScrollViewer briefing, Border briefingContent) =
                        CreateScrollViewer(animated: true);
                    var virtualizedList = new ListBox
                    {
                        ItemsSource = Enumerable.Range(1, 200).ToArray()
                    };
                    var panel = new Grid();
                    panel.RowDefinitions.Add(new());
                    panel.RowDefinitions.Add(new());
                    panel.RowDefinitions.Add(new());
                    Grid.SetRow(briefing, 1);
                    Grid.SetRow(virtualizedList, 2);
                    panel.Children.Add(plain);
                    panel.Children.Add(briefing);
                    panel.Children.Add(virtualizedList);
                    window = new Window
                    {
                        Title = "Smooth wheel runtime acceptance",
                        Width = 640,
                        Height = 520,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = panel
                    };
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();
                    Assert.True(plain.ScrollableHeight > 100d);
                    Assert.True(briefing.ScrollableHeight > 100d);
                    // 像素滚动保留虚拟化，同时避免列表按整项跳动。
                    Assert.True(VirtualizingPanel.GetIsVirtualizing(
                        virtualizedList));
                    Assert.Equal(
                        ScrollUnit.Pixel,
                        VirtualizingPanel.GetScrollUnit(virtualizedList));

                    stage = "checking plain ScrollViewer transition";
                    double expectedPlain = ExpectedTarget(plain);
                    RaiseWheel(plainContent);
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        Assert.True(plain.VerticalOffset < expectedPlain);
                    }
                    PumpUntil(
                        () => Math.Abs(plain.VerticalOffset - expectedPlain) < 1d,
                        TimeSpan.FromSeconds(2));

                    stage = "checking accumulated wheel target";
                    // 测试重置先终止上一段动画，避免把前一格滚轮计入本场景。
                    SmoothWheelScrolling.Cancel(plain);
                    plain.ScrollToTop();
                    PumpDispatcher();
                    double expectedAccumulated = Math.Min(
                        plain.ScrollableHeight,
                        ExpectedTarget(plain) * 2d);
                    RaiseWheel(plainContent);
                    RaiseWheel(plainContent);
                    PumpUntil(
                        () => Math.Abs(
                            plain.VerticalOffset - expectedAccumulated) < 1d,
                        TimeSpan.FromSeconds(2),
                        () => $"普通滚动区实际偏移 {plain.VerticalOffset:F2}，"
                              + $"期望累计偏移 {expectedAccumulated:F2}。");

                    stage = "checking burst input reuses one animation session";
                    SmoothWheelScrolling.ScrollToImmediately(plain, 0d);
                    PumpDispatcher();
                    RaiseWheel(plainContent);
                    object? animationSession =
                        SmoothWheelScrolling.GetActiveAnimationSession(plain);
                    Assert.NotNull(animationSession);
                    PumpFor(TimeSpan.FromMilliseconds(32d));
                    double offsetBeforeRetarget = plain.VerticalOffset;
                    RaiseWheel(plainContent);
                    Assert.Same(
                        animationSession,
                        SmoothWheelScrolling.GetActiveAnimationSession(plain));
                    PumpFor(TimeSpan.FromMilliseconds(32d));
                    Assert.True(
                        plain.VerticalOffset > offsetBeforeRetarget,
                        $"连续滚轮更新目标后偏移应继续前进；更新前 {offsetBeforeRetarget:F2}，"
                        + $"更新后 {plain.VerticalOffset:F2}。");

                    stage = "checking immediate direction reversal";
                    SmoothWheelScrolling.ScrollToImmediately(plain, 0d);
                    PumpDispatcher();
                    RaiseWheel(plainContent, delta: -120);
                    RaiseWheel(plainContent, delta: 120);
                    PumpFor(TimeSpan.FromMilliseconds(300));
                    Assert.InRange(plain.VerticalOffset, 0d, 0.01d);

                    stage = "checking programmatic restore cancels old animation";
                    RaiseWheel(plainContent, delta: -120);
                    SmoothWheelScrolling.ScrollToImmediately(plain, 200d);
                    PumpFor(TimeSpan.FromMilliseconds(300));
                    Assert.InRange(plain.VerticalOffset, 199d, 201d);

                    stage = "checking direct input cancels wheel animation";
                    SmoothWheelScrolling.ScrollToImmediately(plain, 0d);
                    PumpDispatcher();
                    RaiseWheel(plainContent, delta: -120);
                    RaiseMouseDown(plainContent);
                    plain.ScrollToVerticalOffset(200d);
                    PumpFor(TimeSpan.FromMilliseconds(300));
                    Assert.InRange(plain.VerticalOffset, 199d, 201d);

                    stage = "checking daily briefing parity";
                    double expectedBriefing = ExpectedTarget(briefing);
                    RaiseWheel(briefingContent);
                    PumpUntil(
                        () => Math.Abs(
                            briefing.VerticalOffset - expectedBriefing) < 1d,
                        TimeSpan.FromSeconds(2));
                    Assert.InRange(
                        Math.Abs(expectedBriefing - expectedPlain),
                        0d,
                        0.01d);

                    stage = "checking wheel to back-to-top handoff";
                    SmoothWheelScrolling.ScrollToImmediately(briefing, 300d);
                    PumpDispatcher();
                    var handoffOffsets = new List<double>();
                    briefing.ScrollChanged += (_, _) =>
                        handoffOffsets.Add(briefing.VerticalOffset);
                    RaiseWheel(briefingContent);
                    AnimatedScrollViewer.SmoothScrollToTopCommand.Execute(
                        parameter: null,
                        target: briefing);
                    Assert.False(
                        SmoothWheelScrolling.HasActiveAnimation(briefing));
                    PumpFor(TimeSpan.FromMilliseconds(520));
                    Assert.InRange(briefing.VerticalOffset, 0d, 0.01d);
                    Assert.All(
                        handoffOffsets,
                        offset => Assert.InRange(offset, 0d, 301d));

                    stage = "checking reduced motion fallback";
                    themeService.ApplyReduceMotion(reduceMotion: true);
                    SmoothWheelScrolling.Cancel(plain);
                    plain.ScrollToTop();
                    PumpDispatcher();
                    RaiseWheel(plainContent);
                    PumpDispatcher();
                    double reducedMotionDifference = Math.Abs(
                        plain.VerticalOffset - expectedPlain);
                    Assert.True(
                        reducedMotionDifference <= 0.01d,
                        $"减少动画后的实际偏移 {plain.VerticalOffset:F2}，"
                        + $"期望偏移 {expectedPlain:F2}。");

                    stage = "checking unload releases the render session";
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    RaiseWheel(plainContent);
                    Assert.True(
                        SmoothWheelScrolling.HasActiveAnimation(plain));
                    window.Close();
                    window = null;
                    PumpDispatcher();
                    Assert.False(
                        SmoothWheelScrolling.HasActiveAnimation(plain));
                    stage = "completed assertions";
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    // 共享 WPF 测试进程必须排空窗口关闭事件，避免残留 Automation 树污染后续场景。
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(15),
            () => $"平滑滚轮 WPF 验收在阶段“{stage}”超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static (ScrollViewer Viewer, Border Content) CreateScrollViewer(
        bool animated)
    {
        var content = new Border
        {
            Height = 2000d
        };
        ScrollViewer viewer = animated
            ? new AnimatedScrollViewer()
            : new ScrollViewer();
        viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        viewer.Content = content;
        return (viewer, content);
    }

    private static double ExpectedTarget(ScrollViewer viewer)
    {
        WheelScrollPlan plan = SmoothWheelScrolling.CreateWheelPlan(
            viewer.VerticalOffset,
            viewer.VerticalOffset,
            viewer.ScrollableHeight,
            viewer.ViewportHeight,
            wheelDelta: -120,
            SystemParameters.WheelScrollLines,
            usesLogicalUnits: false,
            motionAllowed: true);
        return plan.TargetOffset;
    }

    private static void RaiseWheel(
        UIElement source,
        int delta = -120)
    {
        var eventArgs = new MouseWheelEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        };
        source.RaiseEvent(eventArgs);
        Assert.True(eventArgs.Handled);
    }

    private static void RaiseMouseDown(UIElement source)
    {
        var eventArgs = new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        };
        source.RaiseEvent(eventArgs);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            PumpDispatcher();
        }
    }

    private static void PumpUntil(
        Func<bool> condition,
        TimeSpan timeout,
        Func<string>? timeoutDetail = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException(
                    timeoutDetail?.Invoke()
                    ?? "等待滚轮动画到达目标位置超时。");
            }
            PumpDispatcher();
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
