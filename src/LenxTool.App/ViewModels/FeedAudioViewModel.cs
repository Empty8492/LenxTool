using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed class FeedAudioItem
{
    public FeedAudioItem(FeedContentItem content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        (AudioEnclosure, AudioAttachment) =
            FindPlayableAudio(content.Entry);
    }

    public FeedContentItem Content { get; }
    public FeedEntry Entry => Content.Entry;
    public string FeedName => Content.FeedName;
    public string CategoryName => Content.CategoryName;
    public string Title => Content.Title;
    public string Summary => Content.Summary;
    public DateTimeOffset DisplayTime => Content.DisplayTime;
    public bool IsStarred => Content.IsStarred;
    public string? SafeOriginalUrl => Content.SafeOriginalUrl;
    public FeedEnclosure? AudioEnclosure { get; }
    public FeedAttachmentClassification? AudioAttachment { get; }
    public bool CanPlay => AudioAttachment is not null;
    public string SourceHost =>
        Uri.TryCreate(
            AudioAttachment?.SafeUrl ?? SafeOriginalUrl,
            UriKind.Absolute,
            out Uri? source)
            ? source.IdnHost
            : "来源不可用";
    public string MediaDetails =>
        AudioAttachment is null
            ? "没有可验证的内置音频格式"
            : string.Join(
                " · ",
                new[]
                {
                    AudioAttachment.NormalizedMediaType
                        ?? "类型未知",
                    FormatLength(AudioAttachment.Length)
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static (
        FeedEnclosure? Enclosure,
        FeedAttachmentClassification? Attachment)
        FindPlayableAudio(FeedEntry entry)
    {
        foreach (FeedEnclosure enclosure in entry.Enclosures)
        {
            FeedAttachmentClassification attachment =
                FeedAttachmentClassifier.Classify(
                    enclosure,
                    entry.NormalizedUrl);
            if (attachment.UrlStatus == FeedAttachmentUrlStatus.Allowed
                && attachment.IsTypeVerified
                && attachment.Kind == FeedAttachmentKind.Audio)
            {
                return (enclosure, attachment);
            }
        }
        return (null, null);
    }

    private static string FormatLength(long? length)
    {
        if (length is null)
        {
            return "大小未知";
        }
        if (length < 1024)
        {
            return $"{length.Value.ToString(CultureInfo.InvariantCulture)} B";
        }
        double kibibytes = length.Value / 1024d;
        if (kibibytes < 1024d)
        {
            return $"{kibibytes.ToString("0.#", CultureInfo.InvariantCulture)} KiB";
        }
        double mebibytes = kibibytes / 1024d;
        return $"{mebibytes.ToString("0.#", CultureInfo.InvariantCulture)} MiB";
    }
}

public sealed class FeedAudioViewModel : ObservableObject, IDisposable
{
    private const string LocalProfile = "default";
    private static readonly TimeSpan ProgressWriteInterval =
        TimeSpan.FromSeconds(1);
    private readonly IEntryStateRepository _states;
    private readonly IFeedAudioPlaybackService _playback;
    private readonly IFeedMediaDeliveryService _delivery;
    private readonly IMediaJobInbox _mediaJobInbox;
    private readonly IAppNavigationService _navigation;
    private readonly Action<string> _openUri;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly Dictionary<string, double> _progressByEntryId =
        new(StringComparer.Ordinal);
    private readonly object _progressWriteLock = new();
    private FeedAudioItem? _selectedItem;
    private FeedAudioPlaybackSnapshot _playbackSnapshot =
        FeedAudioPlaybackSnapshot.Idle;
    private string _status = "正在读取本地音频缓存…";
    private string? _pendingExternalUrl;
    private string? _activePlaybackEntryId;
    private DateTimeOffset _lastProgressWriteAt =
        DateTimeOffset.UtcNow;
    private Task _progressPersistence = Task.CompletedTask;
    private double _seekPositionSeconds;
    private bool _updatingSeekPosition;
    private bool _disposed;

    public FeedAudioViewModel(
        FeedContentCollectionViewModel feed,
        IEntryStateRepository states,
        IFeedAudioPlaybackService playback,
        IFeedMediaDeliveryService delivery,
        IMediaJobInbox mediaJobInbox,
        IAppNavigationService navigation,
        Action<string> openUri)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(mediaJobInbox);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(openUri);
        if (feed.ViewKind != EntryViewKind.Audio)
        {
            throw new ArgumentException(
                "音频视图只能组合 Audio 内容集合。",
                nameof(feed));
        }

        Feed = feed;
        _states = states;
        _playback = playback;
        _delivery = delivery;
        _mediaJobInbox = mediaJobInbox;
        _navigation = navigation;
        _openUri = openUri;
        _synchronizationContext =
            SynchronizationContext.Current is
                System.Windows.Threading.DispatcherSynchronizationContext
                dispatcherContext
            && System.Windows.Application.Current is not null
                ? dispatcherContext
                : null;

        PlayPauseCommand = new(
            TogglePlayback,
            CanTogglePlayback);
        QueueTranscriptionCommand = new(
            QueueTranscriptionAsync,
            CanQueueTranscription);
        RequestExternalOpenCommand = new(
            RequestExternalOpen,
            CanRequestExternalOpen);
        ConfirmExternalOpenCommand = new(
            ConfirmExternalOpen,
            () => HasPendingExternalConfirmation);
        CancelExternalOpenCommand = new(CancelExternalOpen);
        Feed.Items.CollectionChanged += OnFeedItemsChanged;
        _playback.Changed += OnPlaybackChanged;
    }

    public FeedContentCollectionViewModel Feed { get; }
    public ObservableCollection<FeedAudioItem> Items { get; } = [];
    public RelayCommand PlayPauseCommand { get; }
    public AsyncRelayCommand QueueTranscriptionCommand { get; }
    public RelayCommand RequestExternalOpenCommand { get; }
    public RelayCommand ConfirmExternalOpenCommand { get; }
    public RelayCommand CancelExternalOpenCommand { get; }
    internal Task ProgressPersistence
    {
        get
        {
            lock (_progressWriteLock)
            {
                return _progressPersistence;
            }
        }
    }

    public FeedAudioItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value))
            {
                return;
            }

            FeedAudioItem? previous = _selectedItem;
            if (previous is not null
                && string.Equals(
                    _activePlaybackEntryId,
                    previous.Entry.Id,
                    StringComparison.Ordinal))
            {
                QueueProgressWrite(
                    previous.Entry.Id,
                    CurrentProgress);
                _playback.StopPlayback();
            }

            _activePlaybackEntryId = null;
            _pendingExternalUrl = null;
            _playbackSnapshot = FeedAudioPlaybackSnapshot.Idle;
            _seekPositionSeconds = 0;
            if (!SetProperty(ref _selectedItem, value))
            {
                return;
            }
            CurrentProgress = value is null
                ? 0
                : GetKnownProgress(value);
            OnPlaybackPropertiesChanged();
            OnPropertyChanged(nameof(HasPendingExternalConfirmation));
            NotifyCommandStates();
            Status = value is null
                ? "当前筛选下没有音频"
                : value.CanPlay
                    ? "已选择音频；点击播放后才会访问媒体源。"
                    : "此条目没有可验证的内置音频，可确认后打开原文。";
        }
    }

    public FeedAudioPlaybackStatus PlaybackStatus =>
        _playbackSnapshot.Status;
    public TimeSpan Position => _playbackSnapshot.Position;
    public TimeSpan? Duration => _playbackSnapshot.Duration;
    public bool IsPlaying =>
        PlaybackStatus == FeedAudioPlaybackStatus.Playing;
    public bool IsLoading =>
        PlaybackStatus == FeedAudioPlaybackStatus.Loading;
    public bool CanSeek =>
        Duration is TimeSpan duration && duration > TimeSpan.Zero;
    public string PlayPauseLabel =>
        PlaybackStatus is
            FeedAudioPlaybackStatus.Playing or
            FeedAudioPlaybackStatus.Loading
            ? "暂停"
            : PlaybackStatus == FeedAudioPlaybackStatus.Paused
                ? "继续"
                : "播放";
    public string PositionText => FormatDuration(Position);
    public string DurationText => Duration is TimeSpan duration
        ? FormatDuration(duration)
        : "--:--";
    public double DurationSeconds =>
        Math.Max(0, Duration?.TotalSeconds ?? 0);
    public double SeekPositionSeconds
    {
        get => _seekPositionSeconds;
        set
        {
            double bounded = Math.Clamp(
                double.IsFinite(value) ? value : 0,
                0,
                DurationSeconds);
            if (!SetProperty(ref _seekPositionSeconds, bounded)
                || _updatingSeekPosition
                || !CanSeek)
            {
                return;
            }
            _playback.Seek(TimeSpan.FromSeconds(bounded));
        }
    }

    public double CurrentProgress { get; private set; }
    public bool HasPendingExternalConfirmation =>
        _pendingExternalUrl is not null;
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Feed.InitializeAsync(cancellationToken);
        Status = Feed.Status;
    }

    public async Task RefreshCatalogAsync(
        bool preserveFilters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Feed.RefreshCatalogAsync(
            preserveFilters,
            cancellationToken);
        Status = Feed.Status;
    }

    internal void ReportLoadFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Feed.ReportLoadFailure(exception);
        Status = "音频流加载失败；时间线仍可使用，重新进入此页可重试。";
    }

    private void TogglePlayback()
    {
        FeedAudioItem? item = SelectedItem;
        string? sourceUrl = item?.AudioAttachment?.SafeUrl;
        if (item is null || sourceUrl is null)
        {
            return;
        }

        if (string.Equals(
                _playbackSnapshot.SourceUrl,
                sourceUrl,
                StringComparison.Ordinal)
            && _playbackSnapshot.Status is
                FeedAudioPlaybackStatus.Playing or
                FeedAudioPlaybackStatus.Loading)
        {
            _playback.Pause();
            return;
        }

        _activePlaybackEntryId = item.Entry.Id;
        _lastProgressWriteAt = DateTimeOffset.UtcNow;
        _pendingExternalUrl = null;
        OnPropertyChanged(nameof(HasPendingExternalConfirmation));
        ConfirmExternalOpenCommand.NotifyCanExecuteChanged();
        _playback.Play(new(
            sourceUrl,
            NormalizeResumeProgress(GetKnownProgress(item))));
    }

    private bool CanTogglePlayback() =>
        !_disposed && SelectedItem?.CanPlay == true;

    private async Task QueueTranscriptionAsync(
        CancellationToken cancellationToken)
    {
        FeedAudioItem? item = SelectedItem;
        if (item?.AudioEnclosure is null)
        {
            return;
        }

        Status = "正在安全下载音频并登记转写任务…";
        try
        {
            FeedMediaDeliveryRegistration registration =
                await _delivery.DeliverAsync(
                    item.Entry,
                    item.AudioEnclosure,
                    cancellationToken);
            _mediaJobInbox.PublishQueued(registration.Job);
            await _navigation.NavigateAsync(
                new(
                    "media",
                    "media_job",
                    registration.Job.Id),
                cancellationToken);
            Status = registration.Created
                ? "转写任务已创建，并已进入媒体工作台。"
                : "已有同源转写任务，已在媒体工作台中定位。";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Status = "已取消转写交接；不会留下未完成的临时下载。";
            throw;
        }
        catch
        {
            Status = "转写交接失败；音频条目和播放进度未受影响。";
        }
    }

    private bool CanQueueTranscription() =>
        !_disposed && SelectedItem?.CanPlay == true;

    private void RequestExternalOpen()
    {
        if (!CanRequestExternalOpen())
        {
            return;
        }
        _pendingExternalUrl = SelectedItem!.SafeOriginalUrl;
        OnPropertyChanged(nameof(HasPendingExternalConfirmation));
        ConfirmExternalOpenCommand.NotifyCanExecuteChanged();
        Status = "当前格式不支持内置播放。确认后将在默认浏览器打开原文；不会直接下载附件。";
    }

    private bool CanRequestExternalOpen() =>
        !_disposed
        && SelectedItem is { CanPlay: false, SafeOriginalUrl: not null };

    private void ConfirmExternalOpen()
    {
        string? safeOriginalUrl = SelectedItem?.SafeOriginalUrl;
        if (_pendingExternalUrl is null
            || !string.Equals(
                _pendingExternalUrl,
                safeOriginalUrl,
                StringComparison.Ordinal))
        {
            CancelExternalOpen();
            return;
        }

        string target = _pendingExternalUrl;
        CancelExternalOpen();
        _openUri(target);
        Status = "已在默认浏览器打开音频原文。";
    }

    private void CancelExternalOpen()
    {
        if (_pendingExternalUrl is null)
        {
            return;
        }
        _pendingExternalUrl = null;
        OnPropertyChanged(nameof(HasPendingExternalConfirmation));
        ConfirmExternalOpenCommand.NotifyCanExecuteChanged();
        Status = "已取消外部打开。";
    }

    private void OnFeedItemsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        string? selectedEntryId = SelectedItem?.Entry.Id;
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            Items.Clear();
        }
        else if (args.Action == NotifyCollectionChangedAction.Add
            && args.NewItems is not null)
        {
            foreach (FeedContentItem content in args.NewItems)
            {
                var item = new FeedAudioItem(content);
                Items.Add(item);
                _progressByEntryId.TryAdd(
                    item.Entry.Id,
                    NormalizeProgress(content.Timeline.Progress));
            }
        }
        else
        {
            Items.Clear();
            foreach (FeedContentItem content in Feed.Items)
            {
                Items.Add(new(content));
            }
        }

        SelectedItem = selectedEntryId is null
            ? Items.FirstOrDefault()
            : Items.FirstOrDefault(
                item => string.Equals(
                    item.Entry.Id,
                    selectedEntryId,
                    StringComparison.Ordinal))
                ?? Items.FirstOrDefault();
    }

    private void OnPlaybackChanged(
        object? sender,
        FeedAudioPlaybackChangedEventArgs args)
    {
        if (_synchronizationContext is not null
            && !ReferenceEquals(
                SynchronizationContext.Current,
                _synchronizationContext))
        {
            _synchronizationContext.Post(
                _ => ApplyPlaybackSnapshot(args.Snapshot),
                null);
            return;
        }
        ApplyPlaybackSnapshot(args.Snapshot);
    }

    private void ApplyPlaybackSnapshot(
        FeedAudioPlaybackSnapshot snapshot)
    {
        FeedAudioItem? item = SelectedItem;
        string? selectedSource = item?.AudioAttachment?.SafeUrl;
        if (item is null
            || selectedSource is null
            || !string.Equals(
                selectedSource,
                snapshot.SourceUrl,
                StringComparison.Ordinal))
        {
            return;
        }

        _playbackSnapshot = snapshot;
        _updatingSeekPosition = true;
        try
        {
            SeekPositionSeconds = snapshot.Position.TotalSeconds;
        }
        finally
        {
            _updatingSeekPosition = false;
        }

        if (snapshot.Duration is TimeSpan duration
            && duration > TimeSpan.Zero)
        {
            CurrentProgress = NormalizeProgress(
                snapshot.Position.TotalSeconds
                / duration.TotalSeconds
                * 100d);
            _progressByEntryId[item.Entry.Id] = CurrentProgress;
            OnPropertyChanged(nameof(CurrentProgress));
        }

        if (snapshot.Status == FeedAudioPlaybackStatus.Playing)
        {
            Status = $"正在播放 · {item.SourceHost}";
            if (DateTimeOffset.UtcNow - _lastProgressWriteAt
                >= ProgressWriteInterval)
            {
                _lastProgressWriteAt = DateTimeOffset.UtcNow;
                QueueProgressWrite(item.Entry.Id, CurrentProgress);
            }
        }
        else if (snapshot.Status == FeedAudioPlaybackStatus.Paused)
        {
            Status = "播放已暂停，当前位置已保存到本地。";
            QueueProgressWrite(item.Entry.Id, CurrentProgress);
        }
        else if (snapshot.Status == FeedAudioPlaybackStatus.Ended)
        {
            Status = "本集播放完成；再次播放将从头开始。";
            QueueProgressWrite(item.Entry.Id, 100d);
        }
        else if (snapshot.Status == FeedAudioPlaybackStatus.Failed)
        {
            Status = snapshot.Error
                ?? "音频流中断；当前位置已保存，可重试或打开原文。";
            QueueProgressWrite(item.Entry.Id, CurrentProgress);
        }
        else if (snapshot.Status == FeedAudioPlaybackStatus.Loading)
        {
            Status = $"正在连接音频来源 · {item.SourceHost}";
        }

        OnPlaybackPropertiesChanged();
        NotifyCommandStates();
    }

    private void QueueProgressWrite(
        string entryId,
        double progress)
    {
        double normalized = NormalizeProgress(progress);
        _progressByEntryId[entryId] = normalized;
        lock (_progressWriteLock)
        {
            _progressPersistence = PersistAfterAsync(
                _progressPersistence,
                entryId,
                normalized);
        }
    }

    private async Task PersistAfterAsync(
        Task previous,
        string entryId,
        double progress)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A later progress write must still be allowed to recover.
        }

        try
        {
            await _states.PatchAsync(
                entryId,
                LocalProfile,
                new(Progress: progress),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Playback remains usable when the local state store is unavailable.
        }
    }

    private double GetKnownProgress(FeedAudioItem item) =>
        _progressByEntryId.GetValueOrDefault(
            item.Entry.Id,
            NormalizeProgress(item.Content.Timeline.Progress));

    private void OnPlaybackPropertiesChanged()
    {
        OnPropertyChanged(nameof(PlaybackStatus));
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(PlayPauseLabel));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(SeekPositionSeconds));
    }

    private void NotifyCommandStates()
    {
        PlayPauseCommand.NotifyCanExecuteChanged();
        QueueTranscriptionCommand.NotifyCanExecuteChanged();
        RequestExternalOpenCommand.NotifyCanExecuteChanged();
        ConfirmExternalOpenCommand.NotifyCanExecuteChanged();
    }

    private static double NormalizeResumeProgress(double progress) =>
        progress is >= 98d and <= 100d
            ? 0d
            : NormalizeProgress(progress);

    private static double NormalizeProgress(double progress) =>
        double.IsFinite(progress)
            ? Math.Clamp(progress, 0d, 100d)
            : 0d;

    private static string FormatDuration(TimeSpan value)
    {
        TimeSpan safe = value < TimeSpan.Zero
            ? TimeSpan.Zero
            : value;
        return safe.TotalHours >= 1
            ? safe.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : safe.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (SelectedItem is FeedAudioItem selected
            && string.Equals(
                _activePlaybackEntryId,
                selected.Entry.Id,
                StringComparison.Ordinal))
        {
            QueueProgressWrite(
                selected.Entry.Id,
                CurrentProgress);
        }
        _disposed = true;
        Feed.Items.CollectionChanged -= OnFeedItemsChanged;
        _playback.Changed -= OnPlaybackChanged;
        _playback.StopPlayback();
        _playback.Dispose();
        QueueTranscriptionCommand.Dispose();
        Feed.Dispose();
    }
}
