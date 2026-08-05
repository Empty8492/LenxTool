using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LenxTool.App.Controls;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 在真实 WPF 事件路由中验证上游逐帧滚动、长文节流和应用接线。
/// </summary>
[Collection(WpfRuntimeGroup.Name)]
public sealed class SmoothWheelScrollingWpfRuntimeTests
{
    [Fact]
    public void HeavyPhysicalPageUsesDirectUpstreamLogicalFrames()
    {
        Exception? failure = null;
        string stage = "starting";
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                try
                {
                    stage = "building heavy content";
                    var content = new StackPanel();
                    for (int index = 0; index < 130; index++)
                    {
                        content.Children.Add(
                            new Button
                            {
                                Height = 48d,
                                Content = $"热点条目 {index + 1}"
                            });
                    }

                    var viewer = new AnimatedScrollViewer
                    {
                        VerticalScrollBarVisibility =
                            ScrollBarVisibility.Visible,
                        HorizontalScrollBarVisibility =
                            ScrollBarVisibility.Disabled,
                        Content = content
                    };
                    window = CreateOffscreenWindow(
                        "Upstream physical wheel acceptance",
                        viewer,
                        width: 960d,
                        height: 620d);
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();
                    Assert.True(viewer.ScrollableHeight > 1000d);

                    Transform originalTransform = content.RenderTransform;
                    int logicalScrollUpdates = 0;
                    viewer.ScrollChanged += (_, eventArgs) =>
                    {
                        if (Math.Abs(eventArgs.VerticalChange) > 0.001d)
                        {
                            logicalScrollUpdates++;
                        }
                    };

                    stage = "raising one wheel notch";
                    RaiseWheel(content.Children[0]);
                    Assert.True(
                        SmoothWheelScrolling.HasActiveAnimation(viewer));
                    Assert.InRange(viewer.VerticalOffset, 0d, 0.01d);

                    stage = "observing intermediate frames";
                    PumpFor(TimeSpan.FromMilliseconds(45d));
                    Assert.InRange(viewer.VerticalOffset, 0.1d, 139d);
                    Assert.Same(originalTransform, content.RenderTransform);

                    stage = "waiting for upstream inertia to stop";
                    PumpUntil(
                        () => !SmoothWheelScrolling
                            .HasActiveAnimation(viewer),
                        TimeSpan.FromSeconds(2d));
                    Assert.InRange(viewer.VerticalOffset, 70d, 140d);
                    Assert.True(
                        logicalScrollUpdates > 1,
                        "上游路径应逐帧提交真实 VerticalOffset。" );
                    Assert.Same(originalTransform, content.RenderTransform);
                    stage = "completed assertions";
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(10),
            () => $"上游滚轮实窗验收在阶段“{stage}”超时。");

        ThrowIfFailed(failure);
    }

    [Fact]
    public void VirtualizedListReusesOneUpstreamSessionForBurstInput()
    {
        Exception? failure = null;
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                try
                {
                    var list = new ListBox
                    {
                        ItemsSource = Enumerable.Range(1, 500)
                            .Select(index => $"缓存边界条目 {index}")
                            .ToArray()
                    };
                    VirtualizingPanel.SetIsVirtualizing(list, true);
                    VirtualizingPanel.SetVirtualizationMode(
                        list,
                        VirtualizationMode.Recycling);
                    VirtualizingPanel.SetScrollUnit(
                        list,
                        ScrollUnit.Pixel);
                    VirtualizingPanel.SetCacheLength(
                        list,
                        new VirtualizationCacheLength(1d, 1d));
                    VirtualizingPanel.SetCacheLengthUnit(
                        list,
                        VirtualizationCacheLengthUnit.Page);
                    window = CreateOffscreenWindow(
                        "Upstream burst wheel acceptance",
                        list,
                        width: 520d,
                        height: 420d);
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();

                    ScrollViewer viewer =
                        FindDescendant<ScrollViewer>(list);
                    ListBoxItem firstItem = Assert.IsType<ListBoxItem>(
                        list.ItemContainerGenerator.ContainerFromIndex(0));
                    UIElement content =
                        Assert.IsAssignableFrom<UIElement>(viewer.Content);
                    Transform originalTransform = content.RenderTransform;

                    object? session = null;
                    for (int notch = 0; notch < 10; notch++)
                    {
                        RaiseWheel(firstItem);
                        session ??= SmoothWheelScrolling
                            .GetActiveAnimationSession(viewer);
                        Assert.Same(
                            session,
                            SmoothWheelScrolling
                                .GetActiveAnimationSession(viewer));
                    }

                    Assert.NotNull(session);
                    Assert.InRange(viewer.VerticalOffset, 0d, 0.01d);
                    PumpFor(TimeSpan.FromMilliseconds(45d));
                    Assert.True(viewer.VerticalOffset > 20d);
                    Assert.Same(originalTransform, content.RenderTransform);

                    PumpUntil(
                        () => !SmoothWheelScrolling
                            .HasActiveAnimation(viewer),
                        TimeSpan.FromSeconds(3d));
                    Assert.InRange(viewer.VerticalOffset, 650d, 1400d);
                    Assert.Same(originalTransform, content.RenderTransform);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(10),
            () => "虚拟列表上游连续滚动验收超时。");

        ThrowIfFailed(failure);
    }

