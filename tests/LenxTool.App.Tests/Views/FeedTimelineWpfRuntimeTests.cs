using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LenxTool.App.Controls;
using LenxTool.App.ViewModels;
using LenxTool.App.Views;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.App.Tests.Views;

public sealed class FeedTimelineWpfRuntimeTests
{
    [Fact]
    public void LongArticleRestoresPrivateStateAcrossRealWpfViewRecreation()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "Lenx Tools WPF timeline tests",
            Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            LenxTool.App.App? application = null;
            Window? window = null;
            Window? reopenedWindow = null;
            Window? cachedImageWindow = null;
            Window? missingImageWindow = null;
            Window? filtersWindow = null;
            NewsCenterViewModel? viewModel = null;
            NewsCenterViewModel? reopenedViewModel = null;
            try
            {
                var paths = new AppPaths(testRoot);
                using var database = new SqliteDatabase(
                    paths,
                    NullLogger<SqliteDatabase>.Instance);
                database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                var states = new EntryStateRepository(database);
                var favorites = new FavoriteRepository(database);
                var assetStore = new EntryAssetStore(
                    database,
                    paths,
                    AssetCacheOptions.Default);
                const string cachedImageUrl = "https://images.example/offline.png";
                assetStore.PutAsync(
                    "cached-image-entry",
                    cachedImageUrl,
                    "image/png",
                    new MemoryStream(CreateOnePixelPng(), writable: false),
                    CancellationToken.None).GetAwaiter().GetResult();
                var noNetworkResolver = new NoNetworkResolver();
                var noNetworkTransport = new NoNetworkTransport();
                using var imageDownloader = new CachedArticleImageDownloader(
                    assetStore,
                    noNetworkResolver,
                    noNetworkTransport,
                    FeedDiscoveryOptions.Default,
                    ArticleImageDownloadOptions.Default,
                    AssetCacheOptions.Default,
                    TimeProvider.System);
                ArticleImageBlockFactory.Configure(imageDownloader);
                FeedEntry entry = CreateLongEntry();
                EntryState initialState = states.PatchAsync(
                    entry.Id,
                    "default",
                    new(
                        IsRead: true,
                        IsStarred: true,
                        Progress: 55,
                        Note: "重启后继续阅读"),
                    CancellationToken.None).GetAwaiter().GetResult();
                FavoriteItem favorite = favorites.UpsertAsync(
                    "feed_entry",
                    entry.Id,
                    initialState.Note,
                    CancellationToken.None).GetAwaiter().GetResult();
                TagItem tag = favorites.UpsertTagAsync(
                    "稍后精读",
                    "#4B6B88",
                    CancellationToken.None).GetAwaiter().GetResult();
                favorites.SetTagsAsync(
                    favorite.EntityType,
                    favorite.EntityId,
                    [tag.Id],
                    CancellationToken.None).GetAwaiter().GetResult();

                application = new LenxTool.App.App
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                application.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                var cachedArticleView = new RichArticleView
                {
                    Article = CreateImageArticle(
                        "cached-image-entry",
                        cachedImageUrl,
                        "离线缓存图片")
                };
                cachedImageWindow = new Window
                {
                    Width = 640,
                    Height = 480,
                    Left = -10000,
                    Top = -10000,
                    ShowInTaskbar = false,
                    Content = cachedArticleView
                };
                cachedImageWindow.Show();
                Assert.DoesNotContain(
                    FindDescendants<TextBlock>(cachedArticleView)
                        .SelectMany(textBlock => textBlock.Inlines.OfType<Run>()),
                    run => run.Text.Contains("查看网页原文", StringComparison.Ordinal));
                Image cachedImage = FindDescendant<Image>(
                    cachedArticleView,
                    element => AutomationProperties.GetName(element) == "离线缓存图片");
                PumpUntil(
                    () => cachedImage.Source is not null
                          && cachedImage.Visibility == Visibility.Visible,
                    TimeSpan.FromSeconds(5));
                Assert.Equal(0, noNetworkResolver.CallCount);
                Assert.Equal(0, noNetworkTransport.CallCount);
                cachedImageWindow.Close();
                cachedImageWindow = null;

                using var offlineImageDownloader = new CachedArticleImageDownloader(
                    new EmptyAssetStore(),
                    noNetworkResolver,
                    noNetworkTransport,
                    FeedDiscoveryOptions.Default,
                    ArticleImageDownloadOptions.Default,
                    AssetCacheOptions.Default,
                    TimeProvider.System);
                ArticleImageBlockFactory.Configure(offlineImageDownloader);
                const string missingImageUrl = "https://missing.example/offline.png";
                var missingArticleView = new RichArticleView
                {
                    Article = CreateImageArticle(
                        "missing-image-entry",
                        missingImageUrl,
                        "离线缺失图片")
                };
                missingImageWindow = CreateImageWindow(missingArticleView);
                missingImageWindow.Show();
                TextBlock missingStatus = FindImageStatus(
                    missingArticleView,
                    "离线缺失图片");
                PumpUntil(
                    () => missingStatus.Text == "图片暂时无法加载，可通过“查看网页原文”打开。",
                    TimeSpan.FromSeconds(5));
                Assert.Equal(1, noNetworkResolver.CallCount);
                Assert.Equal(0, noNetworkTransport.CallCount);
                missingImageWindow.Close();

                missingArticleView = new RichArticleView
                {
                    Article = CreateImageArticle(
                        "missing-image-entry",
                        missingImageUrl,
                        "离线缺失图片")
                };
                missingImageWindow = CreateImageWindow(missingArticleView);
                missingImageWindow.Show();
                missingStatus = FindImageStatus(
                    missingArticleView,
                    "离线缺失图片");
                PumpUntil(
                    () => missingStatus.Text == "图片暂时无法加载，可通过“查看网页原文”打开。",
                    TimeSpan.FromSeconds(5));
                Assert.Equal(1, noNetworkResolver.CallCount);
                Assert.Equal(0, noNetworkTransport.CallCount);
                missingImageWindow.Close();
                missingImageWindow = null;

                viewModel = CreateViewModel(states, favorites);
                var filtersView = new FeedTimelineFiltersView
                {
                    DataContext = viewModel
                };
                filtersWindow = new Window
                {
                    Width = 1080,
                    Height = 260,
                    Left = -10000,
                    Top = -10000,
                    ShowInTaskbar = false,
                    Content = filtersView
                };
                filtersWindow.Show();
                ComboBox readFilter = FindDescendant<ComboBox>(
                    filtersView,
                    element => AutomationProperties.GetName(element)
                        == "Feed 阅读状态筛选");
                PumpUntil(
                    () => FindDescendants<TextBlock>(readFilter)
                        .Any(textBlock => textBlock.Text == "全部"),
                    TimeSpan.FromSeconds(5));
                Assert.DoesNotContain(
                    FindDescendants<TextBlock>(readFilter),
                    textBlock => textBlock.Text.Contains(
                        nameof(FeedTimelineReadFilterOption),
                        StringComparison.Ordinal));
                filtersWindow.Close();
                filtersWindow = null;

                FeedTimelineItem item = new(
                    entry,
                    "Runtime Feed",
                    "Acceptance",
                    initialState,
                    favorite);
                viewModel.TimelineEntries.Add(item);
                viewModel.SelectedTimelineEntry = item;
                PumpUntil(
                    () => viewModel.SelectedTimelineEditorLoad.IsCompleted,
                    TimeSpan.FromSeconds(5));
                viewModel.SelectedTimelineEditorLoad.GetAwaiter().GetResult();

                var view = new FeedTimelineBrowserView
                {
                    DataContext = viewModel
                };
                window = CreateWindow(view);
                window.Show();
                window.Activate();

                var scrollViewer = Assert.IsType<ScrollViewer>(
                    view.FindName("ArticleScrollViewer"));
                PumpUntil(
                    () => scrollViewer.ExtentHeight > scrollViewer.ViewportHeight + 1,
                    TimeSpan.FromSeconds(5));
                Assert.InRange(ReadProgress(scrollViewer), 50, 60);

                TextBox noteEditor = FindDescendant<TextBox>(
                    view,
                    element => AutomationProperties.GetName(element) == "Feed 私人备注");
                Assert.True(noteEditor.Focus());
                Keyboard.Focus(noteEditor);
                PumpDispatcher();
                Assert.True(noteEditor.IsKeyboardFocusWithin);

                scrollViewer.ScrollToVerticalOffset(
                    (scrollViewer.ExtentHeight - scrollViewer.ViewportHeight) * 0.73);
                PumpUntil(
                    () => viewModel.TimelineProgressWrite.IsCompleted,
                    TimeSpan.FromSeconds(5));
                viewModel.TimelineProgressWrite.GetAwaiter().GetResult();

                IReadOnlyDictionary<string, EntryState> persisted = states.GetAsync(
                    [entry.Id],
                    "default",
                    CancellationToken.None).GetAwaiter().GetResult();
                EntryState persistedState = Assert.Single(persisted).Value;
                Assert.InRange(persistedState.Progress, 68, 78);

                window.Close();
                window = null;
                viewModel.Dispose();
                viewModel = null;
                PumpDispatcher();

                FavoriteItem reopenedFavorite = Assert.IsType<FavoriteItem>(
                    favorites.GetAsync(
                        "feed_entry",
                        entry.Id,
                        CancellationToken.None).GetAwaiter().GetResult());
                IReadOnlyList<TagItem> reopenedTags = favorites.GetTagsForEntityAsync(
                    "feed_entry",
                    entry.Id,
                    CancellationToken.None).GetAwaiter().GetResult();
                EntryState reopenedState = Assert.Single(
                    states.GetAsync(
                        [entry.Id],
                        "default",
                        CancellationToken.None).GetAwaiter().GetResult()).Value;

                reopenedViewModel = CreateViewModel(states, favorites);
                FeedTimelineItem reopenedItem = new(
                    entry,
                    "Runtime Feed",
                    "Acceptance",
                    reopenedState,
                    reopenedFavorite);
                reopenedViewModel.TimelineEntries.Add(reopenedItem);
                reopenedViewModel.SelectedTimelineEntry = reopenedItem;
                PumpUntil(
                    () => reopenedViewModel.SelectedTimelineEditorLoad.IsCompleted,
                    TimeSpan.FromSeconds(5));
                reopenedViewModel.SelectedTimelineEditorLoad.GetAwaiter().GetResult();

                var reopenedView = new FeedTimelineBrowserView
                {
                    DataContext = reopenedViewModel
                };
                reopenedWindow = CreateWindow(reopenedView);
                reopenedWindow.Show();
                var reopenedScrollViewer = Assert.IsType<ScrollViewer>(
                    reopenedView.FindName("ArticleScrollViewer"));
                PumpUntil(
                    () => reopenedScrollViewer.ExtentHeight > reopenedScrollViewer.ViewportHeight + 1,
                    TimeSpan.FromSeconds(5));

                Assert.InRange(
                    ReadProgress(reopenedScrollViewer),
                    persistedState.Progress - 5,
                    persistedState.Progress + 5);
                Assert.Equal("重启后继续阅读", reopenedViewModel.SelectedTimelineNote);
                Assert.Equal(tag.Id, Assert.Single(reopenedViewModel.SelectedTimelineTags).Id);
                Assert.Equal(tag.Id, Assert.Single(reopenedTags).Id);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                missingImageWindow?.Close();
                cachedImageWindow?.Close();
                filtersWindow?.Close();
                reopenedWindow?.Close();
                window?.Close();
                reopenedViewModel?.Dispose();
                viewModel?.Dispose();
                application?.Shutdown();
                SynchronizationContext.SetSynchronizationContext(null);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF acceptance thread timed out.");

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static NewsCenterViewModel CreateViewModel(
        IEntryStateRepository states,
        IFavoriteRepository favorites) =>
        new(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new StubFeedCatalogSyncService(),
            states,
            favorites,
            new StubFeedFullTextQueueService(),
            null!);

    private static Window CreateWindow(FeedTimelineBrowserView view) =>
        new()
        {
            Title = "Feed timeline runtime acceptance",
            Width = 1180,
            Height = 760,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            Content = view
        };

    private static Window CreateImageWindow(RichArticleView view) =>
        new()
        {
            Width = 640,
            Height = 480,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            Content = view
        };

    private static NewsArticle CreateImageArticle(
        string id,
        string imageUrl,
        string altText) =>
        new(
            id,
            DateOnly.FromDateTime(DateTime.Today),
            "Runtime Feed",
            "Offline image acceptance",
            "Image acceptance",
            string.Empty,
            "https://site.example/article",
            $"{id}-content",
            DateTimeOffset.UtcNow)
        {
            RichContent =
                $"<article><img src=\"{imageUrl}\" alt=\"{altText}\"></article>"
        };

    private static TextBlock FindImageStatus(
        DependencyObject root,
        string imageAutomationName)
    {
        Image image = FindDescendant<Image>(
            root,
            element => AutomationProperties.GetName(element) == imageAutomationName);
        var host = Assert.IsType<Grid>(VisualTreeHelper.GetParent(image));
        return Assert.Single(host.Children.OfType<TextBlock>());
    }

    private static double ReadProgress(ScrollViewer scrollViewer)
    {
        double maximumOffset = scrollViewer.ExtentHeight - scrollViewer.ViewportHeight;
        return maximumOffset <= 1 ? 0 : scrollViewer.VerticalOffset / maximumOffset * 100;
    }

    private static T FindDescendant<T>(
        DependencyObject root,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
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
        throw new InvalidOperationException($"Could not find {typeof(T).Name} in the visual tree.");
    }

    private static List<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var matches = new List<T>();
        CollectDescendants(root, matches);
        return matches;
    }

