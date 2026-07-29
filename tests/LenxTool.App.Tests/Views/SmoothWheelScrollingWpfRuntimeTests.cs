using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LenxTool.App.Controls;
using LenxTool.App.Services;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 在真实 WPF 事件路由中验证普通页面与每日早报控件共享滚轮手感。
/// </summary>
[Collection(WpfRuntimeGroup.Name)]
public sealed class SmoothWheelScrollingWpfRuntimeTests
{
    [Fact]
    public void HeavyPhysicalPageCommitsOneLogicalScrollDuringSmoothTransition()
    {
        Exception? failure = null;
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                var themeService = new ThemeService();
                try
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
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
                        VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = content
                    };
                    window = new Window
                    {
                        Title = "Heavy smooth wheel runtime acceptance",
                        Width = 960,
                        Height = 620,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = viewer
                    };
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();

                    int logicalScrollUpdates = 0;
                    viewer.ScrollChanged += (_, eventArgs) =>
                    {
                        if (Math.Abs(eventArgs.VerticalChange) > 0.001d)
                        {
                            logicalScrollUpdates++;
                        }
                    };

                    double expectedTarget = ExpectedTarget(viewer);
                    RaiseWheel(content.Children[0]);
                    var scrollTransform = Assert.IsType<TranslateTransform>(
                        content.RenderTransform);
                    Assert.True(
                        scrollTransform.Y > 0d,
                        "重页面应通过内容渲染变换补间，而不是逐帧修改逻辑滚动位置。");
                    PumpDispatcher();
                    Assert.InRange(
                        viewer.VerticalOffset,
                        expectedTarget - 0.5d,
                        expectedTarget + 0.5d);
                    PumpFor(TimeSpan.FromMilliseconds(320d));

                    Assert.InRange(
                        viewer.VerticalOffset,
                        expectedTarget - 0.5d,
                        expectedTarget + 0.5d);
                    Assert.InRange(scrollTransform.Y, -0.01d, 0.01d);
                    Assert.Equal(1, logicalScrollUpdates);
                    ScrollFrameTelemetrySnapshot telemetry = Assert.IsType<
                        ScrollFrameTelemetrySnapshot>(
                        ScrollFrameTelemetry.GetLatestSnapshot(viewer));
                    Assert.True(telemetry.FrameCount >= 2);
                    Assert.True(double.IsFinite(
                        telemetry.AverageFramesPerSecond));
                    Assert.True(
                        telemetry.AverageFramesPerSecond > 0d);
                    Assert.False(telemetry.HasExplicitFrameBudget);
                    Assert.False(telemetry.MeetsFrameBudget);
                    Assert.True(
                        telemetry.P95FrameInterval > TimeSpan.Zero);
                    Assert.True(
                        telemetry.WorstFrameInterval
                            >= telemetry.P95FrameInterval);
                    Assert.True(
                        telemetry.Duration
                            >= telemetry.WorstFrameInterval);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(10),
            () => "重页面平滑滚动验收超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void HeavyPhysicalPageCommitsOneLogicalScrollWhenReturningToTop()
    {
        Exception? failure = null;
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                var themeService = new ThemeService();
                try
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    var content = new StackPanel();
                    for (int index = 0; index < 130; index++)
                    {
                        content.Children.Add(
                            new Button
                            {
                                Height = 48d,
                                Content = $"早报段落 {index + 1}"
                            });
                    }

                    var viewer = new AnimatedScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = content
                    };
                    window = new Window
                    {
                        Title = "Heavy back-to-top runtime acceptance",
                        Width = 960,
                        Height = 620,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = viewer
                    };
                    window.Show();
                    window.UpdateLayout();
                    viewer.ScrollToVerticalOffset(1200d);
                    PumpDispatcher();

                    int logicalScrollUpdates = 0;
                    viewer.ScrollChanged += (_, eventArgs) =>
                    {
                        if (Math.Abs(eventArgs.VerticalChange) > 0.001d)
                        {
                            logicalScrollUpdates++;
                        }
                    };

                    AnimatedScrollViewer.SmoothScrollToTopCommand.Execute(
                        parameter: null,
                        target: viewer);
                    PumpDispatcher();

                    Assert.InRange(viewer.VerticalOffset, 0d, 0.5d);
                    var scrollTransform = Assert.IsType<TranslateTransform>(
                        content.RenderTransform);
                    Assert.True(
                        scrollTransform.Y < -100d,
                        "回顶应一次提交顶部偏移，再通过内容渲染变换保持连续画面。");
                    PumpFor(TimeSpan.FromMilliseconds(520d));

                    Assert.InRange(viewer.VerticalOffset, 0d, 0.01d);
                    Assert.InRange(scrollTransform.Y, -0.01d, 0.01d);
                    Assert.Equal(1, logicalScrollUpdates);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(10),
            () => "重页面合成式回顶验收超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void ReverseWheelDuringBackToTopStartsFromCurrentVisualPosition()
    {
        Exception? failure = null;
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                var themeService = new ThemeService();
                try
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    var content = new Border { Height = 4000d };
                    var viewer = new AnimatedScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = content
                    };
                    window = new Window
                    {
                        Title = "Reverse back-to-top wheel acceptance",
                        Width = 720,
                        Height = 520,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = viewer
                    };
                    window.Show();
                    window.UpdateLayout();
                    SmoothWheelScrolling.ScrollToImmediately(viewer, 1600d);
                    PumpDispatcher();

