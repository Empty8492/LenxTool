using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LenxTool.App.Controls;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Views;

[Collection(WpfRuntimeGroup.Name)]
public sealed class FeedThumbnailTests
{
    [Fact]
    public void RecycledThumbnailCancelsOldRequestAndUsesBoundedDecode()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                byte[] imageBytes = CreatePng(width: 1_000, height: 500);
                var downloader = new StubDownloader(imageBytes);
                FeedThumbnail.Configure(downloader);
                var thumbnail = new FeedThumbnail
                {
                    EntryId = "entry",
                    SourceUrl = "https://cdn.example/slow.jpg",
                    Referrer = "https://example.com/article",
                    AltText = "Recycled thumbnail",
                    Width = 320,
                    Height = 180
                };
                window = new()
                {
                    Width = 360,
                    Height = 220,
                    Left = -10_000,
                    Top = -10_000,
                    ShowInTaskbar = false,
                    Content = thumbnail
                };
                window.Show();
                PumpUntil(
                    () => downloader.SlowRequestStarted.Task.IsCompleted,
                    TimeSpan.FromSeconds(2));

                thumbnail.SourceUrl = "https://cdn.example/good.jpg";
                PumpUntil(
                    () => FindDescendant<Image>(thumbnail).Source is BitmapSource,
                    TimeSpan.FromSeconds(2));
                PumpUntil(
                    () => downloader.SlowRequestCancelled.Task.IsCompleted,
                    TimeSpan.FromSeconds(2));

                Assert.True(downloader.SlowRequestCancelled.Task.IsCompleted);
                BitmapSource bitmap = Assert.IsAssignableFrom<BitmapSource>(
                    FindDescendant<Image>(thumbnail).Source);
                Assert.Equal(360, bitmap.PixelWidth);
                Assert.Equal(180, bitmap.PixelHeight);

                thumbnail.SourceUrl = "https://cdn.example/missing.jpg";
                PumpUntil(
                    () => FindDescendant<TextBlock>(thumbnail).Text
                        == "缩略图暂时无法加载",
                    TimeSpan.FromSeconds(2));
                Assert.Null(FindDescendant<Image>(thumbnail).Source);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)), "Thumbnail WPF thread timed out.");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static byte[] CreatePng(int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x33;
            pixels[index + 1] = 0x66;
            pixels[index + 2] = 0x99;
            pixels[index + 3] = 0xFF;
        }
        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static T FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            try
            {
                return FindDescendant<T>(child);
            }
            catch (InvalidOperationException)
            {
            }
        }
        throw new InvalidOperationException($"Could not find {typeof(T).Name}.");
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException("Timed out while pumping the WPF dispatcher.");
            }
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class StubDownloader(byte[] bytes) : IArticleImageStreamDownloader
    {
        public TaskCompletionSource SlowRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SlowRequestCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ArticleImageStreamContent?> OpenAsync(
            string entryId,
            string imageUrl,
            string? referrer,
            ArticleImageDownloadBudget budget,
            CancellationToken cancellationToken)
        {
            if (imageUrl.EndsWith("slow.jpg", StringComparison.Ordinal))
            {
                SlowRequestStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    SlowRequestCancelled.TrySetResult();
                    throw;
                }
            }
            return imageUrl.EndsWith("good.jpg", StringComparison.Ordinal)
                ? new(
                    new MemoryStream(bytes, writable: false),
                    "image/png",
                    fromCache: true)
                : null;
        }
    }
}