    private static void CollectDescendants<T>(
        DependencyObject root,
        ICollection<T> matches)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) matches.Add(match);
            CollectDescendants(child, matches);
        }
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
            PumpDispatcher();
            Thread.Sleep(10);
        }
        PumpDispatcher();
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static FeedEntry CreateLongEntry()
    {
        string paragraphs = string.Concat(
            Enumerable.Range(1, 180).Select(index =>
                $"<p>Runtime acceptance paragraph {index}. "
                + "This intentionally long local article verifies scrolling and restart restoration.</p>"));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(
            "runtime-entry",
            "runtime-feed",
            "runtime-external",
            "https://example.com/runtime-entry",
            "Runtime WPF acceptance article",
            "LenxTool Tests",
            now,
            now,
            "A long local article used for runtime acceptance.",
            paragraphs,
            ["Acceptance"],
            [],
            "runtime-content-hash",
            now);
    }

    private static byte[] CreateOnePixelPng()
    {
        BitmapSource bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 0x33, 0x66, 0x99, 0xFF },
            4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private sealed class NoNetworkResolver : IFeedHostResolver
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            throw new IOException("Offline WPF acceptance must not resolve DNS.");
        }
    }

    private sealed class NoNetworkTransport : IArticleImageTransport
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ArticleImageHttpResponse> SendAsync(
            Uri uri,
            IReadOnlyList<IPAddress> addresses,
            Uri? referrer,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            throw new IOException("Offline WPF acceptance must not use the network.");
        }
    }

    private sealed class EmptyAssetStore : IEntryAssetStore
    {
        public Task<EntryAsset?> GetAsync(
            string entryId,
            string sourceUrl,
            CancellationToken cancellationToken) =>
            Task.FromResult<EntryAsset?>(null);

        public Task<EntryAsset> PutAsync(
            string entryId,
            string sourceUrl,
            string mimeType,
            Stream content,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Offline miss must not be cached.");

        public Task<Stream?> OpenReadAsync(
            EntryAsset asset,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An empty cache cannot be opened.");

        public Task<int> PruneAsync(
            IReadOnlyCollection<string> protectedContentHashes,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class StubFeedFullTextQueueService : IFeedFullTextQueueService
    {
        public Task<FeedFullTextContent?> FetchOnOpenAsync(
            string entryId,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedFullTextContent?>(null);

        public Task<int> ProcessBackgroundBatchAsync(CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class StubFeedCatalogSyncService : IFeedCatalogSyncService
    {
        public FeedCatalogSyncStatus Current { get; } = new(
            false,
            7,
            FeedCatalogScope.Active,
            DateTimeOffset.UtcNow,
            false,
            0,
            null,
            null);

        public event EventHandler<FeedCatalogSyncStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeedCatalogSyncResult> SyncAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FeedCatalogSyncResult(
                FeedCatalogSyncOutcome.Unchanged,
                Current.Version,
                Current.LastSynchronizedAt));
    }
}