    [Fact]
    public void DailyBriefingThrottlesDeferredViewportScans()
    {
        Exception? failure = null;
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                try
                {
                    RichArticleBlock[] blocks = Enumerable.Range(1, 180)
                        .Select(index => new RichArticleBlock(
                            RichArticleBlockKind.Body,
                            [new($"早报正文段落 {index:D3}，用于验证长文视口虚拟化。")]))
                        .ToArray();
                    var article = new NewsArticle(
                        "daily-virtualization",
                        new DateOnly(2026, 7, 29),
                        "每日早报",
                        "每日早报性能验收",
                        string.Empty,
                        string.Empty,
                        "https://example.com/daily",
                        "daily-virtualization-hash",
                        DateTimeOffset.UtcNow);
                    var articleView = new RichArticleView
                    {
                        Article = article,
                        Document = new RichArticleDocument(
                            null,
                            Array.AsReadOnly(blocks))
                    };
                    var viewer = new AnimatedScrollViewer
                    {
                        VerticalScrollBarVisibility =
                            ScrollBarVisibility.Visible,
                        HorizontalScrollBarVisibility =
                            ScrollBarVisibility.Disabled,
                        Content = articleView
                    };
                    window = CreateOffscreenWindow(
                        "Daily briefing viewport virtualization",
                        viewer,
                        width: 880d,
                        height: 520d);
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();
                    // 等待正文控件注册与首轮可见内容实现完全收敛，避免把初始化回调计入滚轮会话。
                    PumpFor(TimeSpan.FromMilliseconds(100d));
                    double initialExtentHeight = viewer.ExtentHeight;

                    Assert.InRange(
                        CountDescendants<TextBlock>(articleView),
                        2,
                        80);
                    Assert.True(viewer.ScrollableHeight > 4000d);
                    int deferredControlCount =
                        CountDescendants<ViewportDeferredContentControl>(
                            articleView);
                    Assert.True(deferredControlCount >= blocks.Length);
                    long evaluationCountBeforeWheel =
                        ViewportDeferredContentControl
                            .GetDeferredViewportEvaluationCount(viewer);
                    double refreshTravel = Math.Min(
                        120d,
                        Math.Max(1d, viewer.ViewportHeight * 0.25d));
                    int precisionStep = Math.Max(
                        1,
                        (int)Math.Ceiling(refreshTravel / 2d));

                    // 半个阈值的精确滚动包含多个逻辑帧，但不应逐帧遍历全文。
                    RaiseWheel(articleView, delta: -precisionStep);
                    PumpUntil(
                        () => !SmoothWheelScrolling
                            .HasActiveAnimation(viewer),
                        TimeSpan.FromSeconds(2d));
                    PumpDispatcher();
                    Assert.Equal(
                        evaluationCountBeforeWheel,
                        ViewportDeferredContentControl
                            .GetDeferredViewportEvaluationCount(viewer));

                    // 累计越过阈值后只允许协调器统一评估一轮。
                    RaiseWheel(articleView, delta: -precisionStep);
                    PumpUntil(
                        () => !SmoothWheelScrolling
                            .HasActiveAnimation(viewer),
                        TimeSpan.FromSeconds(2d));
                    PumpDispatcher();
                    Assert.Equal(
                        evaluationCountBeforeWheel
                        + deferredControlCount,
                        ViewportDeferredContentControl
                            .GetDeferredViewportEvaluationCount(viewer));

                    SmoothWheelScrolling.ScrollToImmediately(
                        viewer,
                        viewer.ScrollableHeight);
                    PumpDispatcher();
                    PumpUntil(
                        () => FindTextBlock(
                            articleView,
                            "早报正文段落 180") is not null,
                        TimeSpan.FromSeconds(2d));
                    double bottomExtentHeight = viewer.ExtentHeight;

                    Assert.Null(FindTextBlock(
                        articleView,
                        "早报正文段落 001"));
                    Assert.InRange(
                        CountDescendants<TextBlock>(articleView),
                        2,
                        80);

                    AnimatedScrollViewer.SmoothScrollToTopCommand.Execute(
                        parameter: null,
                        target: viewer);
                    PumpUntil(
                        () => FindTextBlock(
                            articleView,
                            "早报正文段落 001") is not null,
                        TimeSpan.FromSeconds(2d));

                    Assert.Null(FindTextBlock(
                        articleView,
                        "早报正文段落 180"));
                    Assert.InRange(viewer.VerticalOffset, 0d, 0.01d);
                    Assert.InRange(
                        Math.Abs(viewer.ExtentHeight - bottomExtentHeight),
                        0d,
                        1d);
                    Assert.InRange(
                        Math.Abs(viewer.ExtentHeight - initialExtentHeight),
                        0d,
                        1d);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(12),
            () => "每日早报长文视口节流验收超时。");

        ThrowIfFailed(failure);
    }