                    AnimatedScrollViewer.SmoothScrollToTopCommand.Execute(
                        parameter: null,
                        target: viewer);
                    Assert.True(
                        SmoothWheelScrolling.HasActiveAnimation(viewer));
                    var transform = Assert.IsType<TranslateTransform>(
                        content.RenderTransform);
                    double visualOffsetAtReverse = Math.Clamp(
                        viewer.VerticalOffset - transform.Y,
                        0d,
                        viewer.ScrollableHeight);
                    Assert.True(visualOffsetAtReverse > 100d);
                    WheelScrollPlan expected = SmoothWheelScrolling.CreateWheelPlan(
                        visualOffsetAtReverse,
                        visualOffsetAtReverse,
                        viewer.ScrollableHeight,
                        viewer.ViewportHeight,
                        wheelDelta: -120,
                        SystemParameters.WheelScrollLines,
                        usesLogicalUnits: false,
                        motionAllowed: true);

                    RaiseWheel(content, delta: -120);
                    PumpFor(TimeSpan.FromMilliseconds(320d));

                    Assert.InRange(
                        viewer.VerticalOffset,
                        expected.TargetOffset - 1d,
                        expected.TargetOffset + 1d);
                    Assert.True(viewer.VerticalOffset > visualOffsetAtReverse);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(10),
            () => "回顶途中反向滚轮验收超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void BufferedPixelVirtualizedListUsesOneLogicalScrollCommit()
    {
        Exception? failure = null;
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                var themeService = new ThemeService();
                try
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    var list = new ListBox
                    {
                        ItemsSource = Enumerable.Range(1, 500)
                            .Select(index => $"虚拟列表条目 {index}")
                            .ToArray()
                    };
                    VirtualizingPanel.SetIsVirtualizing(list, true);
                    VirtualizingPanel.SetVirtualizationMode(
                        list,
                        VirtualizationMode.Recycling);
                    VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
                    VirtualizingPanel.SetCacheLength(
                        list,
                        new VirtualizationCacheLength(1d, 1d));
                    VirtualizingPanel.SetCacheLengthUnit(
                        list,
                        VirtualizationCacheLengthUnit.Page);
                    window = new Window
                    {
                        Title = "Buffered virtualized wheel runtime acceptance",
                        Width = 520,
                        Height = 420,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = list
                    };
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();

                    ScrollViewer viewer = FindDescendant<ScrollViewer>(list);
                    UIElement content = Assert.IsAssignableFrom<UIElement>(
                        viewer.Content);
                    int logicalScrollUpdates = 0;
                    viewer.ScrollChanged += (_, eventArgs) =>
                    {
                        if (Math.Abs(eventArgs.VerticalChange) > 0.001d)
                        {
                            logicalScrollUpdates++;
                        }
                    };

                    ListBoxItem firstItem = Assert.IsType<ListBoxItem>(
                        list.ItemContainerGenerator.ContainerFromIndex(0));
                    RaiseWheel(firstItem);
                    PumpDispatcher();

                    Assert.True(viewer.VerticalOffset > 1d);
                    var scrollTransform = Assert.IsType<TranslateTransform>(
                        content.RenderTransform);
                    Assert.True(
                        scrollTransform.Y > 0d,
                        "带缓存的像素虚拟列表应通过渲染变换补间滚动。");
                    PumpFor(TimeSpan.FromMilliseconds(320d));

                    Assert.InRange(scrollTransform.Y, -0.01d, 0.01d);
                    Assert.Equal(1, logicalScrollUpdates);
                    int realizedItems = Enumerable.Range(0, list.Items.Count)
                        .Count(index =>
                            list.ItemContainerGenerator.ContainerFromIndex(index)
                            is not null);
                    Assert.InRange(realizedItems, 2, 200);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(10),
            () => "像素虚拟列表合成式滚动验收超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void VirtualizedListFallsBackWhenBurstExceedsRealizedCache()
    {
        Exception? failure = null;
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                var themeService = new ThemeService();
                try
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
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
                    VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
                    VirtualizingPanel.SetCacheLength(
                        list,
                        new VirtualizationCacheLength(1d, 1d));
                    VirtualizingPanel.SetCacheLengthUnit(
                        list,
                        VirtualizationCacheLengthUnit.Page);
                    window = new Window
                    {
                        Title = "Virtualized cache boundary acceptance",
                        Width = 520,
                        Height = 420,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = list
                    };
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();

                    ScrollViewer viewer = FindDescendant<ScrollViewer>(list);
                    UIElement content = Assert.IsAssignableFrom<UIElement>(
                        viewer.Content);
                    ListBoxItem firstItem = Assert.IsType<ListBoxItem>(
                        list.ItemContainerGenerator.ContainerFromIndex(0));
                    for (int notch = 0; notch < 10; notch++)
                    {
                        RaiseWheel(firstItem);
                    }

                    var scrollTransform = Assert.IsType<TranslateTransform>(
                        content.RenderTransform);
                    Assert.InRange(scrollTransform.Y, -0.01d, 0.01d);
                    double offsetBeforeFrames = viewer.VerticalOffset;
                    PumpFor(TimeSpan.FromMilliseconds(320d));

                    Assert.True(
                        viewer.VerticalOffset > offsetBeforeFrames + 1d,
                        "累计目标超过一屏缓存时应退回逐帧偏移，避免合成动画穿过未实现项目。");
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(10),
            () => "虚拟列表缓存边界验收超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void ExternalRenderTransformForcesSafeOffsetFallback()
    {
        Exception? failure = null;
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                var themeService = new ThemeService();
                try
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    var content = new StackPanel();
                    for (int index = 0; index < 80; index++)
                    {
                        content.Children.Add(
                            new Border { Height = 48d });
                    }

                    var viewer = new AnimatedScrollViewer
                    {
                        VerticalScrollBarVisibility =
                            ScrollBarVisibility.Visible,
                        Content = content
                    };
                    window = new Window
                    {
                        Title = "External transform ownership acceptance",
                        Width = 720,
                        Height = 520,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = viewer
                    };
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();

                    RaiseWheel(content.Children[0]);
                    PumpFor(TimeSpan.FromMilliseconds(40d));
                    double offsetBeforeExternalTakeover =
                        viewer.VerticalOffset;
                    var externalTransform = new TranslateTransform(3d, 0d);
                    content.RenderTransform = externalTransform;
                    RaiseMouseDown(content);
                    PumpDispatcher();
                    Assert.InRange(
                        viewer.VerticalOffset,
                        offsetBeforeExternalTakeover - 0.5d,
                        offsetBeforeExternalTakeover + 0.5d);
                    int logicalScrollUpdates = 0;
                    viewer.ScrollChanged += (_, eventArgs) =>
                    {
                        if (Math.Abs(eventArgs.VerticalChange) > 0.001d)
                        {
                            logicalScrollUpdates++;
                        }
                    };

                    RaiseWheel(content.Children[0]);
                    PumpFor(TimeSpan.FromMilliseconds(320d));

                    Assert.Same(externalTransform, content.RenderTransform);
                    Assert.True(
                        logicalScrollUpdates > 1,
                        "内容变换被外部接管后必须退回安全偏移路径，不能继续驱动失效的旧变换。");
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    Keyboard.ClearFocus();
                    window?.Close();
                    PumpDispatcher();
                }
            },
            TimeSpan.FromSeconds(10),
            () => "外部渲染变换所有权验收超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void DailyBriefingRealizesOnlyBufferedViewportBlocks()
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
                        VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = articleView
                    };
                    window = new Window
                    {
                        Title = "Daily briefing viewport virtualization",
                        Width = 880,
                        Height = 520,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = viewer
                    };
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();
                    double initialExtentHeight = viewer.ExtentHeight;

