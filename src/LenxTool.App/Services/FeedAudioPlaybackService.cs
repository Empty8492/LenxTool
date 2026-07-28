using System.Windows.Media;
using System.Windows.Threading;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public enum FeedAudioPlaybackStatus
{
    Idle,
    Loading,
    Playing,
    Paused,
    Ended,
    Failed
}

public sealed record FeedAudioPlaybackRequest(
    string SourceUrl,
    string MediaType,
    double ResumeProgress);

public sealed record FeedAudioPlaybackSnapshot(
    string? SourceUrl,
    FeedAudioPlaybackStatus Status,
    TimeSpan Position,
    TimeSpan? Duration,
    string? Error = null)
{
    public static FeedAudioPlaybackSnapshot Idle { get; } =
        new(
            null,
            FeedAudioPlaybackStatus.Idle,
            TimeSpan.Zero,
            null);
}

public sealed class FeedAudioPlaybackChangedEventArgs(
    FeedAudioPlaybackSnapshot snapshot) : EventArgs
{
    public FeedAudioPlaybackSnapshot Snapshot { get; } =
        snapshot ?? throw new ArgumentNullException(nameof(snapshot));
}

public interface IFeedAudioPlaybackService : IDisposable
{
    event EventHandler<FeedAudioPlaybackChangedEventArgs>? Changed;

    FeedAudioPlaybackSnapshot Snapshot { get; }

    void Play(FeedAudioPlaybackRequest request);

    void Pause();

    void Seek(TimeSpan position);

    void StopPlayback();
}