    [Fact]
    public void TrendGroupsRealizeOnlyVisibleCardsAndHalfViewportBuffer()
    {
        Exception? failure = null;
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                try
                {
                    var panel = new UniformGrid { Columns = 2 };
                    var groups =
                        new List<ViewportDeferredContentControl>();
                    int releasedGroupCount = 0;
                    for (int groupIndex = 0;
                         groupIndex < 13;
                         groupIndex++)
                    {
                        int capturedGroup = groupIndex;
                        var group = new ViewportDeferredContentControl
                        {
                            EstimatedHeight = 720d,
                            PreloadViewportCount = 0.5d,
                            ContentReleased = () => releasedGroupCount++,
                            ContentFactory = () =>
                            {
                                var items = new StackPanel();
                                for (int itemIndex = 0;
                                     itemIndex < 10;
                                     itemIndex++)
                                {
                                    items.Children.Add(
                                        new Button
                                        {
                                            Height = 58d,
                                            Content =
                                                $"平台 {capturedGroup + 1} 热点 {itemIndex + 1}"
                                        });
                                }
                                return items;
                            }
                        };
                        groups.Add(group);
                        panel.Children.Add(group);
                    }

                    var viewer = new AnimatedScrollViewer
                    {
                        VerticalScrollBarVisibility =
                            ScrollBarVisibility.Visible,
                        HorizontalScrollBarVisibility =
                            ScrollBarVisibility.Disabled,
                        Content = panel
                    };
                    window = CreateOffscreenWindow(
                        "Trend viewport virtualization",
                        viewer,
                        width: 960d,
                        height: 620d);
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();

                    Assert.InRange(
                        groups.Count(group => group.IsContentRealized),
                        2,
                        8);
                    Assert.True(viewer.ScrollableHeight > 3000d);

                    SmoothWheelScrolling.ScrollToImmediately(
                        viewer,
                        viewer.ScrollableHeight);
                    PumpUntil(
                        () => groups[^1].IsContentRealized,
                        TimeSpan.FromSeconds(2d));

                    Assert.False(groups[0].IsContentRealized);
                    Assert.True(releasedGroupCount >= 1);
                    Assert.InRange(
                        groups.Count(group => group.IsContentRealized),
                        2,
                        8);

                    window.Close();
                    window = null;
                    PumpDispatcher();
                    Assert.All(
                        groups,
                        group => Assert.False(group.IsContentRealized));
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(10),
            () => "热点平台卡片视口虚拟化验收超时。");

        ThrowIfFailed(failure);
    }

