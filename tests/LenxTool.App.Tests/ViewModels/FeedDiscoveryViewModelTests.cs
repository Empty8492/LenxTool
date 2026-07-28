using LenxTool.App.ViewModels;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

/// <summary>
/// 冻结 DISC-04 的只读发现页行为；目录发布仍由后续 DISC-05 单独验收。
/// </summary>
public sealed class FeedDiscoveryViewModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SearchMapsPartialStateAndLoadsAtMostFourRealLocalPreviews()
    {
        var discovery = new FakeUnifiedDiscoveryService();
        discovery.Results.Enqueue(new(
            ValidQuery("reader"),
            [Candidate("https://feeds.example/feed.xml")],
            [
                new(
                    "worker:known-catalog",
                    FeedDiscoverySourceKind.KnownCatalog,
                    FeedDiscoverySourceStatus.Succeeded,
                    1,
                    false,
                    false),
                new(
                    "direct-probe",
                    FeedDiscoverySourceKind.DirectProbe,
                    FeedDiscoverySourceStatus.TimedOut,
                    0,
                    false,
                    false)
            ],
            FeedDiscoveryCompletionStatus.Partial));
        var entries = new FakePreviewRepository(
            Enumerable.Range(1, 6)
                .Select(index => Preview(index))
                .ToArray());
        using var viewModel = CreateViewModel(
            discovery,
            entries,
            AccountRole.Admin,
            TimeSpan.FromHours(1));
        viewModel.Input = "reader";

        await viewModel.SearchCommand.ExecuteAsync();

        Assert.Equal(FeedDiscoveryPageState.Partial, viewModel.State);
        Assert.Contains("部分", viewModel.Status, StringComparison.Ordinal);
        FeedDiscoveryCandidateViewModel candidate =
            Assert.Single(viewModel.Candidates);
        Assert.Equal(4, candidate.RecentEntries.Count);
        Assert.Equal("第 1 条", candidate.RecentEntries[0].Title);
        Assert.Single(entries.Queries);
        Assert.Equal(4, entries.Queries[0].MaximumPerFeed);
        Assert.Equal(["feed-1"], entries.Queries[0].FeedIds);
    }

    [Fact]
    public async Task NewDebouncedInputWinsEvenWhenOldProviderIgnoresCancellation()
    {
        var discovery = new ControlledUnifiedDiscoveryService();
        using var viewModel = CreateViewModel(
            discovery,
            new FakePreviewRepository([]),
            AccountRole.Admin,
            TimeSpan.Zero);

        viewModel.Input = "旧关键词";
        await WaitUntilAsync(
            () => discovery.Calls.Contains("旧关键词"),
            TimeSpan.FromSeconds(2));
        viewModel.Input = "新关键词";
        await WaitUntilAsync(
            () => discovery.Calls.Contains("新关键词"),
            TimeSpan.FromSeconds(2));

        discovery.Complete(
            "新关键词",
            new(
                ValidQuery("新关键词"),
                [Candidate("https://new.example/feed.xml", "新结果")],
                [SuccessfulSource()],
                FeedDiscoveryCompletionStatus.Complete));
        await WaitUntilAsync(
            () => viewModel.Candidates.SingleOrDefault()?.Title == "新结果",
            TimeSpan.FromSeconds(2));
        discovery.Complete(
            "旧关键词",
            new(
                ValidQuery("旧关键词"),
                [Candidate("https://old.example/feed.xml", "旧结果")],
                [SuccessfulSource()],
                FeedDiscoveryCompletionStatus.Complete));
        await Task.Delay(30);

        Assert.Equal("新结果", Assert.Single(viewModel.Candidates).Title);
        Assert.Equal("新关键词", viewModel.LastSubmittedInput);
    }

    [Fact]
    public async Task RateLimitedAndRoleLossRemainDistinctAndProtected()
    {
        var discovery = new FakeUnifiedDiscoveryService();
        discovery.Results.Enqueue(new(
            ValidQuery("限流"),
            [],
            [
                new(
                    "worker:known-catalog",
                    FeedDiscoverySourceKind.KnownCatalog,
                    FeedDiscoverySourceStatus.RateLimited,
                    0,
                    false,
                    false)
            ],
            FeedDiscoveryCompletionStatus.Unavailable));
        var account = new FakeAccountSession(AccountRole.Admin);
        using var viewModel = CreateViewModel(
            discovery,
            new FakePreviewRepository([]),
            account,
            TimeSpan.FromHours(1));
        viewModel.Input = "限流";

        await viewModel.SearchCommand.ExecuteAsync();

        Assert.Equal(FeedDiscoveryPageState.RateLimited, viewModel.State);
        Assert.True(viewModel.RetryCommand.CanExecute(null));

        account.SetRole(AccountRole.User);

        Assert.False(viewModel.IsAdmin);
        Assert.False(viewModel.SearchCommand.CanExecute(null));
        Assert.Empty(viewModel.Candidates);
        Assert.Equal(FeedDiscoveryPageState.Forbidden, viewModel.State);
    }

    [Fact]
    public async Task UnavailableSourcesMapToOfflineAndRemainRetryable()
    {
        var discovery = new FakeUnifiedDiscoveryService();
        discovery.Results.Enqueue(new(
            ValidQuery("offline"),
            [],
            [
                new(
                    "worker:known-catalog",
                    FeedDiscoverySourceKind.KnownCatalog,
                    FeedDiscoverySourceStatus.Unavailable,
                    0,
                    false,
                    false)
            ],
            FeedDiscoveryCompletionStatus.Unavailable));
        using var viewModel = CreateViewModel(
            discovery,
            new FakePreviewRepository([]),
            AccountRole.Admin,
            TimeSpan.FromHours(1));
        viewModel.Input = "offline";

        await viewModel.SearchCommand.ExecuteAsync();

        Assert.Equal(FeedDiscoveryPageState.Offline, viewModel.State);
        Assert.Contains("无法连接", viewModel.Status, StringComparison.Ordinal);
        Assert.Empty(viewModel.Candidates);
        Assert.True(viewModel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task LocalPreviewFailureDoesNotHideValidDiscoveryCandidates()
    {
        var discovery = new FakeUnifiedDiscoveryService();
        discovery.Results.Enqueue(new(
            ValidQuery("reader"),
            [Candidate("https://feeds.example/feed.xml")],
            [SuccessfulSource()],
            FeedDiscoveryCompletionStatus.Complete));
        var entries = new FakePreviewRepository([])
        {
            Failure = new InvalidOperationException("simulated local failure")
        };
        using var viewModel = CreateViewModel(
            discovery,
            entries,
            AccountRole.Admin,
            TimeSpan.FromHours(1));
        viewModel.Input = "reader";

        await viewModel.SearchCommand.ExecuteAsync();

        Assert.Equal(FeedDiscoveryPageState.Ready, viewModel.State);
        FeedDiscoveryCandidateViewModel candidate =
            Assert.Single(viewModel.Candidates);
        Assert.Empty(candidate.RecentEntries);
        Assert.Contains("尚无", candidate.PreviewStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialSourcesWithoutCandidatesDoNotMasqueradeAsCleanEmptyResult()
    {
        var discovery = new FakeUnifiedDiscoveryService();
        discovery.Results.Enqueue(new(
            ValidQuery("reader"),
            [],
            [
                new(
                    "worker:known-catalog",
                    FeedDiscoverySourceKind.KnownCatalog,
                    FeedDiscoverySourceStatus.NoResults,
                    0,
                    false,
                    false),
                new(
                    "direct-probe",
                    FeedDiscoverySourceKind.DirectProbe,
                    FeedDiscoverySourceStatus.TimedOut,
                    0,
                    false,
                    false)
            ],
            FeedDiscoveryCompletionStatus.Partial));
        using var viewModel = CreateViewModel(
            discovery,
            new FakePreviewRepository([]),
            AccountRole.Admin,
            TimeSpan.FromHours(1));
        viewModel.Input = "reader";

        await viewModel.SearchCommand.ExecuteAsync();

        Assert.Equal(FeedDiscoveryPageState.Partial, viewModel.State);
        Assert.Contains("部分来源", viewModel.Status, StringComparison.Ordinal);
        Assert.Empty(viewModel.Candidates);
    }

    [Fact]
    public async Task InvalidInputImmediatelyCancelsActiveSearchAndKeepsInvalidState()
    {
        var discovery = new ControlledUnifiedDiscoveryService();
        using var viewModel = CreateViewModel(
            discovery,
            new FakePreviewRepository([]),
            AccountRole.Admin,
            TimeSpan.Zero);
        viewModel.Input = "旧关键词";
        await WaitUntilAsync(
            () => discovery.Calls.Contains("旧关键词"),
            TimeSpan.FromSeconds(2));

        viewModel.Input = "javascript:alert(1)";

        Assert.True(discovery.WasCancelled("旧关键词"));
        Assert.False(viewModel.IsBusy);
        Assert.Equal(FeedDiscoveryPageState.InvalidInput, viewModel.State);
        Assert.Contains("无效", viewModel.Status, StringComparison.Ordinal);

        discovery.Complete(
            "旧关键词",
            new(
                ValidQuery("旧关键词"),
                [Candidate("https://old.example/feed.xml", "旧结果")],
                [SuccessfulSource()],
                FeedDiscoveryCompletionStatus.Complete));
        await Task.Delay(30);

        Assert.Equal(FeedDiscoveryPageState.InvalidInput, viewModel.State);
        Assert.Empty(viewModel.Candidates);
    }

    [Fact]
    public async Task ManualCancelReleasesCommandWhenProviderIgnoresCancellation()
    {
        var discovery = new ControlledUnifiedDiscoveryService();
        using var viewModel = CreateViewModel(
            discovery,
            new FakePreviewRepository([]),
            AccountRole.Admin,
            TimeSpan.FromHours(1));
        viewModel.Input = "不会主动结束的搜索";

        Task searchTask = viewModel.SearchCommand.ExecuteAsync();
        await WaitUntilAsync(
            () => discovery.Calls.Contains("不会主动结束的搜索"),
            TimeSpan.FromSeconds(2));

        // 底层服务故意不完成任务，取消后命令仍必须及时恢复可用。
        viewModel.CancelCommand.Execute(null);
        await searchTask.WaitAsync(TimeSpan.FromMilliseconds(300));

        Assert.False(viewModel.SearchCommand.IsRunning);
        Assert.True(viewModel.SearchCommand.CanExecute(null));
        Assert.Equal(FeedDiscoveryPageState.Cancelled, viewModel.State);
    }

    [Fact]
    public async Task CancelDuringDebouncePreventsProviderCall()
    {
        var discovery = new ControlledUnifiedDiscoveryService();
        using var viewModel = CreateViewModel(
            discovery,
            new FakePreviewRepository([]),
            AccountRole.Admin,
            TimeSpan.FromMinutes(1));
        viewModel.Input = "reader";

        viewModel.CancelCommand.Execute(null);
        await Task.Delay(30);

        Assert.Equal(FeedDiscoveryPageState.Cancelled, viewModel.State);
        Assert.Empty(discovery.Calls);
    }

    private static FeedDiscoveryViewModel CreateViewModel(
        IUnifiedFeedDiscoveryService discovery,
        IFeedDiscoveryPreviewRepository entries,
        AccountRole role,
        TimeSpan debounceDelay) =>
        CreateViewModel(
            discovery,
            entries,
            new FakeAccountSession(role),
            debounceDelay);

    private static FeedDiscoveryViewModel CreateViewModel(
        IUnifiedFeedDiscoveryService discovery,
        IFeedDiscoveryPreviewRepository entries,
        FakeAccountSession account,
        TimeSpan debounceDelay) =>
        new(
            discovery,
            new FakeCatalogRepository(),
            entries,
            account,
            debounceDelay);

    private static FeedDiscoveryQuery ValidQuery(string value) =>
        new(value, FeedDiscoveryQueryKind.Keyword, FeedDiscoveryQueryError.None);

    private static FeedDiscoveryCandidate Candidate(
        string url,
        string title = "示例订阅") =>
        new(
            url,
            title,
            "https://feeds.example/",
            FeedDocumentKind.Rss20,
            Now,
            FeedDiscoveryHealth.Healthy,
            [
                new(
                    "worker:known-catalog",
                    FeedDiscoverySourceKind.KnownCatalog,
                    FeedDiscoveryMatchKind.Keyword,
                    FeedDiscoveryConfidence.High)
            ],
            [new(FeedDiscoveryWarningCode.Stale, "worker:known-catalog")]);

    private static FeedDiscoverySourceReport SuccessfulSource() =>
        new(
            "worker:known-catalog",
            FeedDiscoverySourceKind.KnownCatalog,
            FeedDiscoverySourceStatus.Succeeded,
            1,
            false,
            false);

    private static FeedDiscoveryPreviewItem Preview(int index) =>
        new(
            "feed-1",
            $"第 {index} 条",
            Now.AddMinutes(-index));

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("等待发现页状态更新超时。");
            }
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// 顺序返回结果，便于验证状态映射与本地预览。
    /// </summary>
    private sealed class FakeUnifiedDiscoveryService
        : IUnifiedFeedDiscoveryService
    {
        public Queue<UnifiedFeedDiscoveryResult> Results { get; } = [];

        public Task<UnifiedFeedDiscoveryResult> DiscoverAsync(
            string input,
            CancellationToken cancellationToken) =>
            Task.FromResult(Results.Dequeue());
    }

    /// <summary>
    /// 故意忽略取消令牌，证明 ViewModel 仍能阻止旧响应覆盖新结果。
    /// </summary>
    private sealed class ControlledUnifiedDiscoveryService
        : IUnifiedFeedDiscoveryService
    {
        private readonly Dictionary<
            string,
            TaskCompletionSource<UnifiedFeedDiscoveryResult>> _pending = [];
        private readonly Dictionary<string, CancellationToken> _tokens = [];

        public List<string> Calls { get; } = [];

        public Task<UnifiedFeedDiscoveryResult> DiscoverAsync(
            string input,
            CancellationToken cancellationToken)
        {
            Calls.Add(input);
            _tokens[input] = cancellationToken;
            var completion =
                new TaskCompletionSource<UnifiedFeedDiscoveryResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[input] = completion;
            return completion.Task;
        }

        public void Complete(
            string input,
            UnifiedFeedDiscoveryResult result) =>
            _pending[input].SetResult(result);

        public bool WasCancelled(string input) =>
            _tokens[input].IsCancellationRequested;
    }

    /// <summary>
    /// 提供与候选规范化地址一致的本地目录，用于加载真实 SQLite 条目预览。
    /// </summary>
    private sealed class FakeCatalogRepository : IFeedCatalogRepository
    {
        private readonly FeedCatalogSnapshot _snapshot = new(
            new(7, FeedCatalogScope.Active, Now, Now),
            [],
            [
                new(
                    "feed-1",
                    "https://feeds.example/feed.xml",
                    "https://feeds.example/feed.xml",
                    "示例订阅",
                    "https://feeds.example/",
                    null,
                    FeedViewKind.Article,
                    60,
                    100,
                    true,
                    1,
                    Now,
                    Now)
            ]);

        public Task ReplaceAsync(
            FeedCatalogSnapshot snapshot,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedCatalogSnapshot?>(_snapshot);

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<FeedCatalogState> GetStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot.State);
    }

    /// <summary>
    /// 记录查询上限，确保候选卡不会偷偷读取超过四条的本地正文。
    /// </summary>
    private sealed class FakePreviewRepository(
        IReadOnlyList<FeedDiscoveryPreviewItem> entries)
        : IFeedDiscoveryPreviewRepository
    {
        public List<PreviewQuery> Queries { get; } = [];
        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<FeedDiscoveryPreviewItem>> GetRecentAsync(
            IReadOnlyCollection<string> feedIds,
            int maximumPerFeed,
            string localProfile,
            CancellationToken cancellationToken)
        {
            Queries.Add(new(
                feedIds.ToArray(),
                maximumPerFeed,
                localProfile));
            if (Failure is not null)
            {
                return Task.FromException<
                    IReadOnlyList<FeedDiscoveryPreviewItem>>(Failure);
            }
            return Task.FromResult<IReadOnlyList<FeedDiscoveryPreviewItem>>(
                entries.Take(maximumPerFeed).ToArray());
        }

        public sealed record PreviewQuery(
            IReadOnlyList<string> FeedIds,
            int MaximumPerFeed,
            string LocalProfile);
    }

    /// <summary>
    /// 模拟服务端会话角色变化，验证入口隐藏之外仍有 ViewModel 防线。
    /// </summary>
    private sealed class FakeAccountSession : IAccountSessionService
    {
        public FakeAccountSession(AccountRole role)
        {
            SetRole(role);
        }

        public bool IsConfigured => true;
        public AccountSessionSnapshot Current { get; private set; } =
            AccountSessionSnapshot.SignedOut;
        public event EventHandler<AccountSessionChangedEventArgs>?
            SessionChanged;

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task LoginAsync(
            string username,
            string password,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RefreshAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task LogoutAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void SetRole(AccountRole role)
        {
            Current = new(
                AccountSessionStatus.SignedIn,
                new("user-1", "owner", role));
            SessionChanged?.Invoke(this, new(Current));
        }
    }
}