public sealed class WpfFeedAudioPlaybackService :
    IFeedAudioPlaybackService
{
    private static readonly TimeSpan ProgressInterval =
        TimeSpan.FromMilliseconds(500);
    private readonly DispatcherTimer _progressTimer;
    private MediaPlayer? _player;
    private FeedAudioPlaybackSnapshot _snapshot =
        FeedAudioPlaybackSnapshot.Idle;
    private double _requestedResumeProgress;
    private bool _disposed;

    public WpfFeedAudioPlaybackService()
    {
        _progressTimer = new(DispatcherPriority.Background)
        {
            Interval = ProgressInterval
        };
        _progressTimer.Tick += OnProgressTimerTick;
    }

    public event EventHandler<FeedAudioPlaybackChangedEventArgs>? Changed;

    public FeedAudioPlaybackSnapshot Snapshot => _snapshot;

    public void Play(FeedAudioPlaybackRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        Uri source = ValidateSource(request);
        double resumeProgress = NormalizeProgress(request.ResumeProgress);

        if (_player is not null
            && string.Equals(
                _snapshot.SourceUrl,
                source.AbsoluteUri,
                StringComparison.Ordinal)
            && _snapshot.Status == FeedAudioPlaybackStatus.Paused)
        {
            _player.Play();
            _progressTimer.Start();
            Publish(_snapshot with
            {
                Status = FeedAudioPlaybackStatus.Playing,
                Error = null
            });
            return;
        }

        ClosePlayer();
        var player = new MediaPlayer();
        player.MediaOpened += OnMediaOpened;
        player.MediaEnded += OnMediaEnded;
        player.MediaFailed += OnMediaFailed;
        _player = player;
        _requestedResumeProgress = resumeProgress;
        Publish(new(
            source.AbsoluteUri,
            FeedAudioPlaybackStatus.Loading,
            TimeSpan.Zero,
            null));
        try
        {
            player.Open(source);
        }
        catch
        {
            ClosePlayer();
            Publish(new(
                source.AbsoluteUri,
                FeedAudioPlaybackStatus.Failed,
                TimeSpan.Zero,
                null,
                "系统媒体组件无法打开此音频来源。"));
        }
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_player is null)
        {
            return;
        }
        if (_snapshot.Status == FeedAudioPlaybackStatus.Loading)
        {
            string? sourceUrl = _snapshot.SourceUrl;
            ClosePlayer();
            Publish(new(
                sourceUrl,
                FeedAudioPlaybackStatus.Paused,
                TimeSpan.Zero,
                null));
            return;
        }
        if (_snapshot.Status != FeedAudioPlaybackStatus.Playing)
        {
            return;
        }

        _player.Pause();
        _progressTimer.Stop();
        Publish(ReadSnapshot(FeedAudioPlaybackStatus.Paused));
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_player is null
            || _snapshot.Duration is not TimeSpan duration
            || duration <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan bounded = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > duration
                ? duration
                : position;
        _player.Position = bounded;
        Publish(ReadSnapshot(_snapshot.Status));
    }

    public void StopPlayback()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClosePlayer();
        Publish(FeedAudioPlaybackSnapshot.Idle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ClosePlayer();
        _progressTimer.Tick -= OnProgressTimerTick;
    }

    private void OnMediaOpened(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(sender, _player) || _player is null)
        {
            return;
        }

        TimeSpan? duration = _player.NaturalDuration.HasTimeSpan
            ? _player.NaturalDuration.TimeSpan
            : null;
        if (duration is TimeSpan knownDuration
            && knownDuration > TimeSpan.Zero
            && _requestedResumeProgress is > 0 and < 98)
        {
            _player.Position = TimeSpan.FromTicks(
                (long)(knownDuration.Ticks
                    * (_requestedResumeProgress / 100d)));
        }

        _player.Play();
        _progressTimer.Start();
        Publish(ReadSnapshot(
            FeedAudioPlaybackStatus.Playing,
            duration));
    }

    private void OnMediaEnded(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(sender, _player))
        {
            return;
        }
        _progressTimer.Stop();
        FeedAudioPlaybackSnapshot ended =
            ReadSnapshot(FeedAudioPlaybackStatus.Ended);
        if (ended.Duration is TimeSpan duration)
        {
            ended = ended with { Position = duration };
        }
        Publish(ended);
    }

    private void OnMediaFailed(
        object? sender,
        ExceptionEventArgs args)
    {
        if (!ReferenceEquals(sender, _player))
        {
            return;
        }
        string? sourceUrl = _snapshot.SourceUrl;
        TimeSpan position = _snapshot.Position;
        TimeSpan? duration = _snapshot.Duration;
        ClosePlayer();
        Publish(new(
            sourceUrl,
            FeedAudioPlaybackStatus.Failed,
            position,
            duration,
            "音频流已中断或当前系统不支持此格式。"));
    }

    private void OnProgressTimerTick(object? sender, EventArgs args)
    {
        if (_player is null
            || _snapshot.Status != FeedAudioPlaybackStatus.Playing)
        {
            return;
        }
        Publish(ReadSnapshot(FeedAudioPlaybackStatus.Playing));
    }

    private FeedAudioPlaybackSnapshot ReadSnapshot(
        FeedAudioPlaybackStatus status,
        TimeSpan? knownDuration = null)
    {
        if (_player is null)
        {
            return FeedAudioPlaybackSnapshot.Idle;
        }
        TimeSpan? duration = knownDuration
            ?? (_player.NaturalDuration.HasTimeSpan
                ? _player.NaturalDuration.TimeSpan
                : _snapshot.Duration);
        return new(
            _snapshot.SourceUrl,
            status,
            _player.Position,
            duration);
    }

    private void ClosePlayer()
    {
        _progressTimer.Stop();
        MediaPlayer? player = _player;
        _player = null;
        if (player is null)
        {
            return;
        }
        player.MediaOpened -= OnMediaOpened;
        player.MediaEnded -= OnMediaEnded;
        player.MediaFailed -= OnMediaFailed;
        player.Close();
    }

    private void Publish(FeedAudioPlaybackSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke(
            this,
            new FeedAudioPlaybackChangedEventArgs(snapshot));
    }

    private static Uri ValidateSource(
        FeedAudioPlaybackRequest request)
    {
        FeedAttachmentClassification attachment =
            FeedAttachmentClassifier.Classify(
                new(
                    request.SourceUrl,
                    request.MediaType,
                    null,
                    null),
                baseUrl: null);
        if (attachment.UrlStatus != FeedAttachmentUrlStatus.Allowed
            || !attachment.IsTypeVerified
            || attachment.Kind != FeedAttachmentKind.Audio
            || !Uri.TryCreate(
                attachment.SafeUrl,
                UriKind.Absolute,
                out Uri? source))
        {
            throw new ArgumentException(
                "音频来源必须通过地址和类型一致性验证。",
                nameof(request));
        }
        return source;
    }

    private static double NormalizeProgress(double progress) =>
        double.IsFinite(progress)
            ? Math.Clamp(progress, 0d, 100d)
            : 0d;
}
