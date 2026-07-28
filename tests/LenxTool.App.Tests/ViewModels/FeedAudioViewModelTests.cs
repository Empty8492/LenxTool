using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class FeedAudioViewModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SelectionDoesNotOpenMediaAndPlayResumesPersistedProgress()
    {
        FeedEntry entry = AudioEntry("episode-1");
        var states = new StubEntryStateRepository(
            new EntryState(
                entry.Id,
                "default",
                false,
                false,
                false,
                42d,
                "",
                Now));
        var playback = new StubFeedAudioPlaybackService();
        using FeedAudioViewModel viewModel = CreateViewModel(
            [entry],
            states,
            playback);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Empty(playback.PlayRequests);
        Assert.Equal(entry.Id, viewModel.SelectedItem?.Entry.Id);

        viewModel.PlayPauseCommand.Execute(null);

        FeedAudioPlaybackRequest request = Assert.Single(
            playback.PlayRequests);
        Assert.Equal("https://cdn.example/episode-1.mp3", request.SourceUrl);
        Assert.Equal("audio/mpeg", request.MediaType);
        Assert.Equal(42d, request.ResumeProgress);

        playback.Publish(new(
            request.SourceUrl,
            FeedAudioPlaybackStatus.Playing,
            TimeSpan.FromSeconds(50),
            TimeSpan.FromSeconds(100)));
        viewModel.PlayPauseCommand.Execute(null);
        await viewModel.ProgressPersistence;

        Assert.Equal(1, playback.PauseCount);
        EntryStatePatch persisted = Assert.Single(states.Patches).Patch;
        Assert.Equal(50d, persisted.Progress);
    }

    [Fact]
    public async Task SwitchingEntryStopsPlaybackAndIgnoresLateSourceEvents()
    {
        FeedEntry first = AudioEntry("episode-1");
        FeedEntry second = AudioEntry("episode-2");
        var playback = new StubFeedAudioPlaybackService();
        using FeedAudioViewModel viewModel = CreateViewModel(
            [first, second],
            new StubEntryStateRepository(),
            playback);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.PlayPauseCommand.Execute(null);

        string oldSource = Assert.Single(playback.PlayRequests).SourceUrl;
        viewModel.SelectedItem = viewModel.Items[1];
        playback.Publish(new(
            oldSource,
            FeedAudioPlaybackStatus.Playing,
            TimeSpan.FromSeconds(75),
            TimeSpan.FromSeconds(100)));

        Assert.Equal(1, playback.StopCount);
        Assert.Equal(second.Id, viewModel.SelectedItem.Entry.Id);
        Assert.Equal(TimeSpan.Zero, viewModel.Position);
        Assert.Equal(FeedAudioPlaybackStatus.Idle, viewModel.PlaybackStatus);
    }

    [Fact]
    public async Task UnsupportedAudioRequiresTwoStepExternalConfirmation()
    {
        FeedEntry entry = AudioEntry("unsupported") with
        {
            Enclosures =
            [
                new(
                    "https://cdn.example/unsupported.bin",
                    "application/octet-stream",
                    256,
                    "Unsupported")
            ]
        };
        var opened = new List<string>();
        using FeedAudioViewModel viewModel = CreateViewModel(
            [entry],
            new StubEntryStateRepository(),
            new StubFeedAudioPlaybackService(),
            opened: opened);
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.PlayPauseCommand.CanExecute(null));
        Assert.True(viewModel.RequestExternalOpenCommand.CanExecute(null));

        viewModel.RequestExternalOpenCommand.Execute(null);

        Assert.True(viewModel.HasPendingExternalConfirmation);
        Assert.Empty(opened);

        viewModel.ConfirmExternalOpenCommand.Execute(null);

        Assert.False(viewModel.HasPendingExternalConfirmation);
        Assert.Equal(
            "https://example.com/unsupported",
            Assert.Single(opened));
    }

    [Fact]
    public async Task ReservedOriginalCannotBecomeAudioFallback()
    {
        FeedEntry entry = AudioEntry("unsafe") with
        {
            NormalizedUrl = "http://127.0.0.1/private",
            Enclosures =
            [
                new(
                    "https://cdn.example/unsupported.bin",
                    "application/octet-stream",
                    256,
                    "Unsupported")
            ]
        };
        using FeedAudioViewModel viewModel = CreateViewModel(
            [entry],
            new StubEntryStateRepository(),
            new StubFeedAudioPlaybackService());

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Null(viewModel.SelectedItem?.SafeOriginalUrl);
        Assert.False(
            viewModel.RequestExternalOpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task TranscriptionPublishesExistingIdempotentJobAndNavigates()
    {
        FeedEntry entry = AudioEntry("episode-1");
        MediaJob job = new(
            "feed-job",
            "FeedTranscription",
            @"L:\media\episode-1.mp3",
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
        var delivery = new StubFeedMediaDeliveryService(
            new(
                new(
                    entry.Id,
                    entry.FeedId,
                    entry.Title,
                    "https://cdn.example/episode-1.mp3",
                    "episode-1",
                    "audio/mpeg",
                    128,
                    job.Id,
                    Now),
                job,
                Created: false));
        var inbox = new MediaJobInbox();
        var published = new List<MediaJob>();
        inbox.JobQueued += published.Add;
        var navigation = new StubNavigationService();
        using FeedAudioViewModel viewModel = CreateViewModel(
            [entry],
            new StubEntryStateRepository(),
            new StubFeedAudioPlaybackService(),
            delivery,
            inbox,
            navigation);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.QueueTranscriptionCommand.ExecuteAsync();

        Assert.Same(entry, delivery.Entry);
        Assert.Equal(
            "https://cdn.example/episode-1.mp3",
            delivery.Enclosure?.Url);
        Assert.Same(job, Assert.Single(published));
        AppNavigationRequest request = Assert.IsType<AppNavigationRequest>(
            navigation.Request);
        Assert.Equal("media", request.RouteId);
        Assert.Equal("media_job", request.EntityType);
        Assert.Equal(job.Id, request.EntityId);
        Assert.Contains("已有", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterruptedStreamPersistsPositionAndEnablesSafeFallback()
    {
        FeedEntry entry = AudioEntry("episode-1");
        var states = new StubEntryStateRepository();
        var playback = new StubFeedAudioPlaybackService();
        var opened = new List<string>();
        using FeedAudioViewModel viewModel = CreateViewModel(
            [entry],
            states,
            playback,
            opened: opened);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.PlayPauseCommand.Execute(null);
        string source = Assert.Single(playback.PlayRequests).SourceUrl;
        playback.Publish(new(
            source,
            FeedAudioPlaybackStatus.Playing,
            TimeSpan.FromSeconds(25),
            TimeSpan.FromSeconds(100)));

        playback.Publish(new(
            source,
            FeedAudioPlaybackStatus.Failed,
            TimeSpan.FromSeconds(25),
            TimeSpan.FromSeconds(100),
            "音频流已中断。"));
        await viewModel.ProgressPersistence;

        Assert.Equal(
            25d,
            Assert.Single(states.Patches).Patch.Progress);
        Assert.True(viewModel.RequestExternalOpenCommand.CanExecute(null));
        viewModel.RequestExternalOpenCommand.Execute(null);
        Assert.Empty(opened);
        viewModel.ConfirmExternalOpenCommand.Execute(null);
        Assert.Equal(entry.NormalizedUrl, Assert.Single(opened));
    }

    [Fact]
    public async Task CancelledTranscriptionDoesNotPublishOrNavigate()
    {
        FeedEntry entry = AudioEntry("episode-1");
        var delivery = new StubFeedMediaDeliveryService(
            waitForCancellation: true);
        var inbox = new MediaJobInbox();
        var published = new List<MediaJob>();
        inbox.JobQueued += published.Add;
        var navigation = new StubNavigationService();
        using FeedAudioViewModel viewModel = CreateViewModel(
            [entry],
            new StubEntryStateRepository(),
            new StubFeedAudioPlaybackService(),
            delivery,
            inbox,
            navigation);
        await viewModel.InitializeAsync(CancellationToken.None);

        Task execution =
            viewModel.QueueTranscriptionCommand.ExecuteAsync();
        await delivery.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        viewModel.QueueTranscriptionCommand.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution);
        Assert.Empty(published);
        Assert.Null(navigation.Request);
        Assert.Contains(
            "已取消",
            viewModel.Status,
            StringComparison.Ordinal);
    }

    private static FeedAudioViewModel CreateViewModel(
        IReadOnlyList<FeedEntry> entries,
        StubEntryStateRepository states,
        StubFeedAudioPlaybackService playback,
        StubFeedMediaDeliveryService? delivery = null,
        MediaJobInbox? inbox = null,
        StubNavigationService? navigation = null,
        List<string>? opened = null)
    {
        var entryRepository = new StubEntryRepository(entries);
        var collection = new FeedContentCollectionViewModel(
            EntryViewKind.Audio,
            "音频",
            entryRepository,
            new StubCatalogRepository(),
            states,
            new StubFavoriteRepository(),
            _ => { });
        return new(
            collection,
            states,
            playback,
            delivery ?? new StubFeedMediaDeliveryService(),
            inbox ?? new MediaJobInbox(),
            navigation ?? new StubNavigationService(),
            (opened ?? []).Add);
    }

    private static FeedEntry AudioEntry(string id) =>
        new(
            id,
            "30000000-0000-4000-8000-000000000001",
            $"Podcast {id}",
            $"https://example.com/{id}",
            id,
            "Author",
            Now,
            Now,
            "Summary",
            "Content",
            [],
            [
                new(
                    $"https://cdn.example/{id}.mp3",
                    "audio/mpeg",
                    128,
                    id)
            ],
            new string('a', 64),
            Now);

    private sealed class StubFeedAudioPlaybackService :
        IFeedAudioPlaybackService
    {
        public event EventHandler<FeedAudioPlaybackChangedEventArgs>? Changed;

        public List<FeedAudioPlaybackRequest> PlayRequests { get; } = [];
        public int PauseCount { get; private set; }
        public int StopCount { get; private set; }
        public FeedAudioPlaybackSnapshot Snapshot { get; private set; } =
            FeedAudioPlaybackSnapshot.Idle;

        public void Play(FeedAudioPlaybackRequest request)
        {
            PlayRequests.Add(request);
            Publish(new(
                request.SourceUrl,
                FeedAudioPlaybackStatus.Loading,
                TimeSpan.Zero,
                null));
        }

        public void Pause()
        {
            PauseCount++;
            Publish(Snapshot with
            {
                Status = FeedAudioPlaybackStatus.Paused
            });
        }

        public void Seek(TimeSpan position)
        {
            Publish(Snapshot with { Position = position });
        }

        public void StopPlayback()
        {
            StopCount++;
            Snapshot = FeedAudioPlaybackSnapshot.Idle;
        }

        public void Publish(FeedAudioPlaybackSnapshot snapshot)
        {
            Snapshot = snapshot;
            Changed?.Invoke(
                this,
                new FeedAudioPlaybackChangedEventArgs(snapshot));
        }

        public void Dispose()
        {
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
            CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class StubEntryStateRepository : IEntryStateRepository
    {
        private readonly Dictionary<string, EntryState> _states;

        public StubEntryStateRepository(params EntryState[] states)
        {
            _states = states.ToDictionary(
                state => state.EntryId,
                StringComparer.Ordinal);
        }

        public List<(string EntryId, EntryStatePatch Patch)> Patches { get; } =
            [];

        public Task<IReadOnlyDictionary<string, EntryState>> GetAsync(
            IReadOnlyCollection<string> entryIds,
            string localProfile,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, EntryState>>(
                _states
                    .Where(pair => entryIds.Contains(pair.Key))
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.Ordinal));

        public Task<EntryState> PatchAsync(
            string entryId,
            string localProfile,
            EntryStatePatch patch,
            CancellationToken cancellationToken)
        {
            Patches.Add((entryId, patch));
            EntryState current = _states.GetValueOrDefault(entryId)
                ?? new(
                    entryId,
                    localProfile,
                    false,
                    false,
                    false,
                    0,
                    "",
                    Now);
            EntryState updated = current with
            {
                Progress = patch.Progress ?? current.Progress,
                UpdatedAt = Now
            };
            _states[entryId] = updated;
            return Task.FromResult(updated);
        }
    }

    private sealed class StubCatalogRepository : IFeedCatalogRepository
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
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;
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
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeleteTagAsync(
            string tagId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class StubFeedMediaDeliveryService(
        FeedMediaDeliveryRegistration? registration = null,
        bool waitForCancellation = false)
        : IFeedMediaDeliveryService
    {
        public FeedEntry? Entry { get; private set; }
        public FeedEnclosure? Enclosure { get; private set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FeedMediaDeliveryRegistration> DeliverAsync(
            FeedEntry entry,
            FeedEnclosure enclosure,
            CancellationToken cancellationToken)
        {
            Entry = entry;
            Enclosure = enclosure;
            Started.TrySetResult();
            if (waitForCancellation)
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }
            return registration
                ?? throw new InvalidOperationException(
                    "No delivery registration configured.");
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
}