                    int initiallyRealizedTextBlocks =
                        CountDescendants<TextBlock>(articleView);
                    Assert.InRange(initiallyRealizedTextBlocks, 2, 80);
                    Assert.True(viewer.ScrollableHeight > 4000d);

                    SmoothWheelScrolling.ScrollToImmediately(
                        viewer,
                        viewer.ScrollableHeight);
                    PumpDispatcher();
                    PumpUntil(
                        () => FindTextBlock(
                            articleView,
                            "早报正文段落 180") is not null,
                        TimeSpan.FromSeconds(2));
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
                    PumpFor(TimeSpan.FromMilliseconds(520d));
                    PumpUntil(
                        () => FindTextBlock(
                            articleView,
                            "早报正文段落 001") is not null,
                        TimeSpan.FromSeconds(2));

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
            TimeSpan.FromSeconds(10),
            () => "每日早报长文视口虚拟化验收超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
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
                    for (int groupIndex = 0; groupIndex < 13; groupIndex++)
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
                        VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = panel
                    };
                    window = new Window
                    {
                        Title = "Trend viewport virtualization",
                        Width = 960,
                        Height = 620,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = viewer
                    };
                    window.Show();
                    window.UpdateLayout();
                    PumpDispatcher();

                    int initialRealized =
                        groups.Count(group => group.IsContentRealized);
                    Assert.InRange(initialRealized, 2, 8);
                    Assert.True(viewer.ScrollableHeight > 3000d);

                    SmoothWheelScrolling.ScrollToImmediately(
                        viewer,
                        viewer.ScrollableHeight);
                    PumpUntil(
                        () => groups[^1].IsContentRealized,
                        TimeSpan.FromSeconds(2));

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
                        group => Assert.False(
                            group.IsContentRealized));
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

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

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
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        Assert.True(
                            SmoothWheelScrolling.HasActiveAnimation(briefing));
                    }
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

    private static T FindDescendant<T>(DependencyObject parent)
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

        throw new InvalidOperationException(
            $"未找到 {typeof(T).Name} 子控件。");
    }

    private static T? FindDescendantOrDefault<T>(DependencyObject parent)
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
}
