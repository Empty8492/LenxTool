using System.Collections.ObjectModel;
using System.Globalization;
using LenxTool.App.Mvvm;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

/// <summary>
/// 描述统一发现页的可见状态，避免把“无结果、离线、限流”折叠成同一种错误。
/// </summary>
public enum FeedDiscoveryPageState
{
    Idle,
    Debouncing,
    Loading,
    Ready,
    Partial,
    Empty,
    Offline,
    RateLimited,
    Cancelled,
    InvalidInput,
    Forbidden,
    Error
}

/// <summary>
/// 只读近期条目摘要；正文仍留在本地 SQLite，不进入发现候选或 Worker。
/// </summary>
public sealed record FeedDiscoveryPreviewEntryViewModel(
    string Title,
    string PublishedText);

/// <summary>
/// 将统一发现契约转换为可直接绑定的候选卡片。
/// </summary>
public sealed record FeedDiscoveryCandidateViewModel(
    string Title,
    string FeedUrl,
    string SiteText,
    string DocumentKindText,
    string HealthText,
    string SourceText,
    string WarningText,
    string UpdatedText,
    IReadOnlyList<FeedDiscoveryPreviewEntryViewModel> RecentEntries)
{
    public bool HasRecentEntries => RecentEntries.Count > 0;

    public string PreviewStatus => HasRecentEntries
        ? $"本机缓存中的最近 {RecentEntries.Count} 条"
        : "本机尚无该订阅的近期缓存";
}

/// <summary>
/// DISC-04 管理员统一发现页。它只读取候选和本地预览，不执行共享目录写入。
/// </summary>
public sealed class FeedDiscoveryViewModel : PageViewModel, IDisposable
{
    private static readonly TimeSpan DefaultDebounceDelay =
        TimeSpan.FromMilliseconds(450);

    private readonly IUnifiedFeedDiscoveryService _discoveryService;
    private readonly IFeedCatalogRepository _catalogRepository;
    private readonly IFeedDiscoveryPreviewRepository _previewRepository;
    private readonly IAccountSessionService _accountSession;
    private readonly TimeSpan _debounceDelay;
    private readonly SynchronizationContext? _synchronizationContext;
    private CancellationTokenSource? _debounceCancellation;
    private CancellationTokenSource? _activeSearchCancellation;
    private Task? _debounceTask;
    private long _requestGeneration;
    private string _input = string.Empty;
    private string _queryKindLabel = "等待输入";
    private string _status = "输入关键词、站点地址或 Feed 地址开始发现。";
    private string? _lastSubmittedInput;
    private FeedDiscoveryPageState _state;
    private bool _isAdmin;
    private bool _isBusy;
    private bool _disposed;

    public FeedDiscoveryViewModel(
        IUnifiedFeedDiscoveryService discoveryService,
        IFeedCatalogRepository catalogRepository,
        IFeedDiscoveryPreviewRepository previewRepository,
        IAccountSessionService accountSession)
        : this(
            discoveryService,
            catalogRepository,
            previewRepository,
            accountSession,
            DefaultDebounceDelay)
    {
    }

    internal FeedDiscoveryViewModel(
        IUnifiedFeedDiscoveryService discoveryService,
        IFeedCatalogRepository catalogRepository,
        IFeedDiscoveryPreviewRepository previewRepository,
        IAccountSessionService accountSession,
        TimeSpan debounceDelay)
        : base(
            "发现订阅",
            "统一搜索已知目录与安全直连来源；本页只读，发布将在确认页单独完成")
    {
        _discoveryService = discoveryService;
        _catalogRepository = catalogRepository;
        _previewRepository = previewRepository;
        _accountSession = accountSession;
        _debounceDelay = debounceDelay >= TimeSpan.Zero
            ? debounceDelay
            : throw new ArgumentOutOfRangeException(nameof(debounceDelay));
        _synchronizationContext = SynchronizationContext.Current;
        Candidates = [];
        SearchCommand = new(SearchImmediatelyAsync, CanSearch);
        RetryCommand = new(SearchImmediatelyAsync, CanRetry);
        CancelCommand = new(CancelSearch, CanCancel);

        _accountSession.SessionChanged += OnSessionChanged;
        ApplySession(_accountSession.Current);
    }

