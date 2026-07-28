using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LenxTool.App.Controls;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.App.Views;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Views;

[Collection(WpfRuntimeGroup.Name)]
public sealed class FeedViewSwitchingWpfRuntimeTests
{
    [Fact]
    public void FiveFeedViewsReuseVirtualizedScrollContextsAcrossSupportedLayouts()
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
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherSynchronizationContext(
                            Dispatcher.CurrentDispatcher));
                    themeService.ApplyReduceMotion(reduceMotion: true);
                    var data = new FeedViewData();
                    Populate(data, itemCount: 300);
                    var view = new FeedTimelineView
                    {
                        DataContext = data
                    };
                    var outerScroll = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility =
                            ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility =
                            ScrollBarVisibility.Auto,
                        Content = view
                    };
                    window = new()
                    {
                        Width = 900,
                        Height = 620,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = outerScroll
                    };

                    stage = "showing compact five-view layout";
                    window.Show();
                    window.Activate();
                    PumpDispatcher();
                    TabControl tabs = FindDescendant<TabControl>(
                        view,
                        candidate =>
                            AutomationProperties.GetName(candidate)
                            == "Feed 视图切换");
                    Assert.Equal(5, tabs.Items.Count);
                    Assert.Equal(0, outerScroll.ScrollableWidth);
                    Assert.InRange(
                        view.ActualWidth,
                        1,
                        outerScroll.ViewportWidth);
                    Assert.Equal(
                        TimeSpan.Zero,
                        Assert.IsType<Duration>(
                            Application.Current.Resources[
                                "LenxTool.MotionDuration"])
                            .TimeSpan);

                    var lists = new PagedListBox[5];
                    var offsets = new double[5];
                    int[] expectedCounts = [300, 100, 300, 300, 300];
                    for (int index = 0; index < tabs.Items.Count; index++)
                    {
                        stage = $"virtualizing view {index}";
                        tabs.SelectedIndex = index;
                        PumpDispatcher();
                        PagedListBox list =
                            FindDescendant<PagedListBox>(view, _ => true);
                        lists[index] = list;
                        PumpUntil(
                            () => CountRealized(list) > 0,
                            TimeSpan.FromSeconds(5));
                        ScrollViewer scroll =
                            FindDescendant<ScrollViewer>(list, _ => true);
                        Assert.Equal(expectedCounts[index], list.Items.Count);
                        Assert.InRange(CountRealized(list), 1, 40);
                        Assert.Equal(0, scroll.ScrollableWidth);

                        int targetIndex = list.Items.Count - 1;
                        list.ScrollIntoView(list.Items[targetIndex]);
                        PumpUntil(
                            () =>
                            {
                                list.UpdateLayout();
                                return scroll.VerticalOffset > 0
                                       && list.ItemContainerGenerator
                                               .ContainerFromIndex(targetIndex)
                                           is not null;
                            },
                            TimeSpan.FromSeconds(5));
                        offsets[index] = scroll.VerticalOffset;
                        Assert.InRange(CountRealized(list), 1, 40);
                    }

                    stage = "checking keyboard navigation";
                    tabs.SelectedIndex = 0;
                    outerScroll.ScrollToTop();
                    PumpDispatcher();
                    for (int expectedIndex = 1;
                         expectedIndex < tabs.Items.Count;
                         expectedIndex++)
                    {
                        var selectedTab =
                            Assert.IsType<TabItem>(
                                tabs.Items[expectedIndex - 1]);
                        Assert.True(selectedTab.Focus());
                        Keyboard.Focus(selectedTab);
                        selectedTab.RaiseEvent(
                            new KeyEventArgs(
                                Keyboard.PrimaryDevice,
                                PresentationSource.FromVisual(selectedTab),
                                Environment.TickCount,
                                Key.Right)
                            {
                                RoutedEvent = Keyboard.KeyDownEvent
                            });
                        PumpDispatcher();
                        Assert.Equal(expectedIndex, tabs.SelectedIndex);
                    }

                    stage = "restoring per-view scroll contexts";
                    for (int index = 0; index < tabs.Items.Count; index++)
                    {
                        tabs.SelectedIndex = index;
                        PumpDispatcher();
                        PagedListBox current =
                            FindDescendant<PagedListBox>(view, _ => true);
                        ScrollViewer scroll =
                            FindDescendant<ScrollViewer>(current, _ => true);
                        Assert.Same(lists[index], current);
                        Assert.Equal(offsets[index], scroll.VerticalOffset, 3);
                        Assert.InRange(CountRealized(current), 1, 40);
                    }

                    stage = "checking 200 percent scale";
                    window.Width = 1800;
                    window.Height = 1240;
                    view.LayoutTransform = new ScaleTransform(2d, 2d);
                    for (int index = 0; index < tabs.Items.Count; index++)
                    {
                        tabs.SelectedIndex = index;
                        window.UpdateLayout();
                        PumpDispatcher();
                        PagedListBox current =
                            FindDescendant<PagedListBox>(view, _ => true);
                        ScrollViewer scroll =
                            FindDescendant<ScrollViewer>(current, _ => true);
                        Assert.Equal(0, scroll.ScrollableWidth);
                        Assert.InRange(CountRealized(current), 1, 40);
                    }
                    Assert.Equal(0, outerScroll.ScrollableWidth);
                    stage = "completed assertions";
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    themeService.ApplyReduceMotion(reduceMotion: false);
                    window?.Close();
                    SynchronizationContext.SetSynchronizationContext(null);
                }
            },
            TimeSpan.FromSeconds(35),
            () =>
                "Five-view WPF acceptance timed out at stage: "
                + stage
                + ".");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static void Populate(FeedViewData data, int itemCount)
    {
        for (int index = 0; index < itemCount; index++)
        {
            FeedTimelineItem timeline = CreateTimelineItem(index);
            var content = new FeedContentItem(timeline);
            data.TimelineEntries.Add(timeline);
            data.PictureFeed.Items.Add(content);
            data.AudioFeed.Items.Add(new(content));
            data.VideoFeed.Items.Add(new(content));
            data.NotificationFeed.Items.Add(content);
        }
        for (int index = 0; index < itemCount; index += 3)
        {
            data.PictureFeed.Rows.Add(
                new(
                    data.PictureFeed.Items
                        .Skip(index)
                        .Take(3)
                        .ToArray()));
        }
        data.SelectedTimelineEntry = data.TimelineEntries[0];
        data.AudioFeed.SelectedItem = data.AudioFeed.Items[0];
        data.VideoFeed.SelectedItem = data.VideoFeed.Items[0];
        data.NotificationFeed.SelectedItem =
            data.NotificationFeed.Items[0];
    }

    private static FeedTimelineItem CreateTimelineItem(int index)
    {
        DateTimeOffset now =
            new(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);
        string id = $"mixed-{index:D4}";
        var entry = new FeedEntry(
            id,
            "30000000-0000-4000-8000-000000000001",
            id,
            $"https://example.com/{id}",
            $"Mixed feed item {index:D4}",
            "Author",
            now.AddMinutes(-index),
            null,
            "A concise mixed-feed summary.",
            "Content",
            [],
            [],
            new string((char)('a' + index % 6), 64),
            now);
        return new(
            entry,
            "Mixed Feed",
            "Performance",
            State: null,
            Favorite: null);
    }

    private static int CountRealized(ItemsControl itemsControl) =>
        Enumerable.Range(0, itemsControl.Items.Count)
            .Count(index =>
                itemsControl.ItemContainerGenerator
                    .ContainerFromIndex(index) is not null);

    private static T FindDescendant<T>(
        DependencyObject root,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (int index = 0;
             index < VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match))
            {
                return match;
            }
            try
            {
                return FindDescendant(child, predicate);
            }
            catch (InvalidOperationException)
            {
            }
        }
        throw new InvalidOperationException(
            $"Could not find {typeof(T).Name} in the visual tree.");
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
                    "Timed out while pumping the WPF dispatcher.");
            }
            PumpDispatcher();
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class FeedViewData : ObservableObject
    {
        private int _selectedFeedViewIndex;

        public int SelectedFeedViewIndex
        {
            get => _selectedFeedViewIndex;
            set => SetProperty(ref _selectedFeedViewIndex, value);
        }
        public ObservableCollection<FeedTimelineItem> TimelineEntries
        {
            get;
        } = [];
        public FeedTimelineItem? SelectedTimelineEntry { get; set; }
        public string TimelineEntrySummary { get; } = "300 items";
        public ContentFeedData PictureFeed { get; } = new();
        public AudioFeedData AudioFeed { get; } = new();
        public VideoFeedData VideoFeed { get; } = new();
        public ContentFeedData NotificationFeed { get; } = new();
    }

    private sealed class ContentFeedData
    {
        public ObservableCollection<FeedContentItem> Items { get; } = [];
        public ObservableCollection<FeedContentRow> Rows { get; } = [];
        public ObservableCollection<object> Categories { get; } = [];
        public ObservableCollection<object> Feeds { get; } = [];
        public FeedContentItem? SelectedItem { get; set; }
        public string Status { get; } = "300 items";
    }

    private sealed class AudioFeedData
    {
        public ObservableCollection<FeedAudioItem> Items { get; } = [];
        public FeedAudioItem? SelectedItem { get; set; }
        public ContentFeedData Feed { get; } = new();
        public string Status { get; } = "300 items";
    }

    private sealed class VideoFeedData
    {
        public ObservableCollection<FeedVideoItem> Items { get; } = [];
        public FeedVideoItem? SelectedItem { get; set; }
        public ContentFeedData Feed { get; } = new();
        public string Status { get; } = "300 items";
    }
}
