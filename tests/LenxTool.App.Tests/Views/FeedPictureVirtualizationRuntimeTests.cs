using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LenxTool.App.Controls;
using LenxTool.App.ViewModels;
using LenxTool.App.Views;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Views;

[Collection(WpfRuntimeGroup.Name)]
public sealed class FeedPictureVirtualizationRuntimeTests
{
    [Fact]
    public void RealPictureViewVirtualizesOneThousandThumbnailCardsWhileScrolling()
    {
        Exception? failure = null;
        string stage = "starting";
        WpfRuntimeHost.Run(() =>
        {
            Window? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var pictureFeed = new PictureFeedData();
                stage = "creating items";
                FeedContentItem[] items = Enumerable.Range(0, 1000)
                    .Select(CreatePictureItem)
                    .ToArray();
                foreach (FeedContentItem item in items)
                {
                    pictureFeed.Items.Add(item);
                }
                for (int index = 0; index < items.Length; index += 3)
                {
                    pictureFeed.Rows.Add(new(items.Skip(index).Take(3).ToArray()));
                }

                var downloader = new CountingDownloader();
                stage = "creating view";
                FeedThumbnail.Configure(downloader);
                var view = new FeedPictureView
                {
                    DataContext = new PictureViewData(pictureFeed)
                };
                window = new()
                {
                    Width = 1180,
                    Height = 760,
                    Left = -10000,
                    Top = -10000,
                    ShowInTaskbar = false,
                    Content = view
                };
                window.Show();

                stage = "waiting for first viewport";
                PagedListBox list = FindDescendant<PagedListBox>(view);
                PumpUntil(
                    () => CountRealized(list) > 0
                          && FindDescendants<FeedThumbnail>(view).Count > 0
                          && downloader.CallCount > 0,
                    TimeSpan.FromSeconds(5));

                int initialRealizedRows = CountRealized(list);
                int initialThumbnails = FindDescendants<FeedThumbnail>(view).Count;
                Assert.Equal(334, list.Items.Count);
                Assert.InRange(initialRealizedRows, 1, 40);
                Assert.InRange(initialThumbnails, 1, 120);
                Assert.InRange(downloader.CallCount, 1, 120);

                stage = "checking 200 percent layout scale";
                ScrollViewer scrollViewer = FindDescendant<ScrollViewer>(list);
                Assert.Equal(0, scrollViewer.ScrollableWidth);
                list.LayoutTransform = new ScaleTransform(2d, 2d);
                list.UpdateLayout();
                Assert.Equal(0, scrollViewer.ScrollableWidth);
                Assert.InRange(CountRealized(list), 1, 40);
                list.LayoutTransform = Transform.Identity;
                list.UpdateLayout();

                stage = "scrolling to end";
                list.ScrollIntoView(list.Items[333]);
                PumpUntil(
                    () =>
                    {
                        list.UpdateLayout();
                        return scrollViewer.VerticalOffset > 0
                               && list.ItemContainerGenerator.ContainerFromIndex(333)
                                   is not null
                               && list.ItemContainerGenerator.ContainerFromIndex(0)
                                   is null;
                    },
                    TimeSpan.FromSeconds(5));

                int finalRealizedRows = CountRealized(list);
                int finalThumbnails = FindDescendants<FeedThumbnail>(view).Count;
                Assert.InRange(finalRealizedRows, 1, 40);
                Assert.InRange(finalThumbnails, 1, 120);
                Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(333));
                Assert.True(
                    downloader.CallCount < items.Length / 4,
                    $"Virtualized view loaded {downloader.CallCount} of {items.Length} thumbnails.");
                stage = "completed assertions";
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                stage = "closing window";
                window?.Close();
                SynchronizationContext.SetSynchronizationContext(null);
                stage = "finished";
            }
        },
            TimeSpan.FromSeconds(30),
            () => $"Real picture-view virtualization acceptance timed out at stage: {stage}.");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static FeedContentItem CreatePictureItem(int index)
    {
        DateTimeOffset now = new(2026, 7, 27, 8, 0, 0, TimeSpan.Zero);
        string entryId = $"picture-{index:D4}";
        var entry = new FeedEntry(
            entryId,
            "30000000-0000-4000-8000-000000000001",
            $"external-{index:D4}",
            $"https://example.com/articles/{index}",
            $"Picture {index:D4}",
            "Author",
            now.AddMinutes(-index),
            null,
            "Summary",
            "Content",
            [],
            [
                new(
                    $"https://images.example.com/{index}.png",
                    "image/png",
                    68,
                    null)
            ],
            new string((char)('a' + index % 6), 64),
            now);
        return new(new(
            entry,
            "Picture Feed",
            "Pictures",
            State: null,
            Favorite: null));
    }

    private static int CountRealized(ItemsControl itemsControl) =>
        Enumerable.Range(0, itemsControl.Items.Count)
            .Count(index =>
                itemsControl.ItemContainerGenerator.ContainerFromIndex(index)
                    is not null);

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
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                results.Add(match);
            }
            AddDescendants(child, results);
        }
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
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

    private sealed record PictureViewData(PictureFeedData PictureFeed);

    private sealed class PictureFeedData
    {
        public string Status { get; } = "1000 pictures";
        public ObservableCollection<FeedContentItem> Items { get; } = [];
        public ObservableCollection<FeedContentRow> Rows { get; } = [];
        public ObservableCollection<object> Categories { get; } = [];
        public ObservableCollection<object> Feeds { get; } = [];
    }

    private sealed class CountingDownloader : IArticleImageStreamDownloader
    {
        private static readonly byte[] PngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ArticleImageStreamContent?> OpenAsync(
            string entryId,
            string imageUrl,
            string? referrer,
            ArticleImageDownloadBudget budget,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult<ArticleImageStreamContent?>(new(
                new MemoryStream(PngBytes, writable: false),
                "image/png",
                fromCache: true));
        }
    }
}