    [Fact]
    public void PlainAndDailyBriefingScrollViewersShareUpstreamBehavior()
    {
        Exception? failure = null;
        string stage = "starting";
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                try
                {
                    SmoothWheelScrolling.Initialize();
                    (ScrollViewer plain, Border plainContent) =
                        CreateScrollViewer(animated: false);
                    (ScrollViewer briefing, Border briefingContent) =
                        CreateScrollViewer(animated: true);
                    var panel = new Grid();
                    panel.RowDefinitions.Add(new());
                    panel.RowDefinitions.Add(new());
                    Grid.SetRow(briefing, 1);
                    panel.Children.Add(plain);
                    panel.Children.Add(briefing);
                    window = CreateOffscreenWindow(
                        "Shared upstream wheel acceptance",
                        panel,
                        width: 640d,
                        height: 520d);
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();
                    Assert.True(plain.ScrollableHeight > 100d);
                    Assert.True(briefing.ScrollableHeight > 100d);

                    stage = "starting both physical sessions";
                    RaiseWheel(plainContent);
                    RaiseWheel(briefingContent);
                    Assert.True(
                        SmoothWheelScrolling.HasActiveAnimation(plain));
                    Assert.True(
                        SmoothWheelScrolling.HasActiveAnimation(briefing));
                    object? firstSession = SmoothWheelScrolling
                        .GetActiveAnimationSession(plain);
                    RaiseWheel(plainContent);
                    Assert.Same(
                        firstSession,
                        SmoothWheelScrolling
                            .GetActiveAnimationSession(plain));

                    stage = "waiting for shared behavior";
                    PumpUntil(
                        () => !SmoothWheelScrolling
                                  .HasActiveAnimation(plain)
                              && !SmoothWheelScrolling
                                  .HasActiveAnimation(briefing),
                        TimeSpan.FromSeconds(3d));
                    Assert.InRange(briefing.VerticalOffset, 70d, 140d);
                    Assert.InRange(plain.VerticalOffset, 150d, 280d);

                    stage = "checking direct input takeover";
                    RaiseWheel(plainContent);
                    Assert.True(
                        SmoothWheelScrolling.HasActiveAnimation(plain));
                    RaiseMouseDown(plainContent);
                    Assert.False(
                        SmoothWheelScrolling.HasActiveAnimation(plain));
                    double directInputOffset = plain.VerticalOffset;
                    PumpFor(TimeSpan.FromMilliseconds(120d));
                    Assert.InRange(
                        plain.VerticalOffset,
                        directInputOffset - 0.01d,
                        directInputOffset + 0.01d);

                    stage = "checking host policy takeover";
                    RaiseWheel(plainContent);
                    Assert.True(
                        SmoothWheelScrolling.HasActiveAnimation(plain));
                    bool previousReduceMotion =
                        Application.Current.Resources[
                            "LenxTool.ReduceMotion"] is true;
                    try
                    {
                        Application.Current.Resources[
                            "LenxTool.ReduceMotion"] = true;
                        var nativeWheel = new MouseWheelEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            -120)
                        {
                            RoutedEvent = UIElement.PreviewMouseWheelEvent
                        };
                        plainContent.RaiseEvent(nativeWheel);
                        Assert.False(nativeWheel.Handled);
                        Assert.False(
                            SmoothWheelScrolling.HasActiveAnimation(plain));
                    }
                    finally
                    {
                        Application.Current.Resources[
                            "LenxTool.ReduceMotion"] =
                            previousReduceMotion;
                    }

                    stage = "checking precision input";
                    SmoothWheelScrolling.ScrollToImmediately(plain, 0d);
                    PumpDispatcher();
                    RaiseWheel(plainContent, delta: -30);
                    Assert.InRange(plain.VerticalOffset, 0d, 0.01d);
                    PumpUntil(
                        () => !SmoothWheelScrolling
                            .HasActiveAnimation(plain),
                        TimeSpan.FromSeconds(2d));
                    Assert.InRange(plain.VerticalOffset, 29.9d, 30.1d);

                    stage = "checking immediate back-to-top handoff";
                    SmoothWheelScrolling.ScrollToImmediately(
                        briefing,
                        300d);
                    RaiseWheel(briefingContent);
                    PumpFor(TimeSpan.FromMilliseconds(40d));
                    AnimatedScrollViewer.SmoothScrollToTopCommand.Execute(
                        parameter: null,
                        target: briefing);
                    Assert.False(
                        SmoothWheelScrolling.HasActiveAnimation(briefing));
                    PumpFor(TimeSpan.FromMilliseconds(200d));
                    Assert.InRange(briefing.VerticalOffset, 0d, 0.01d);

                    stage = "checking unload cleanup";
                    RaiseWheel(plainContent);
                    Assert.True(
                        SmoothWheelScrolling.HasActiveAnimation(plain));
                    panel.Children.Remove(plain);
                    Assert.DoesNotContain(plain, panel.Children.Cast<UIElement>());
                    PumpDispatcher();
                    Assert.False(
                        SmoothWheelScrolling.HasActiveAnimation(plain));
                    window.Close();
                    window = null;
                    PumpDispatcher();

                    stage = "checking reload starts with fresh momentum";
                    window = CreateOffscreenWindow(
                        "Reloaded upstream wheel acceptance",
                        plain,
                        width: 640d,
                        height: 520d);
                    window.Show();
                    window.UpdateLayout();
                    SmoothWheelScrolling.ScrollToImmediately(plain, 0d);
                    PumpDispatcher();
                    RaiseWheel(plainContent);
                    PumpUntil(
                        () => !SmoothWheelScrolling
                            .HasActiveAnimation(plain),
                        TimeSpan.FromSeconds(2d));
                    Assert.InRange(plain.VerticalOffset, 70d, 140d);
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
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(15),
            () => $"共享上游滚轮验收在阶段“{stage}”超时。");

