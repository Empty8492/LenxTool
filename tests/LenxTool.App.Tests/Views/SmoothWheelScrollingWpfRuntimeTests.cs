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
    public void HeavyPhysicalPageUsesCompositedFluentInertia()
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
                    stage = "applying motion settings";
                    themeService.ApplyReduceMotion(reduceMotion: false);
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
                        VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = content
                    };
                    stage = "creating offscreen window";
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
                    stage = "showing offscreen window";
                    window.Show();
                    stage = "updating initial layout";
                    window.UpdateLayout();
                    stage = "pumping initial dispatcher work";
                    PumpDispatcher();
                    stage = "raising wheel";

                    int logicalScrollUpdates = 0;
                    viewer.ScrollChanged += (_, eventArgs) =>
                    {
                        if (Math.Abs(eventArgs.VerticalChange) > 0.001d)
                        {
                            logicalScrollUpdates++;
                        }
                    };

                    double expectedTarget = ExpectedTarget(viewer);
                    bool expectsAnimation = RuntimeMotionAllowed()
                                            && expectedTarget > 0.01d;
                    RaiseWheel(content.Children[0]);
                    Assert.Equal(
                        expectsAnimation,
                        SmoothWheelScrolling.HasActiveAnimation(viewer));
                    if (!expectsAnimation)
                    {
                        // Windows 关闭客户端动画或把滚轮行数设为 0 时，生产代码应尊重系统设置并直接落位。
                        Assert.InRange(
                            viewer.VerticalOffset,
                            expectedTarget - 0.5d,
                            expectedTarget + 0.5d);
                        Assert.Equal(expectedTarget > 0.01d ? 1 : 0,
                            logicalScrollUpdates);
                        stage = "completed system-motion fallback assertions";
                        return;
                    }
                    viewer.UpdateLayout();
                    Assert.InRange(
                        GetVisualOffset(viewer, content),
                        0d,
                        1d);
                    PumpFor(TimeSpan.FromMilliseconds(45d));
                    double intermediateOffset = GetVisualOffset(viewer, content);
                    Assert.InRange(
                        intermediateOffset,
                        1d,
                        expectedTarget - 1d);
                    stage = "waiting for inertia completion";
                    PumpUntil(
                        () => !SmoothWheelScrolling.HasActiveAnimation(viewer),
                        TimeSpan.FromSeconds(2d));
                    Assert.InRange(
                        viewer.VerticalOffset,
                        expectedTarget - 0.5d,
                        expectedTarget + 0.5d);
                    Assert.Equal(1, logicalScrollUpdates);
                    stage = "completed assertions";
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
            () => $"重页面 Fluent 滚动验收在阶段“{stage}”超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void HeavyPhysicalPageReturnsToTopImmediately()
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
                    Assert.False(SmoothWheelScrolling.HasActiveAnimation(viewer));
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
            () => "重页面即时回顶验收超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void VirtualizedListReusesOneFluentMotionSessionForBurstInput()
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
                    ListBoxItem firstItem = Assert.IsType<ListBoxItem>(
                        list.ItemContainerGenerator.ContainerFromIndex(0));
                    double startingOffset = viewer.VerticalOffset;
                    bool motionAllowed = RuntimeMotionAllowed();
                    double expectedTarget = startingOffset;
                    double expectedVelocity = 0d;
                    bool expectsAnimation = false;
                    object? motionSession = null;
                    for (int notch = 0; notch < 10; notch++)
                    {
                        WheelMotionPlan expectedPlan =
                            SmoothWheelScrolling.CreateWheelMotionPlan(
                                motionAllowed
                                    ? startingOffset
                                    : expectedTarget,
                                pendingTargetOffset: expectedTarget,
                                currentVelocity: motionAllowed
                                    ? expectedVelocity
                                    : 0d,
                                viewer.ScrollableHeight,
                                viewer.ViewportHeight,
                                wheelDelta: -120,
                                SystemParameters.WheelScrollLines,
                                usesLogicalUnits: false,
                                motionAllowed,
                                WheelInputMode.Inertial);
                        expectedTarget = expectedPlan.TargetOffset;
                        expectedVelocity = expectedPlan.Velocity;
                        RaiseWheel(firstItem);
                        if (expectedPlan.ShouldAnimate)
                        {
                            expectsAnimation = true;
                            motionSession ??=
                                SmoothWheelScrolling
                                    .GetActiveAnimationSession(viewer);
                            Assert.Same(
                                motionSession,
                                SmoothWheelScrolling
                                    .GetActiveAnimationSession(viewer));
                        }
                    }
                    viewer.UpdateLayout();

                    if (!expectsAnimation)
                    {
                        Assert.False(
                            SmoothWheelScrolling.HasActiveAnimation(viewer));
                        Assert.InRange(
                            viewer.VerticalOffset,
                            expectedTarget - 0.5d,
                            expectedTarget + 0.5d);
                        return;
                    }

                    Assert.True(
                        SmoothWheelScrolling.HasActiveAnimation(viewer));
                    UIElement compositedContent =
                        Assert.IsAssignableFrom<UIElement>(viewer.Content);
                    Assert.InRange(
                        GetVisualOffset(viewer, compositedContent),
                        startingOffset - 0.5d,
                        startingOffset + 0.5d);
                    if (Math.Abs(expectedTarget - startingOffset)
                        > viewer.ViewportHeight + 0.5d)
                    {
                        // 连续输入越过一屏缓存后必须无跳变地交给逐帧逻辑路径，不能继续预提交不可见容器。
                        Assert.InRange(
                            viewer.VerticalOffset,
                            startingOffset - 0.5d,
                            startingOffset + 0.5d);
                    }
                    PumpUntil(
                        () => !SmoothWheelScrolling.HasActiveAnimation(viewer),
                        TimeSpan.FromSeconds(3d));
                    Assert.InRange(
                        viewer.VerticalOffset,
                        expectedTarget - 1d,
                        expectedTarget + 1d);
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
            () => "虚拟列表连续 Fluent 滚动验收超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void ExternalRenderTransformUsesLogicalFallbackWithoutBeingReplaced()
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
                    PumpDispatcher();
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
                    double expectedTarget = ExpectedTarget(viewer);
                    int logicalScrollUpdates = 0;
                    viewer.ScrollChanged += (_, eventArgs) =>
                    {
                        if (Math.Abs(eventArgs.VerticalChange) > 0.001d)
                        {
                            logicalScrollUpdates++;
                        }
                    };

                    RaiseWheel(content.Children[0]);
                    bool expectsAnimation = RuntimeMotionAllowed()
                                            && Math.Abs(
                                                expectedTarget
                                                - viewer.VerticalOffset)
                                            > 0.01d;
                    Assert.Equal(
                        expectsAnimation,
                        SmoothWheelScrolling.HasActiveAnimation(viewer));
                    if (!expectsAnimation)
                    {
                        Assert.Same(
                            externalTransform,
                            content.RenderTransform);
                        return;
                    }
                    PumpUntil(
                        () => !SmoothWheelScrolling.HasActiveAnimation(viewer),
                        TimeSpan.FromSeconds(2d));

                    Assert.Same(externalTransform, content.RenderTransform);
                    Assert.InRange(
                        viewer.VerticalOffset,
                        expectedTarget - 0.5d,
                        expectedTarget + 0.5d);
                    Assert.True(
                        logicalScrollUpdates > 1,
                        "外部占用 RenderTransform 时应回退到逐帧逻辑偏移，不能替换调用方的变换。");
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
            () => "外部渲染变换 Fluent 回退验收超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void DailyBriefingKeepsBufferedBlocksWithoutPerFrameRescans()
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

                    int deferredControlCount =
                        CountDescendants<ViewportDeferredContentControl>(
                            articleView);
                    Assert.True(deferredControlCount >= blocks.Length);
                    long evaluationCountBeforeWheel =
                        ViewportDeferredContentControl
                            .GetDeferredViewportEvaluationCount(viewer);
                    bool canExerciseAnimatedViewport =
                        RuntimeMotionAllowed()
                        && SystemParameters.WheelScrollLines != 0;
                    if (canExerciseAnimatedViewport)
                    {
                        double refreshTravel = Math.Min(
                            120d,
                            Math.Max(1d, viewer.ViewportHeight * 0.25d));
                        int precisionStep = Math.Max(
                            1,
                            (int)Math.Ceiling(refreshTravel / 2d));

                        // 使用高分辨率 delta 构造与系统滚轮行数无关的半阈值位移，第一段完成后不应扫描全文。
                        RaiseWheel(articleView, delta: -precisionStep);
                        Assert.True(
                            SmoothWheelScrolling.HasActiveAnimation(viewer));
                        PumpFor(TimeSpan.FromMilliseconds(100d));
                        PumpUntil(
                            () => !SmoothWheelScrolling
                                .HasActiveAnimation(viewer),
                            TimeSpan.FromSeconds(2d));
                        Assert.Equal(
                            evaluationCountBeforeWheel,
                            ViewportDeferredContentControl
                                .GetDeferredViewportEvaluationCount(viewer));

                        // 第二段让累计视觉位移越过四分之一屏阈值，只允许协调器统一评估一轮。
                        RaiseWheel(articleView, delta: -precisionStep);
                        PumpUntil(
                            () => !SmoothWheelScrolling
                                .HasActiveAnimation(viewer),
                            TimeSpan.FromSeconds(2d));
                        long evaluationDeltaAfterThreshold =
                            ViewportDeferredContentControl
                                .GetDeferredViewportEvaluationCount(viewer)
                            - evaluationCountBeforeWheel;
                        Assert.Equal(
                            (long)deferredControlCount,
                            evaluationDeltaAfterThreshold);
                        PumpDispatcher();
                        Assert.Equal(
                            evaluationCountBeforeWheel
                            + deferredControlCount,
                            ViewportDeferredContentControl
                                .GetDeferredViewportEvaluationCount(viewer));
                    }
                    else
                    {
                        // 远程桌面/辅助功能关闭客户端动画时，只校验系统策略被尊重；合并扫描由纯逻辑测试冻结。
                        RaiseWheel(articleView, delta: -60);
                        PumpDispatcher();
                        Assert.False(
                            SmoothWheelScrolling.HasActiveAnimation(viewer));
                    }

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
                    themeService.ApplyReduceMotion(reduceMotion: false);
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
    public void PlainAndDailyBriefingScrollViewersShareFluentWheelBehavior()
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

                    stage = "checking plain ScrollViewer Fluent landing target";
                    double expectedPlain = ExpectedTarget(plain);
                    bool expectsRuntimeAnimation = RuntimeMotionAllowed()
                                                   && expectedPlain > 0.01d;
                    RaiseWheel(plainContent);
                    plain.UpdateLayout();
                    Assert.InRange(plain.VerticalOffset, expectedPlain - 1d, expectedPlain + 1d);
                    Assert.Equal(
                        expectsRuntimeAnimation,
                        SmoothWheelScrolling.HasActiveAnimation(plain));

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
                    plain.UpdateLayout();
                    Assert.InRange(
                        plain.VerticalOffset,
                        expectedAccumulated - 1d,
                        expectedAccumulated + 1d);

                    stage = "checking repeated wheel input retargets one session";
                    SmoothWheelScrolling.ScrollToImmediately(plain, 0d);
                    PumpDispatcher();
                    RaiseWheel(plainContent);
                    plain.UpdateLayout();
                    Assert.InRange(plain.VerticalOffset, expectedPlain - 1d, expectedPlain + 1d);
                    object? firstSession = SmoothWheelScrolling
                        .GetActiveAnimationSession(plain);
                    RaiseWheel(plainContent);
                    plain.UpdateLayout();
                    if (expectsRuntimeAnimation)
                    {
                        Assert.NotNull(firstSession);
                        Assert.Same(
                            firstSession,
                            SmoothWheelScrolling
                                .GetActiveAnimationSession(plain));
                    }
                    else
                    {
                        Assert.Null(firstSession);
                        Assert.False(
                            SmoothWheelScrolling.HasActiveAnimation(plain));
                    }
                    Assert.InRange(
                        plain.VerticalOffset,
                        expectedAccumulated - 1d,
                        expectedAccumulated + 1d);

                    stage = "checking additive direction reversal";
                    SmoothWheelScrolling.ScrollToImmediately(plain, 0d);
                    PumpDispatcher();
                    RaiseWheel(plainContent, delta: -120);
                    RaiseWheel(plainContent, delta: 120);
                    PumpFor(TimeSpan.FromMilliseconds(300));
                    Assert.InRange(plain.VerticalOffset, 0d, 0.01d);

                    stage = "checking programmatic restore after immediate wheel";
                    RaiseWheel(plainContent, delta: -120);
                    SmoothWheelScrolling.ScrollToImmediately(plain, 200d);
                    PumpFor(TimeSpan.FromMilliseconds(300));
                    Assert.InRange(plain.VerticalOffset, 199d, 201d);

                    stage = "checking direct input after immediate wheel";
                    SmoothWheelScrolling.ScrollToImmediately(plain, 0d);
                    PumpDispatcher();
                    RaiseWheel(plainContent, delta: -120);
                    RaiseMouseDown(plainContent);
                    plain.ScrollToVerticalOffset(200d);
                    PumpFor(TimeSpan.FromMilliseconds(300));
                    Assert.InRange(plain.VerticalOffset, 199d, 201d);
                    double expectedAfterNativeScroll = Math.Min(
                        plain.ScrollableHeight,
                        200d + expectedPlain);
                    RaiseWheel(plainContent, delta: -120);
                    plain.UpdateLayout();
                    Assert.InRange(
                        plain.VerticalOffset,
                        expectedAfterNativeScroll - 1d,
                        expectedAfterNativeScroll + 1d);
                    SmoothWheelScrolling.Cancel(plain);

                    stage = "checking daily briefing parity";
                    double expectedBriefing = ExpectedTarget(briefing);
                    RaiseWheel(briefingContent);
                    briefing.UpdateLayout();
                    Assert.Equal(
                        expectsRuntimeAnimation,
                        SmoothWheelScrolling.HasActiveAnimation(briefing));
                    Assert.InRange(
                        briefing.VerticalOffset,
                        expectedBriefing - 1d,
                        expectedBriefing + 1d);
                    Assert.InRange(
                        Math.Abs(expectedBriefing - expectedPlain),
                        0d,
                        0.01d);

                    stage = "checking wheel to immediate back-to-top handoff";
                    SmoothWheelScrolling.ScrollToImmediately(briefing, 300d);
                    PumpDispatcher();
                    double expectedHandoffTarget = ExpectedTarget(briefing);
                    var handoffOffsets = new List<double>();
                    briefing.ScrollChanged += (_, _) =>
                        handoffOffsets.Add(briefing.VerticalOffset);
                    RaiseWheel(briefingContent);
                    PumpDispatcher();
                    AnimatedScrollViewer.SmoothScrollToTopCommand.Execute(
                        parameter: null,
                        target: briefing);
                    PumpDispatcher();
                    Assert.InRange(briefing.VerticalOffset, 0d, 0.01d);
                    Assert.False(
                        SmoothWheelScrolling.HasActiveAnimation(briefing));
                    Assert.All(
                        handoffOffsets,
                        offset => Assert.InRange(
                            offset,
                            0d,
                            Math.Max(300d, expectedHandoffTarget) + 1d));

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
                    Assert.Equal(
                        expectsRuntimeAnimation,
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
            () => $"Fluent 滚轮 WPF 验收在阶段“{stage}”超时。");

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
        WheelMotionPlan plan = SmoothWheelScrolling.CreateWheelMotionPlan(
            viewer.VerticalOffset,
            pendingTargetOffset: viewer.VerticalOffset,
            currentVelocity: 0d,
            viewer.ScrollableHeight,
            viewer.ViewportHeight,
            wheelDelta: -120,
            SystemParameters.WheelScrollLines,
            usesLogicalUnits: false,
            motionAllowed: true,
            WheelInputMode.Inertial);
        return plan.TargetOffset;
    }

    private static bool RuntimeMotionAllowed()
    {
        object? reduceMotion =
            Application.Current?.Resources["LenxTool.ReduceMotion"];
        return SystemParameters.ClientAreaAnimation
               && reduceMotion is not true;
    }

    private static double GetVisualOffset(
        ScrollViewer viewer,
        UIElement content)
    {
        double translation = content.RenderTransform
            is TranslateTransform transform
                ? transform.Y
                : 0d;
        return viewer.VerticalOffset - translation;
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
