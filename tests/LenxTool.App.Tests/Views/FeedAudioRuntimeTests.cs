using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LenxTool.App.Controls;
using LenxTool.App.Mvvm;
using LenxTool.App.ViewModels;
using LenxTool.App.Views;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Views;

[Collection(WpfRuntimeGroup.Name)]
public sealed class FeedAudioRuntimeTests
{
    [Fact]
    public void AudioViewVirtualizesAtCompactSizeAndTwoHundredPercentScale()
    {
        Exception? failure = null;
        string stage = "starting";
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherSynchronizationContext(
                            Dispatcher.CurrentDispatcher));
                    var audioFeed = new AudioFeedData();
                    foreach (FeedAudioItem item in Enumerable
                                 .Range(0, 1000)
                                 .Select(CreateAudioItem))
                    {
                        audioFeed.Items.Add(item);
                    }
                    audioFeed.SelectedItem = audioFeed.Items[0];
                    var view = new FeedAudioView
                    {
                        DataContext = new AudioViewData(audioFeed)
                    };
                    window = new()
                    {
                        Width = 900,
                        Height = 620,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = view
                    };

                    stage = "showing compact layout";
                    window.Show();
                    PagedListBox list = FindDescendant<PagedListBox>(view);
                    PumpUntil(
                        () => CountRealized(list) > 0,
                        TimeSpan.FromSeconds(5));
                    ScrollViewer scroll = FindDescendant<ScrollViewer>(list);

                    Assert.Equal(1000, list.Items.Count);
                    Assert.InRange(CountRealized(list), 1, 40);
                    Assert.Equal(0, scroll.ScrollableWidth);
                    Assert.True(view.ActualWidth >= 850);

                    stage = "checking 200 percent layout";
                    window.Width = 1800;
                    window.Height = 1240;
                    view.LayoutTransform = new ScaleTransform(2d, 2d);
                    view.UpdateLayout();

                    Assert.Equal(0, scroll.ScrollableWidth);
                    Assert.InRange(CountRealized(list), 1, 40);
                    string[] actionNames =
                    [
                        "清除音频筛选",
                        "应用音频筛选",
                        "播放或暂停所选音频",
                        "将所选音频送入转写",
                        "请求在浏览器打开音频原文"
                    ];
                    Assert.All(actionNames, name =>
                    {
                        Button button = Assert.Single(
                            FindDescendants<Button>(view),
                            candidate =>
                                AutomationProperties.GetName(candidate)
                                == name);
                        Assert.True(
                            button.ActualHeight >= 24,
                            $"Button '{name}' was clipped.");
                    });
                    stage = "completed";
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    window?.Close();
                    SynchronizationContext.SetSynchronizationContext(null);
                }
            },
            TimeSpan.FromSeconds(30),
            () => $"Audio layout acceptance timed out at stage: {stage}.");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static FeedAudioItem CreateAudioItem(int index)
    {
        DateTimeOffset now =
            new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);
        string id = $"audio-{index:D4}";
        var entry = new FeedEntry(
            id,
            "30000000-0000-4000-8000-000000000001",
            id,
            $"https://example.com/{id}",
            $"Podcast episode {index:D4}",
            "Author",
            now.AddMinutes(-index),
            null,
            "Summary",
            "Content",
            [],
            [
                new(
                    $"https://cdn.example/{id}.mp3",
                    "audio/mpeg",
                    1024 + index,
                    id)
            ],
            new string((char)('a' + index % 6), 64),
            now);
        return new(new(new(
            entry,
            "Podcast Feed",
            "Audio",
            new(
                entry.Id,
                "default",
                false,
                false,
                false,
                index % 100,
                "",
                now),
            Favorite: null)));
    }

    private static int CountRealized(ItemsControl itemsControl) =>
        Enumerable.Range(0, itemsControl.Items.Count)
            .Count(index =>
                itemsControl.ItemContainerGenerator
                    .ContainerFromIndex(index) is not null);

    private static T FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        List<T> results = FindDescendants<T>(root);
        return results.Count > 0
            ? results[0]
            : throw new InvalidOperationException(
                $"Could not find {typeof(T).Name}.");
    }

    private static List<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var results = new List<T>();
        AddDescendants(root, results);
        return results;
    }

    private static void AddDescendants<T>(
        DependencyObject root,
        ICollection<T> results)
        where T : DependencyObject
    {
        for (int index = 0;
             index < VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                results.Add(match);
            }
            AddDescendants(child, results);
        }
    }

    private static void PumpUntil(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException(
                    "Timed out while pumping the WPF dispatcher.");
            }
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed record AudioViewData(AudioFeedData AudioFeed);

    private sealed class AudioFeedData
    {
        public AudioFeedData()
        {
            PlayPauseCommand = new(() => { });
            QueueTranscriptionCommand = new(_ => Task.CompletedTask);
            RequestExternalOpenCommand = new(() => { });
            ConfirmExternalOpenCommand = new(() => { });
            CancelExternalOpenCommand = new(() => { });
        }

        public ObservableCollection<FeedAudioItem> Items { get; } = [];
        public FeedAudioItem? SelectedItem { get; set; }
        public FeedData Feed { get; } = new();
        public RelayCommand PlayPauseCommand { get; }
        public AsyncRelayCommand QueueTranscriptionCommand { get; }
        public RelayCommand RequestExternalOpenCommand { get; }
        public RelayCommand ConfirmExternalOpenCommand { get; }
        public RelayCommand CancelExternalOpenCommand { get; }
        public string Status { get; } = "Ready";
        public string PlayPauseLabel { get; } = "播放";
        public double DurationSeconds { get; } = 3600;
        public double SeekPositionSeconds { get; set; } = 120;
        public bool CanSeek { get; } = true;
        public string PositionText { get; } = "2:00";
        public string DurationText { get; } = "1:00:00";
        public bool HasPendingExternalConfirmation { get; }
    }

    private sealed class FeedData
    {
        public ObservableCollection<object> Categories { get; } = [];
        public ObservableCollection<object> Feeds { get; } = [];
        public AsyncRelayCommand ClearFiltersCommand { get; } =
            new(_ => Task.CompletedTask);
        public AsyncRelayCommand ApplyFiltersCommand { get; } =
            new(_ => Task.CompletedTask);
        public AsyncRelayCommand LoadMoreCommand { get; } =
            new(_ => Task.CompletedTask);
    }
}
