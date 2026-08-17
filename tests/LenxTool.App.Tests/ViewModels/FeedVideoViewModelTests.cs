using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class FeedVideoViewModelTests
{
    private const long Mebibyte = 1024L * 1024;
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SelectionUsesVerifiedVideoAndPosterWithoutSideEffects()
    {
        FeedEntry entry = VideoEntry("video-1", 5 * Mebibyte);
        var planner = new StubVideoPlanner();
        var delivery = new StubDeliveryService();
        var opened = new List<string>();
        using FeedVideoViewModel viewModel = CreateViewModel(
            [entry],
            planner,
            delivery,
            opened: opened);

        await viewModel.InitializeAsync(CancellationToken.None);

        FeedVideoItem item = Assert.IsType<FeedVideoItem>(
            viewModel.SelectedItem);
        Assert.Equal(
            "https://cdn.example/video-1.mp4",
            item.VideoAttachment?.SafeUrl);
        Assert.Equal(
            "https://cdn.example/video-1-poster.jpg",
            item.PosterUrl);
        Assert.Equal("video/mp4 · 5 MiB", item.MediaDetails);
        Assert.Empty(planner.Calls);
        Assert.Equal(0, delivery.CallCount);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task BrowserFallbackRequiresConfirmationAndNeverOpensEnclosure()
    {
        FeedEntry entry = VideoEntry("video-1", 5 * Mebibyte);
        var opened = new List<string>();
        using FeedVideoViewModel viewModel = CreateViewModel(
            [entry],
            new StubVideoPlanner(),
            new StubDeliveryService(),
            opened: opened);
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.RequestExternalOpenCommand.Execute(null);

        Assert.True(viewModel.HasPendingExternalConfirmation);
        Assert.Empty(opened);
        viewModel.ConfirmExternalOpenCommand.Execute(null);

        Assert.Equal(entry.NormalizedUrl, Assert.Single(opened));
        Assert.DoesNotContain(".mp4", opened[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedPosterAndReservedOriginalProduceNoExternalAction()
    {
        FeedEntry entry = VideoEntry("video-1", 5 * Mebibyte)
            with
        {
            NormalizedUrl = "http://127.0.0.1/private",
            Enclosures =
                [
                    new(
                        "https://cdn.example/video-1.mp4",
                        "video/mp4",
                        5 * Mebibyte,
                        "Video"),
                    new(
                        "https://cdn.example/video-1-poster.jpg",
                        "text/html",
                        128,
                        "Fake poster")
                ]
        };
        using FeedVideoViewModel viewModel = CreateViewModel(
            [entry],
            new StubVideoPlanner(),
            new StubDeliveryService());

        await viewModel.InitializeAsync(CancellationToken.None);

        FeedVideoItem item = Assert.IsType<FeedVideoItem>(
            viewModel.SelectedItem);
        Assert.Null(item.PosterUrl);
        Assert.Null(item.SafeOriginalUrl);
        Assert.False(
            viewModel.RequestExternalOpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task LargeVideoRechecksPlanBeforeConfirmedDelivery()
    {
        FeedEntry entry = VideoEntry("video-1", 25 * Mebibyte);
        FeedVideoDeliveryPlan plan = Plan(
            entry,
            25 * Mebibyte,
            FeedVideoDeliveryPlanStatus.Ready,
            requiresConfirmation: true);
        var planner = new StubVideoPlanner(plan, plan);
        MediaJob job = Job("video-job");
        var delivery = new StubDeliveryService(
            Registration(entry, job, Created: true));
        var inbox = new MediaJobInbox();
        var published = new List<MediaJob>();
        inbox.JobQueued += published.Add;
        var navigation = new StubNavigationService();
        using FeedVideoViewModel viewModel = CreateViewModel(
            [entry],
            planner,
            delivery,
            inbox,
            navigation);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.PrepareDeliveryCommand.ExecuteAsync();

        Assert.True(viewModel.HasPendingDeliveryConfirmation);
        Assert.Equal(0, delivery.CallCount);
        Assert.Single(planner.Calls);

        await viewModel.ConfirmDeliveryCommand.ExecuteAsync();

        Assert.Collection(
            planner.Calls,
            _ => { },
            _ => { });
        Assert.Equal(1, delivery.CallCount);
        Assert.Same(job, Assert.Single(published));
        Assert.Equal("media", navigation.Request?.RouteId);
        Assert.False(viewModel.HasPendingDeliveryConfirmation);
    }

    [Fact]
    public async Task SmallOrExistingVideoDeliversWithoutSecondConfirmation()
    {
        FeedEntry entry = VideoEntry("video-1", 5 * Mebibyte);
        FeedVideoDeliveryPlan plan = Plan(
            entry,
            5 * Mebibyte,
            FeedVideoDeliveryPlanStatus.Ready,
            requiresConfirmation: false);
        var planner = new StubVideoPlanner(plan);
        var delivery = new StubDeliveryService(
            Registration(entry, Job("video-job"), Created: false));
        using FeedVideoViewModel viewModel = CreateViewModel(
            [entry],
            planner,
            delivery);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.PrepareDeliveryCommand.ExecuteAsync();

        Assert.False(viewModel.HasPendingDeliveryConfirmation);
        Assert.Equal(1, delivery.CallCount);
        Assert.Contains("已有", viewModel.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FeedVideoDeliveryPlanStatus.ExceedsLimit, "超过")]
    [InlineData(FeedVideoDeliveryPlanStatus.InsufficientSpace, "空间不足")]
    public async Task BlockedPlanNeverStartsDownload(
        FeedVideoDeliveryPlanStatus status,
        string expectedStatus)
    {
        FeedEntry entry = VideoEntry("video-1", 600 * Mebibyte);
        var planner = new StubVideoPlanner(
            Plan(
                entry,
                600 * Mebibyte,
                status,
                requiresConfirmation: false));
        var delivery = new StubDeliveryService();
        using FeedVideoViewModel viewModel = CreateViewModel(
            [entry],
            planner,
            delivery);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.PrepareDeliveryCommand.ExecuteAsync();

        Assert.Equal(0, delivery.CallCount);
        Assert.Contains(
            expectedStatus,
            viewModel.Status,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledVideoDownloadPublishesNothing()
    {
        FeedEntry entry = VideoEntry("video-1", 5 * Mebibyte);
        var planner = new StubVideoPlanner(
            Plan(
                entry,
                5 * Mebibyte,
                FeedVideoDeliveryPlanStatus.Ready,
                requiresConfirmation: false));
        var delivery = new StubDeliveryService(waitForCancellation: true);
        var inbox = new MediaJobInbox();
        var published = new List<MediaJob>();
        inbox.JobQueued += published.Add;
        var navigation = new StubNavigationService();
        using FeedVideoViewModel viewModel = CreateViewModel(
            [entry],
            planner,
            delivery,
            inbox,
            navigation);
        await viewModel.InitializeAsync(CancellationToken.None);

        Task execution = viewModel.PrepareDeliveryCommand.ExecuteAsync();
        await delivery.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        viewModel.CancelDeliveryCommand.Execute(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution);

        Assert.Empty(published);
        Assert.Null(navigation.Request);
        Assert.Contains("已取消", viewModel.Status, StringComparison.Ordinal);
    }

    private static FeedVideoViewModel CreateViewModel(
        IReadOnlyList<FeedEntry> entries,
        StubVideoPlanner planner,
        StubDeliveryService delivery,
        MediaJobInbox? inbox = null,
        StubNavigationService? navigation = null,
        List<string>? opened = null)
    {
        var collection = new FeedContentCollectionViewModel(
            EntryViewKind.Video,
            "视频",
            new StubEntryRepository(entries),
            new StubCatalogRepository(),
            new StubEntryStateRepository(),
            new StubFavoriteRepository(),
            _ => { });
        return new(
            collection,
            planner,
            delivery,
            inbox ?? new MediaJobInbox(),
            navigation ?? new StubNavigationService(),
            (opened ?? []).Add);
    }

    private static FeedEntry VideoEntry(
        string id,
        long? length) =>
        new(
            id,
            "30000000-0000-4000-8000-000000000001",
            id,
            $"https://example.com/{id}",
            $"Video {id}",
            "Author",
            Now,
            Now,
            "Summary",
            "Content",
            [],
            [
                new(
                    $"https://cdn.example/{id}.mp4",
                    "video/mp4",
                    length,
                    id),
                new(
                    $"https://cdn.example/{id}-poster.jpg",
                    "image/jpeg",
                    128,
                    "Poster")
            ],
            new string('b', 64),
            Now);

    private static FeedVideoDeliveryPlan Plan(
        FeedEntry entry,
        long? declaredBytes,
        FeedVideoDeliveryPlanStatus status,
        bool requiresConfirmation) =>
        new(
            entry.Id,
            entry.Enclosures[0].Url,
            @"C:\Users\Test\AppData\Local\LenxTool\Data\FeedMedia",
            declaredBytes,
            declaredBytes ?? 512 * Mebibyte,
            512 * Mebibyte,
            2_000 * Mebibyte,
            status,
            requiresConfirmation,
            status == FeedVideoDeliveryPlanStatus.AlreadyAvailable);

    private static MediaJob Job(string id) =>
        new(
            id,
            "FeedTranscription",
            $@"C:\media\{id}.mp4",
            null,
            MediaJobStatus.Queued,
            0,
            TranscriptionEngine.Groq,
            "whisper-large-v3",
            0,
            0,
            null,
            Now,
            Now);

    private static FeedMediaDeliveryRegistration Registration(
        FeedEntry entry,
        MediaJob job,
        bool Created) =>
        new(
            new(
                entry.Id,
                entry.FeedId,
                entry.Title,
                entry.Enclosures[0].Url,
                entry.Enclosures[0].Title,
                entry.Enclosures[0].MediaType!,
                entry.Enclosures[0].Length,
                job.Id,
                Now),
            job,
            Created);

    private sealed class StubVideoPlanner(
        params FeedVideoDeliveryPlan[] plans)
        : IFeedVideoDeliveryPlanningService
    {
        private readonly Queue<FeedVideoDeliveryPlan> _plans =
            new(plans);
        public List<(FeedEntry Entry, FeedEnclosure Enclosure)> Calls
        {
            get;
        } = [];

        public Task<FeedVideoDeliveryPlan> PlanAsync(
            FeedEntry entry,
            FeedEnclosure enclosure,
            CancellationToken cancellationToken)
        {
            Calls.Add((entry, enclosure));
            return Task.FromResult(_plans.Dequeue());
        }
    }

    private sealed class StubDeliveryService(
        FeedMediaDeliveryRegistration? registration = null,
        bool waitForCancellation = false)
        : IFeedMediaDeliveryService
    {
        public int CallCount { get; private set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FeedMediaDeliveryRegistration> DeliverAsync(
            FeedEntry entry,
            FeedEnclosure enclosure,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult();
            if (waitForCancellation)
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }
            return registration
                ?? throw new InvalidOperationException(
                    "No registration configured.");
        }
    }

    private sealed class StubNavigationService : IAppNavigationService
    {
        public AppNavigationRequest? Request { get; private set; }

        public Task NavigateAsync(
            AppNavigationRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.CompletedTask;
        }
    }

    private sealed class StubEntryRepository(
        IReadOnlyList<FeedEntry> entries) : IFeedEntryRepository
    {
        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FeedEntryPage(
                entries,
                query.Offset,
                HasMore: false,
                NextOffset: entries.Count));

        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken) =>
            Task.FromResult(entries.FirstOrDefault(
                entry => entry.Id == entryId));

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> newEntries,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class StubEntryStateRepository :
        IEntryStateRepository
    {
        public Task<IReadOnlyDictionary<string, EntryState>> GetAsync(
            IReadOnlyCollection<string> entryIds,
            string localProfile,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, EntryState>>(
                new Dictionary<string, EntryState>());

        public Task<EntryState> PatchAsync(
            string entryId,
            string localProfile,
            EntryStatePatch patch,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCatalogRepository :
        IFeedCatalogRepository
    {
        private readonly FeedCatalogSnapshot _catalog = new(
            new(1, FeedCatalogScope.Active, Now, Now),
            [
                new(
                    "10000000-0000-4000-8000-000000000001",
                    "Technology",
                    "technology",
                    1,
                    true,
                    1,
                    Now,
                    Now)
            ],
            [
                new(
                    "30000000-0000-4000-8000-000000000001",
                    "https://feeds.example/feed.xml",
                    "https://feeds.example/feed.xml",
                    "Daily Feed",
                    "https://feeds.example/",
                    "10000000-0000-4000-8000-000000000001",
                    FeedViewKind.Article,
                    60,
                    1,
                    true,
                    1,
                    Now,
                    Now)
            ]);

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedCatalogSnapshot?>(_catalog);

        public Task<FeedCatalogState> GetStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(_catalog.State);

        public Task ReplaceAsync(
            FeedCatalogSnapshot snapshot,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubFavoriteRepository : IFavoriteRepository
    {
        public Task<IReadOnlyDictionary<string, FavoriteItem>>
            GetForEntitiesAsync(
                string entityType,
                IReadOnlyCollection<string> entityIds,
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, FavoriteItem>>(
                new Dictionary<string, FavoriteItem>());

        public Task<int> GetCountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(0);
        public Task<FavoriteItem?> GetAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult<FavoriteItem?>(null);
        public Task<FavoriteItem> UpsertAsync(
            string entityType,
            string entityId,
            string note,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> RemoveAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TagItem> UpsertTagAsync(
            string name,
            string color,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TagItem> AddTagAsync(
            string entityType,
            string entityId,
            string name,
            string color,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TagItem>> GetTagsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagItem>>([]);
        public Task<IReadOnlyList<TagItem>> GetTagsForEntityAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagItem>>([]);
        public Task SetTagsAsync(
            string entityType,
            string entityId,
            IReadOnlyCollection<string> tagIds,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<bool> DeleteTagAsync(
            string tagId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