    public ObservableCollection<FeedDiscoveryCandidateViewModel> Candidates
    {
        get;
    }

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public RelayCommand CancelCommand { get; }

    public bool IsAdmin => _isAdmin;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(ShowEmptyState));
            NotifyCommands();
        }
    }

    public bool HasCandidates => Candidates.Count > 0;

    public bool ShowEmptyState => !IsBusy && !HasCandidates;

    public FeedDiscoveryPageState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(CanShowRetry));
            OnPropertyChanged(nameof(ShowEmptyState));
            NotifyCommands();
        }
    }

    public bool CanShowRetry => State is
        FeedDiscoveryPageState.Partial
        or FeedDiscoveryPageState.Empty
        or FeedDiscoveryPageState.Offline
        or FeedDiscoveryPageState.RateLimited
        or FeedDiscoveryPageState.Error;

    public string Input
    {
        get => _input;
        set
        {
            string normalized = value ?? string.Empty;
            if (!SetProperty(ref _input, normalized)) return;
            InvalidateActiveSearch();
            // 输入一旦变化就移除旧候选，避免防抖期间展示与当前查询不相符的结果。
            ClearCandidates();
            UpdateQueryKind(normalized);
            QueueDebouncedSearch();
            NotifyCommands();
        }
    }

    public string QueryKindLabel
    {
        get => _queryKindLabel;
        private set => SetProperty(ref _queryKindLabel, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string? LastSubmittedInput
    {
        get => _lastSubmittedInput;
        private set => SetProperty(ref _lastSubmittedInput, value);
    }

    private bool CanSearch() =>
        IsAdmin
        && !IsBusy
        && FeedDiscoveryQueryClassifier.Classify(Input).IsValid;

    private bool CanRetry() =>
        CanSearch()
        && CanShowRetry;

    private bool CanCancel() =>
        IsAdmin
        && (IsBusy || _debounceTask is { IsCompleted: false });

    private void QueueDebouncedSearch()
    {
        CancelDebounce();
        FeedDiscoveryQuery query =
            FeedDiscoveryQueryClassifier.Classify(Input);
        if (!IsAdmin || !query.IsValid)
        {
            if (string.IsNullOrWhiteSpace(Input))
            {
                ClearCandidates();
                State = IsAdmin
                    ? FeedDiscoveryPageState.Idle
                    : FeedDiscoveryPageState.Forbidden;
                Status = IsAdmin
                    ? "输入关键词、站点地址或 Feed 地址开始发现。"
                    : "需要管理员账号才能使用统一发现。";
            }
            else if (IsAdmin)
            {
                State = FeedDiscoveryPageState.InvalidInput;
                Status = "输入格式无效，请使用关键词、HTTP/HTTPS 地址或受支持的 RSSHub 路由。";
            }
            return;
        }

        // 每次输入都建立新的防抖代次；旧任务即使忽略取消，也会被请求代次挡住。
        var cancellation = new CancellationTokenSource();
        _debounceCancellation = cancellation;
        State = FeedDiscoveryPageState.Debouncing;
        Status = $"已识别为{QueryKindLabel}，等待输入稳定…";
        string inputSnapshot = Input;
        _debounceTask = DebounceAndSearchAsync(inputSnapshot, cancellation);
        CancelCommand.NotifyCanExecuteChanged();
    }

    private async Task DebounceAndSearchAsync(
        string input,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_debounceDelay, cancellation.Token)
                .ConfigureAwait(true);
            if (!string.Equals(input, Input, StringComparison.Ordinal)) return;
            await RunSearchAsync(input, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 输入变化和显式取消都是预期路径，不向用户暴露异常。
        }
        finally
        {
            if (ReferenceEquals(_debounceCancellation, cancellation))
            {
                _debounceCancellation = null;
                _debounceTask = null;
                cancellation.Dispose();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private async Task SearchImmediatelyAsync(CancellationToken cancellationToken)
    {
        CancelDebounce();
        await RunSearchAsync(Input, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 为每次搜索建立独立代次和取消源，确保忽略取消的旧 provider 也不能覆盖新结果。
    /// </summary>
    private async Task RunSearchAsync(
        string input,
        CancellationToken cancellationToken)
    {
        FeedDiscoveryQuery query =
            FeedDiscoveryQueryClassifier.Classify(input);
        if (!IsAdmin || !query.IsValid) return;

        long generation = Interlocked.Increment(ref _requestGeneration);
        CancellationTokenSource? previous = _activeSearchCancellation;
        var active = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        _activeSearchCancellation = active;
        previous?.Cancel();
        previous?.Dispose();

        LastSubmittedInput = input;
        ClearCandidates();
        State = FeedDiscoveryPageState.Loading;
        Status = "正在从可用来源查找订阅…";
        IsBusy = true;
        try
        {
            Task<UnifiedFeedDiscoveryResult> discoveryTask =
                _discoveryService.DiscoverAsync(input, active.Token);
            // 服务实现即使忽略取消，界面命令也必须及时结束，不能永久占用搜索入口。
            UnifiedFeedDiscoveryResult result = await discoveryTask
                .WaitAsync(active.Token)
                .ConfigureAwait(true);
            IReadOnlyList<FeedDiscoveryCandidateViewModel> candidates =
                await BuildCandidatesAsync(result.Candidates, active.Token)
                    .ConfigureAwait(true);
            if (!IsCurrent(generation, active, input)) return;

            foreach (FeedDiscoveryCandidateViewModel candidate in candidates)
            {
                Candidates.Add(candidate);
            }
            NotifyCandidateState();
            ApplyResultState(result);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(generation, active, input))
            {
                State = FeedDiscoveryPageState.Cancelled;
                Status = "本次发现已取消。";
            }
        }
        catch (AppException exception)
        {
            if (IsCurrent(generation, active, input))
            {
                ApplyError(exception.Error);
            }
        }
        catch (Exception)
        {
            if (IsCurrent(generation, active, input))
            {
                State = FeedDiscoveryPageState.Error;
                Status = "发现服务暂时不可用，请稍后重试。";
            }
        }
        finally
        {
            if (ReferenceEquals(_activeSearchCancellation, active))
            {
                _activeSearchCancellation = null;
                IsBusy = false;
                active.Dispose();
            }
        }
    }

    /// <summary>
    /// 将候选映射为 UI 卡片，并以本地只读预览作为可降级的附加信息。
    /// </summary>
    private async Task<IReadOnlyList<FeedDiscoveryCandidateViewModel>>
        BuildCandidatesAsync(
            IReadOnlyList<FeedDiscoveryCandidate> candidates,
            CancellationToken cancellationToken)
    {
        FeedCatalogSnapshot? catalog;
        try
        {
            catalog = await _catalogRepository
                .GetCatalogAsync(FeedCatalogScope.Active, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 本地预览是附加信息；数据库读取失败不能吞掉已经验证的发现候选。
            return candidates
                .Select(candidate => MapCandidate(candidate, []))
                .ToArray();
        }
        Dictionary<string, FeedCatalogItem> localFeeds =
            catalog?.Feeds.ToDictionary(
                feed => feed.NormalizedUrl,
                StringComparer.Ordinal)
            ?? new(StringComparer.Ordinal);
        string[] previewFeedIds = candidates
            .Select(candidate => localFeeds.GetValueOrDefault(
                candidate.NormalizedFeedUrl)?.Id)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, IReadOnlyList<FeedDiscoveryPreviewEntryViewModel>>
            previews = await LoadLocalPreviewsAsync(
                    previewFeedIds,
                    cancellationToken)
                .ConfigureAwait(true);
        var result =
            new List<FeedDiscoveryCandidateViewModel>(candidates.Count);
        foreach (FeedDiscoveryCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FeedDiscoveryPreviewEntryViewModel> preview =
                localFeeds.TryGetValue(
                    candidate.NormalizedFeedUrl,
                    out FeedCatalogItem? feed)
                && previews.TryGetValue(feed.Id, out var items)
                    ? items
                    : [];
            result.Add(MapCandidate(candidate, preview));
        }
        return result;
    }

    /// <summary>
    /// 一次批量读取所有匹配候选的标题/时间投影；失败时只降级预览，不丢候选。
    /// </summary>
    private async Task<
        Dictionary<string, IReadOnlyList<FeedDiscoveryPreviewEntryViewModel>>>
        LoadLocalPreviewsAsync(
            string[] feedIds,
            CancellationToken cancellationToken)
    {
        if (feedIds.Length == 0) return new(StringComparer.Ordinal);
        try
        {
            IReadOnlyList<FeedDiscoveryPreviewItem> items =
                await _previewRepository.GetRecentAsync(
                        feedIds,
                        maximumPerFeed: 4,
                        localProfile: "default",
                        cancellationToken)
                    .ConfigureAwait(true);
            HashSet<string> requested = feedIds.ToHashSet(
                StringComparer.Ordinal);
            return items
                .Where(item => requested.Contains(item.FeedId))
                .GroupBy(item => item.FeedId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<
                        FeedDiscoveryPreviewEntryViewModel>)group
                        .Take(4)
                        .Select(item =>
                            new FeedDiscoveryPreviewEntryViewModel(
                                item.Title,
                                FormatTimestamp(item.PublishedAt)))
                        .ToArray(),
                    StringComparer.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 预览数据库损坏或占用时保持候选可见，用户仍可稍后重试。
            return new(StringComparer.Ordinal);
        }
    }

    private static FeedDiscoveryCandidateViewModel MapCandidate(
        FeedDiscoveryCandidate candidate,
        IReadOnlyList<FeedDiscoveryPreviewEntryViewModel> preview) =>
        new(
            candidate.Title ?? candidate.NormalizedFeedUrl,
            candidate.NormalizedFeedUrl,
            candidate.SiteUrl ?? "未提供站点地址",
            candidate.DocumentKind switch
            {
                FeedDocumentKind.Rss20 => "RSS 2.0",
                FeedDocumentKind.Atom => "Atom",
                _ => "待验证"
            },
            candidate.Health switch
            {
                FeedDiscoveryHealth.Healthy => "健康",
                FeedDiscoveryHealth.Degraded => "降级",
                FeedDiscoveryHealth.Unavailable => "不可用",
                _ => "未知"
            },
            FormatSources(candidate.Evidence),
            FormatWarnings(candidate.Warnings),
            candidate.LastUpdatedAt is DateTimeOffset updated
                ? FormatTimestamp(updated)
                : "更新时间未知",
            preview);

    private static string FormatSources(
        IReadOnlyList<FeedDiscoveryEvidence> evidence) =>
        string.Join(
            " · ",
            evidence
                .Select(item => item.SourceKind switch
                {
                    FeedDiscoverySourceKind.KnownCatalog => "已知目录",
                    FeedDiscoverySourceKind.DirectProbe => "安全直连",
                    FeedDiscoverySourceKind.RssHubAdapter => "RSSHub",
                    _ => "外部来源"
                })
                .Distinct(StringComparer.Ordinal));

    private static string FormatWarnings(
        IReadOnlyList<FeedDiscoveryWarning> warnings) =>
        warnings.Count == 0
            ? "未发现风险提示"
            : string.Join(
                " · ",
                warnings.Select(item => item.Code switch
                {
                    FeedDiscoveryWarningCode.Stale => "目录信息可能过期",
                    FeedDiscoveryWarningCode.InsecureTransport =>
                        "使用未加密 HTTP",
                    FeedDiscoveryWarningCode.Unverified => "来源尚未验证",
                    FeedDiscoveryWarningCode.ProviderPartialFailure =>
                        "部分来源失败",
                    FeedDiscoveryWarningCode.RateLimited => "来源正在限流",
                    _ => "需要人工复核"
                }).Distinct(StringComparer.Ordinal));

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString(
            "yyyy-MM-dd HH:mm",
            CultureInfo.CurrentCulture);

    /// <summary>
    /// 按来源完成度优先映射页面状态，避免把部分失败伪装成干净的空结果。
    /// </summary>
    private void ApplyResultState(UnifiedFeedDiscoveryResult result)
    {
        if (result.Status == FeedDiscoveryCompletionStatus.Unavailable)
        {
            bool rateLimited = result.Sources.Any(source =>
                source.Status == FeedDiscoverySourceStatus.RateLimited);
            State = rateLimited
                ? FeedDiscoveryPageState.RateLimited
                : FeedDiscoveryPageState.Offline;
            Status = rateLimited
                ? "发现来源正在限流，请稍后重试。"
                : "当前无法连接任何发现来源，可检查网络后重试。";
            return;
        }
        if (result.Status == FeedDiscoveryCompletionStatus.Partial)
        {
            State = FeedDiscoveryPageState.Partial;
            Status = Candidates.Count == 0
                ? "部分来源暂时不可用，当前没有可确认的候选。"
                : $"已找到 {Candidates.Count} 个候选；部分来源暂时不可用。";
            return;
        }
        if (Candidates.Count == 0)
        {
            State = FeedDiscoveryPageState.Empty;
            Status = "没有找到匹配的订阅，可以尝试更具体的关键词或 Feed 地址。";
            return;
        }

        State = FeedDiscoveryPageState.Ready;
        Status = $"已找到 {Candidates.Count} 个候选。";
    }

    private void ApplyError(AppError error)
    {
        State = error.Code switch
        {
            AppErrorCode.ProviderRateLimited =>
                FeedDiscoveryPageState.RateLimited,
            AppErrorCode.NetworkUnavailable
                or AppErrorCode.ProviderUnavailable
                or AppErrorCode.Timeout =>
                FeedDiscoveryPageState.Offline,
            _ => FeedDiscoveryPageState.Error
        };
        Status = $"{error.Title}：{error.Suggestion}";
    }

    private bool IsCurrent(
        long generation,
        CancellationTokenSource cancellation,
        string input) =>
        generation == Volatile.Read(ref _requestGeneration)
        && ReferenceEquals(_activeSearchCancellation, cancellation)
        && string.Equals(Input, input, StringComparison.Ordinal)
        && IsAdmin;

    private void CancelSearch()
    {
        CancelDebounce();
        InvalidateActiveSearch();
        if (IsAdmin)
        {
            State = FeedDiscoveryPageState.Cancelled;
            Status = "本次发现已取消。";
        }
    }

    /// <summary>
    /// 输入、取消或降权时立即终止当前请求；代次失效是结果隔离的最终防线。
    /// </summary>
    private void InvalidateActiveSearch()
    {
        Interlocked.Increment(ref _requestGeneration);
        CancellationTokenSource? active = _activeSearchCancellation;
        _activeSearchCancellation = null;
        active?.Cancel();
        active?.Dispose();
        IsBusy = false;
    }

    private void CancelDebounce()
    {
        CancellationTokenSource? cancellation = _debounceCancellation;
        _debounceCancellation = null;
        _debounceTask = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void UpdateQueryKind(string input)
    {
        FeedDiscoveryQuery query =
            FeedDiscoveryQueryClassifier.Classify(input);
        QueryKindLabel = query.Kind switch
        {
            FeedDiscoveryQueryKind.Url => "URL",
            FeedDiscoveryQueryKind.RssHubRoute => "RSSHub 路由",
            FeedDiscoveryQueryKind.Keyword => "关键词",
            _ when query.Error == FeedDiscoveryQueryError.Empty => "等待输入",
            _ => "输入格式无效"
        };
    }

    private void OnSessionChanged(
        object? sender,
        AccountSessionChangedEventArgs eventArgs)
    {
        if (_synchronizationContext is not null
            && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(
                _ => ApplySession(eventArgs.Session),
                null);
            return;
        }
        ApplySession(eventArgs.Session);
    }

    /// <summary>
    /// 会话降权时同步取消请求并清空候选，防止隐藏入口后仍保留管理员数据。
    /// </summary>
    private void ApplySession(AccountSessionSnapshot session)
    {
        bool isAdmin = session.IsAdmin;
        bool roleChanged =
            SetProperty(ref _isAdmin, isAdmin, nameof(IsAdmin));
        if (!isAdmin)
        {
            CancelSearch();
            ClearCandidates();
            State = FeedDiscoveryPageState.Forbidden;
            Status = "需要管理员账号才能使用统一发现。";
        }
        else
        {
            State = FeedDiscoveryPageState.Idle;
            Status = "输入关键词、站点地址或 Feed 地址开始发现。";
        }
        if (roleChanged) NotifyCommands();
    }

    private void ClearCandidates()
    {
        Candidates.Clear();
        NotifyCandidateState();
    }

    private void NotifyCandidateState()
    {
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void NotifyCommands()
    {
        SearchCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accountSession.SessionChanged -= OnSessionChanged;
        CancelSearch();
        SearchCommand.Dispose();
        RetryCommand.Dispose();
    }
}