        ThrowIfFailed(failure);
    }

    private static Window CreateOffscreenWindow(
        string title,
        object content,
        double width,
        double height) =>
        new()
        {
            Title = title,
            Width = width,
            Height = height,
            Left = -10000d,
            Top = -10000d,
            ShowInTaskbar = false,
            Content = content
        };

    private static (ScrollViewer Viewer, Border Content)
        CreateScrollViewer(bool animated)
    {
        var content = new Border { Height = 2000d };
        ScrollViewer viewer = animated
            ? new AnimatedScrollViewer()
            : new ScrollViewer();
        viewer.VerticalScrollBarVisibility =
            ScrollBarVisibility.Auto;
        viewer.HorizontalScrollBarVisibility =
            ScrollBarVisibility.Disabled;
        viewer.Content = content;
        return (viewer, content);
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
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException(
                    "等待滚轮动画或视口刷新完成超时。");
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

    private static T FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        T? match = FindDescendantOrDefault<T>(parent);
        return match
               ?? throw new InvalidOperationException(
                   $"未找到 {typeof(T).Name} 子控件。");
    }

    private static T? FindDescendantOrDefault<T>(
        DependencyObject parent)
        where T : DependencyObject
    {
        for (int index = 0;
             index < VisualTreeHelper.GetChildrenCount(parent);
             index++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            T? descendant = FindDescendantOrDefault<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static int CountDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int count = 0;
        for (int index = 0;
             index < VisualTreeHelper.GetChildrenCount(parent);
             index++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(parent, index);
            if (child is T) count++;
            count += CountDescendants<T>(child);
        }
        return count;
    }

    private static TextBlock? FindTextBlock(
        DependencyObject parent,
        string textPrefix)
    {
        for (int index = 0;
             index < VisualTreeHelper.GetChildrenCount(parent);
             index++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock textBlock
                && GetRenderedText(textBlock).StartsWith(
                    textPrefix,
                    StringComparison.Ordinal))
            {
                return textBlock;
            }

            TextBlock? descendant = FindTextBlock(child, textPrefix);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static string GetRenderedText(TextBlock textBlock)
    {
        if (!string.IsNullOrEmpty(textBlock.Text)) return textBlock.Text;
        return string.Concat(
            textBlock.Inlines
                .OfType<System.Windows.Documents.Run>()
                .Select(run => run.Text));
    }

    private static void ThrowIfFailed(Exception? failure)
    {
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
