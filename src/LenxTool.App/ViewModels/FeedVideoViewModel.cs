using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed class FeedVideoItem
{
    public FeedVideoItem(FeedContentItem content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        (VideoEnclosure, VideoAttachment) =
            FindFirst(content.Entry, FeedAttachmentKind.Video);
        (_, FeedAttachmentClassification? poster) =
            FindFirst(content.Entry, FeedAttachmentKind.Image);
        PosterUrl = poster?.SafeUrl;
        SafeOriginalUrl = ValidateExternalUrl(content.SafeOriginalUrl);
    }

    public FeedContentItem Content { get; }
    public FeedEntry Entry => Content.Entry;
    public string FeedName => Content.FeedName;
    public string CategoryName => Content.CategoryName;
    public string Title => Content.Title;
    public string Summary => Content.Summary;
    public DateTimeOffset DisplayTime => Content.DisplayTime;
    public bool IsStarred => Content.IsStarred;
    public FeedEnclosure? VideoEnclosure { get; }
    public FeedAttachmentClassification? VideoAttachment { get; }
    public string? PosterUrl { get; }
    public string? SafeOriginalUrl { get; }
    public bool CanDeliver => VideoAttachment is not null;
    public string SourceHost => ReadHost(
        VideoAttachment?.SafeUrl,
        "视频来源不可用");
    public string OriginalHost => ReadHost(
        SafeOriginalUrl,
        "原文来源不可用");
    public string DurationText { get; } = "时长未知";
    public string MediaDetails =>
        VideoAttachment is null
            ? "没有可验证的视频附件"
            : string.Join(
                " · ",
                new[]
                {
                    VideoAttachment.NormalizedMediaType
                        ?? "类型未知",
                    FormatLength(VideoAttachment.Length)
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static (
        FeedEnclosure? Enclosure,
        FeedAttachmentClassification? Attachment)
        FindFirst(
            FeedEntry entry,
            FeedAttachmentKind expectedKind)
    {
        foreach (FeedEnclosure enclosure in entry.Enclosures)
        {
            FeedAttachmentClassification attachment =
                FeedAttachmentClassifier.Classify(
                    enclosure,
                    entry.NormalizedUrl);
            if (attachment.UrlStatus == FeedAttachmentUrlStatus.Allowed
                && attachment.IsTypeVerified
                && attachment.Kind == expectedKind)
            {
                return (enclosure, attachment);
            }
        }
        return (null, null);
    }

    private static string? ValidateExternalUrl(string? value)
    {
        if (value is null)
        {
            return null;
        }
        FeedAttachmentClassification classification =
            FeedAttachmentClassifier.Classify(
                new(value, null, null, null),
                baseUrl: null);
        return classification.UrlStatus == FeedAttachmentUrlStatus.Allowed
            ? classification.SafeUrl
            : null;
    }

    private static string ReadHost(
        string? value,
        string fallback) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            ? uri.IdnHost
            : fallback;

    internal static string FormatLength(long? length)
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

public sealed class FeedVideoViewModel : ObservableObject, IDisposable
{
    private readonly IFeedVideoDeliveryPlanningService _planner;
    private readonly IFeedMediaDeliveryService _delivery;
    private readonly IMediaJobInbox _mediaJobInbox;
    private readonly IAppNavigationService _navigation;
    private readonly Action<string> _openUri;
    private FeedVideoItem? _selectedItem;
    private FeedVideoDeliveryPlan? _pendingDeliveryPlan;
    private string? _pendingExternalUrl;
    private string _status = "正在读取本地视频缓存…";
    private bool _disposed;

    public FeedVideoViewModel(
        FeedContentCollectionViewModel feed,
        IFeedVideoDeliveryPlanningService planner,
        IFeedMediaDeliveryService delivery,
        IMediaJobInbox mediaJobInbox,
        IAppNavigationService navigation,
        Action<string> openUri)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(mediaJobInbox);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(openUri);
        if (feed.ViewKind != EntryViewKind.Video)
        {
            throw new ArgumentException(
                "视频视图只能组合 Video 内容集合。",
                nameof(feed));
        }

        Feed = feed;
        _planner = planner;
        _delivery = delivery;
        _mediaJobInbox = mediaJobInbox;
        _navigation = navigation;
        _openUri = openUri;
        PrepareDeliveryCommand = new(
            PrepareDeliveryAsync,
            CanPrepareDelivery);
        ConfirmDeliveryCommand = new(
            ConfirmDeliveryAsync,
            () => HasPendingDeliveryConfirmation);
        CancelDeliveryCommand = new(CancelDelivery);
        RequestExternalOpenCommand = new(
            RequestExternalOpen,
            CanRequestExternalOpen);
        ConfirmExternalOpenCommand = new(
            ConfirmExternalOpen,
            () => HasPendingExternalConfirmation);
        CancelExternalOpenCommand = new(CancelExternalOpen);
        Feed.Items.CollectionChanged += OnFeedItemsChanged;
    }

    public FeedContentCollectionViewModel Feed { get; }
    public ObservableCollection<FeedVideoItem> Items { get; } = [];
    public AsyncRelayCommand PrepareDeliveryCommand { get; }
    public AsyncRelayCommand ConfirmDeliveryCommand { get; }
    public RelayCommand CancelDeliveryCommand { get; }
    public RelayCommand RequestExternalOpenCommand { get; }
    public RelayCommand ConfirmExternalOpenCommand { get; }
    public RelayCommand CancelExternalOpenCommand { get; }

    public FeedVideoItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
            {
                return;
            }
            PrepareDeliveryCommand.Cancel();
            ConfirmDeliveryCommand.Cancel();
            ClearDeliveryPlan();
            ClearExternalConfirmation();
            NotifyCommandStates();
            Status = value is null
                ? "当前筛选下没有视频"
                : value.CanDeliver
                    ? "已选择视频；打开原文或下载转写都需要明确操作。"
                    : "此条目没有可验证的视频附件，仅可按安全结果打开原文。";
        }
    }

    public FeedVideoDeliveryPlan? PendingDeliveryPlan
    {
        get => _pendingDeliveryPlan;
        private set
        {
            if (!SetProperty(ref _pendingDeliveryPlan, value))
            {
                return;
            }
            OnPropertyChanged(nameof(HasPendingDeliveryConfirmation));
            OnPropertyChanged(nameof(PendingDeclaredSize));
            OnPropertyChanged(nameof(PendingMaximumSize));
            OnPropertyChanged(nameof(PendingAvailableSpace));
            OnPropertyChanged(nameof(PendingTargetDirectory));
            ConfirmDeliveryCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasPendingDeliveryConfirmation =>
        PendingDeliveryPlan is not null;
    public string PendingDeclaredSize =>
        FeedVideoItem.FormatLength(
            PendingDeliveryPlan?.DeclaredBytes);
    public string PendingMaximumSize =>
        FeedVideoItem.FormatLength(
            PendingDeliveryPlan?.MaximumBytes);
    public string PendingAvailableSpace =>
        FeedVideoItem.FormatLength(
            PendingDeliveryPlan?.AvailableBytes);
    public string PendingTargetDirectory =>
        PendingDeliveryPlan?.TargetDirectory ?? "";
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
        Status = "视频流加载失败；其他资讯视图仍可使用。";
    }

    private async Task PrepareDeliveryAsync(
        CancellationToken cancellationToken)
    {
        FeedVideoItem? item = SelectedItem;
        if (item?.VideoEnclosure is null)
        {
            return;
        }
        ClearDeliveryPlan();
        Status = "正在检查视频大小、目标目录和可用空间…";
        try
        {
            FeedVideoDeliveryPlan plan =
                await _planner.PlanAsync(
                    item.Entry,
                    item.VideoEnclosure,
                    cancellationToken);
            if (!ApplyBlockedPlan(plan))
            {
                return;
            }
            if (plan.RequiresConfirmation)
            {
                PendingDeliveryPlan = plan;
                Status = plan.DeclaredBytes is null
                    ? "视频大小未知；确认后最多下载到安全上限。"
                    : "这是较大的视频；请核对大小、目标和空间后确认。";
                return;
            }
            await DeliverAsync(item, cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Status = "视频下载已取消；未发布新的媒体任务。";
            throw;
        }
        catch
        {
            Status = "视频投递计划失败；未启动下载。";
        }
    }

    private async Task ConfirmDeliveryAsync(
        CancellationToken cancellationToken)
    {
        FeedVideoItem? item = SelectedItem;
        FeedVideoDeliveryPlan? confirmed = PendingDeliveryPlan;
        if (item?.VideoEnclosure is null || confirmed is null)
        {
            return;
        }
        ClearDeliveryPlan();
        Status = "正在重新检查磁盘空间和同源任务…";
        try
        {
            FeedVideoDeliveryPlan current =
                await _planner.PlanAsync(
                    item.Entry,
                    item.VideoEnclosure,
                    cancellationToken);
            if (!ApplyBlockedPlan(current))
            {
                return;
            }
            if (!IsSameConfirmation(confirmed, current))
            {
                PendingDeliveryPlan = current;
                Status = "视频计划已经变化，请重新核对后再次确认。";
                return;
            }
            await DeliverAsync(item, cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Status = "视频下载已取消；未发布新的媒体任务。";
            throw;
        }
        catch
        {
            Status = "视频下载或任务交接失败；可重新检查后重试。";
        }
    }

    private async Task DeliverAsync(
        FeedVideoItem item,
        CancellationToken cancellationToken)
    {
        Status = "正在安全下载视频并登记媒体任务…";
        FeedMediaDeliveryRegistration registration =
            await _delivery.DeliverAsync(
                item.Entry,
                item.VideoEnclosure!,
                cancellationToken);
        _mediaJobInbox.PublishQueued(registration.Job);
        await _navigation.NavigateAsync(
            new(
                "media",
                "media_job",
                registration.Job.Id),
            CancellationToken.None);
        Status = registration.Created
            ? "视频已安全下载并进入媒体工作台。"
            : "已有同源媒体任务，已在媒体工作台中定位。";
    }

    private bool ApplyBlockedPlan(
        FeedVideoDeliveryPlan plan)
    {
        if (plan.Status == FeedVideoDeliveryPlanStatus.ExceedsLimit)
        {
            Status = $"视频超过 {FeedVideoItem.FormatLength(plan.MaximumBytes)} 的安全上限。";
            return false;
        }
        if (plan.Status
            == FeedVideoDeliveryPlanStatus.InsufficientSpace)
        {
            Status = "目标磁盘空间不足，未启动视频下载。";
            return false;
        }
        return plan.CanDeliver;
    }

    private static bool IsSameConfirmation(
        FeedVideoDeliveryPlan confirmed,
        FeedVideoDeliveryPlan current) =>
        string.Equals(
            confirmed.EntryId,
            current.EntryId,
            StringComparison.Ordinal)
        && string.Equals(
            confirmed.SourceUrl,
            current.SourceUrl,
            StringComparison.Ordinal)
        && string.Equals(
            confirmed.TargetDirectory,
            current.TargetDirectory,
            StringComparison.OrdinalIgnoreCase)
        && confirmed.DeclaredBytes == current.DeclaredBytes
        && confirmed.RequiredMediaBytes == current.RequiredMediaBytes
        && confirmed.MaximumBytes == current.MaximumBytes;

    private bool CanPrepareDelivery() =>
        !_disposed && SelectedItem?.CanDeliver == true;

    private void CancelDelivery()
    {
        PrepareDeliveryCommand.Cancel();
        ConfirmDeliveryCommand.Cancel();
        if (HasPendingDeliveryConfirmation)
        {
            ClearDeliveryPlan();
            Status = "已取消视频下载确认。";
        }
    }

    private void RequestExternalOpen()
    {
        if (!CanRequestExternalOpen())
        {
            return;
        }
        _pendingExternalUrl = SelectedItem!.SafeOriginalUrl;
        OnPropertyChanged(nameof(HasPendingExternalConfirmation));
        ConfirmExternalOpenCommand.NotifyCanExecuteChanged();
        Status = "确认后将在默认浏览器打开原文；不会内嵌网页播放器或直接打开视频附件。";
    }

    private bool CanRequestExternalOpen() =>
        !_disposed && SelectedItem?.SafeOriginalUrl is not null;

    private void ConfirmExternalOpen()
    {
        string? selectedUrl = SelectedItem?.SafeOriginalUrl;
        if (_pendingExternalUrl is null
            || !string.Equals(
                _pendingExternalUrl,
                selectedUrl,
                StringComparison.Ordinal))
        {
            CancelExternalOpen();
            return;
        }
        string target = _pendingExternalUrl;
        ClearExternalConfirmation();
        _openUri(target);
        Status = "已在默认浏览器打开视频原文。";
    }

    private void CancelExternalOpen()
    {
        if (_pendingExternalUrl is null)
        {
            return;
        }
        ClearExternalConfirmation();
        Status = "已取消外部打开。";
    }

    private void ClearExternalConfirmation()
    {
        if (_pendingExternalUrl is null)
        {
            return;
        }
        _pendingExternalUrl = null;
        OnPropertyChanged(nameof(HasPendingExternalConfirmation));
        ConfirmExternalOpenCommand.NotifyCanExecuteChanged();
    }

    private void ClearDeliveryPlan()
    {
        PendingDeliveryPlan = null;
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
                Items.Add(new(content));
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
            : Items.FirstOrDefault(item =>
                string.Equals(
                    item.Entry.Id,
                    selectedEntryId,
                    StringComparison.Ordinal))
                ?? Items.FirstOrDefault();
    }

    private void NotifyCommandStates()
    {
        PrepareDeliveryCommand.NotifyCanExecuteChanged();
        ConfirmDeliveryCommand.NotifyCanExecuteChanged();
        RequestExternalOpenCommand.NotifyCanExecuteChanged();
        ConfirmExternalOpenCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Feed.Items.CollectionChanged -= OnFeedItemsChanged;
        PrepareDeliveryCommand.Dispose();
        ConfirmDeliveryCommand.Dispose();
        Feed.Dispose();
    }
}
