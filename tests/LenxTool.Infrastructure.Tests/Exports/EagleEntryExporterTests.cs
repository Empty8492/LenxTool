using System.Globalization;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Exports;

/// <summary>
/// 冻结 P2-12 Eagle 适配器的首个安全契约：只有显式策略、当前本机目标和
/// 完整验证的图片才能越过 API 边界，且队列重放必须复用稳定远端条目标识。
/// </summary>
public sealed class EagleEntryExporterTests
{
    private const long MaximumImageBytes = 12L * 1024 * 1024;
    private const string DefaultLibraryRevision =
        "111111111111111111111111";
    private static readonly Uri DefaultEndpoint =
        new("http://127.0.0.1:41595/");
    private static readonly string[] ExpectedMappedTags = ["AI", "设计"];
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task ExportAsyncDeniedPolicyDoesNotReadImageOrCallEagle()
    {
        // 管理员撤销策略后必须在任何图片读取或本机 API 探测前失败关闭。
        EagleExportTarget target = Target();
        var targetStore = new StubTargetStore(target);
        var policies = new StubPolicyService(enabled: false);
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient();
        var exporter = new EagleEntryExporter(
            targetStore,
            policies,
            downloader,
            api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(), target),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.AccessDenied, exception.Error.Code);
        Assert.Empty(downloader.Requests);
        Assert.Equal(0, api.ProbeCount);
        Assert.Equal(0, api.AddCount);
        Assert.Equal(
            [EntryIntegrationPolicyScope.Active],
            policies.RequestedScopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportAsyncMapsTransientPolicyTransportFailureWithoutLeakingDetails(
        bool isInternalTimeout)
    {
        const string sensitiveDetail = "private-policy-response";
        EagleExportTarget target = Target();
        var policies = new StubPolicyService(enabled: true)
        {
            Failure = isInternalTimeout
                ? new OperationCanceledException(sensitiveDetail)
                : new HttpRequestException(sensitiveDetail)
        };
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient();
        var exporter = new EagleEntryExporter(
            new StubTargetStore(target),
            policies,
            downloader,
            api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(), target),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.DestinationUnavailable,
            exception.Error.Code);
        Assert.True(exception.Error.IsRetryable);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            sensitiveDetail,
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.Empty(downloader.Requests);
        Assert.Equal(0, api.ProbeCount);
    }

    [Fact]
    public async Task ExportAsyncPreservesExplicitCallerCancellation()
    {
        EagleExportTarget target = Target();
        var exporter = Exporter(
            target,
            SuccessfulDownloader(),
            new StubEagleApiClient());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => exporter.ExportAsync(
                Request(Entry(), target),
                cancellation.Token));
    }

    [Fact]
    public async Task ExportAsyncSelectsFirstAllowedAndVerifiedPictureEnclosure()
    {
        // 受阻、缺扩展名和 MIME/扩展冲突的候选都不能抢在首个完整验证图片之前。
        EagleExportTarget target = Target();
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient();
        var exporter = Exporter(target, downloader, api);
        FeedEntry entry = Entry(
            enclosures:
            [
                new(
                    "http://127.0.0.1/private.png",
                    "image/png",
                    PngBytes.Length,
                    "blocked"),
                new(
                    "https://cdn.example.com/oversized.png",
                    "image/png",
                    MaximumImageBytes + 1,
                    "oversized"),
                new(
                    "https://cdn.example.com/no-extension",
                    "image/png",
                    PngBytes.Length,
                    "unverified"),
                new(
                    "https://cdn.example.com/conflict.mp3",
                    "image/png",
                    PngBytes.Length,
                    "conflicting"),
                new(
                    "https://cdn.example.com/accepted.png",
                    "image/png",
                    PngBytes.Length,
                    "accepted"),
                new(
                    "https://cdn.example.com/later.jpg",
                    "image/jpeg",
                    PngBytes.Length,
                    "later")
            ]);

        EntryExportResult result = await exporter.ExportAsync(
            Request(entry, target),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        DownloadRequest download = Assert.Single(downloader.Requests);
        Assert.Equal(
            "https://cdn.example.com/accepted.png",
            download.ImageUrl);
        Assert.Equal(entry.NormalizedUrl, download.Referrer);
        Assert.Equal(1, download.MaximumResources);
        Assert.Equal(MaximumImageBytes, download.MaximumNetworkBytes);
        Assert.Equal(DefaultEndpoint, api.LastProbeEndpoint);
        Assert.Equal(1, api.AddCount);
    }

    [Fact]
    public async Task ExportAsyncUnsupportedAttachmentNeverTouchesEagle()
    {
        EagleExportTarget target = Target();
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient();
        var exporter = Exporter(target, downloader, api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(enclosures: []), target),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.UnsupportedContent,
            exception.Error.Code);
        Assert.Empty(downloader.Requests);
        Assert.Equal(0, api.ProbeCount);
        Assert.Equal(0, api.ExistsCount);
        Assert.Equal(0, api.AddCount);
    }

    [Fact]
    public async Task ExportAsyncRejectsSpoofedImageMagicBeforeCallingEagle()
    {
        // 即使下载器声称 image/png，HTML 正文也不能被写入 Eagle；
        // 为收敛崩溃重放，下载前允许一次只读稳定 ID 查询。
        EagleExportTarget target = Target();
        var downloader = new StubImageStreamDownloader(
            _ => Content(
                Encoding.UTF8.GetBytes(
                    "<html>provider login response</html>"),
                "image/png"));
        var api = new StubEagleApiClient();
        var exporter = Exporter(target, downloader, api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(), target),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.UnsupportedContent,
            exception.Error.Code);
        Assert.Single(downloader.Requests);
        Assert.Equal(1, api.ProbeCount);
        Assert.Equal(1, api.ExistsCount);
        Assert.Equal(0, api.AddCount);
    }

    [Fact]
    public async Task ExportAsyncRejectsDownloadedImageBeyondTwelveMiBBeforeCallingEagle()
    {
        // 声明长度较小也不能绕过实际流量上限，边界检查必须以读取到的字节为准；
        // 超限正文不得越过只读预检进入写入 API。
        EagleExportTarget target = Target();
        byte[] oversized = new byte[MaximumImageBytes + 1];
        PngBytes.CopyTo(oversized, 0);
        var downloader = new StubImageStreamDownloader(
            _ => Content(oversized, "image/png"));
        var api = new StubEagleApiClient();
        var exporter = Exporter(target, downloader, api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(declaredLength: 1024), target),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.ContentTooLarge,
            exception.Error.Code);
        Assert.Single(downloader.Requests);
        Assert.Equal(1, api.ProbeCount);
        Assert.Equal(1, api.ExistsCount);
        Assert.Equal(0, api.AddCount);
    }

    [Fact]
    public async Task ExportAsyncMapsCapabilityTitleSourceAndCategories()
    {
        // 能力和 DTO 映射共同冻结 Eagle 只能处理图片且不需要读取个人凭据。
        EagleExportTarget target = Target();
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient();
        var exporter = Exporter(target, downloader, api);
        FeedEntry entry = Entry(
            title: "Eagle 图片标题",
            normalizedUrl: "https://example.com/articles/eagle-42",
            categories: ["AI", "设计"]);

        EntryExportResult result = await exporter.ExportAsync(
            Request(entry, target),
            CancellationToken.None);

        Assert.Equal("eagle", exporter.Capability.ExporterId);
        Assert.Equal(
            [EntryViewKind.Picture],
            exporter.Capability.SupportedViewKinds);
        Assert.False(exporter.Capability.RequiresCredentials);
        Assert.Equal(
            MaximumImageBytes,
            exporter.Capability.MaximumContentBytes);
        Assert.True(exporter.Capability.IsIdempotent);
        Assert.Matches(
            "^default\\.[0-9a-f]{24}\\.[0-9a-f]{24}$",
            target.CreateQueueTargetId(DefaultLibraryRevision));
        CapturedEagleItem added = Assert.Single(api.Items);
        Assert.Equal(entry.Title, added.Name);
        Assert.Equal(entry.NormalizedUrl, added.Website);
        Assert.Equal(ExpectedMappedTags, added.Tags);
        Assert.StartsWith(
            "data:image/png;base64,",
            added.DataUri,
            StringComparison.Ordinal);
        Assert.Equal(PngBytes, DecodeDataUri(added.DataUri));
        Assert.Equal(added.ItemId, result.RemoteId);
    }

    [Fact]
    public async Task ExportAsyncReusesStableItemIdForRepeatedRequest()
    {
        // 至少一次队列可能重放同一请求，适配器必须把同一幂等键映射为同一 Eagle 条目 ID。
        EagleExportTarget target = Target();
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient();
        var exporter = Exporter(target, downloader, api);
        EntryExportRequest request = Request(Entry(), target);

        EntryExportResult first = await exporter.ExportAsync(
            request,
            CancellationToken.None);
        EntryExportResult second = await exporter.ExportAsync(
            request,
            CancellationToken.None);

        Assert.Equal(2, api.Items.Count);
        Assert.All(api.Items, item => Assert.Matches(
            "^LT[0-9A-F]{30}$",
            item.ItemId));
        Assert.Equal(api.Items[0].ItemId, api.Items[1].ItemId);
        Assert.Equal(first.RemoteId, second.RemoteId);
        Assert.Equal(api.Items[0].ItemId, first.RemoteId);
    }

    [Fact]
    public async Task ExportAsyncExistingStableItemSkipsImageReplayDownload()
    {
        // 首次写入后若队列尚未来得及落终态，重放必须先按稳定 ID 收敛，
        // 不能因原图随后失效而把已经存在的 Eagle 条目标成失败。
        EagleExportTarget target = Target();
        var downloader = new StubImageStreamDownloader(
            _ => throw new HttpRequestException("source image expired"));
        var api = new StubEagleApiClient
        {
            ExistingItem = true
        };
        var exporter = Exporter(target, downloader, api);

        EntryExportResult result = await exporter.ExportAsync(
            Request(Entry(), target),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Matches("^LT[0-9A-F]{30}$", result.RemoteId);
        Assert.Empty(downloader.Requests);
        Assert.Equal(2, api.ProbeCount);
        Assert.Equal(1, api.ExistsCount);
        Assert.Equal(result.RemoteId, api.LastExistsItemId);
        Assert.Equal(0, api.AddCount);
    }

    [Fact]
    public async Task ExportAsyncRechecksLibraryScopeBeforeAcceptingExistingItem()
    {
        // 查询稳定 ID 时若用户切库，不能把另一资源库中的同 ID 误当成本任务完成。
        EagleExportTarget target = Target();
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient
        {
            ExistingItem = true,
            LibraryRevisions = new(
            [
                DefaultLibraryRevision,
                "222222222222222222222222"
            ])
        };
        var exporter = Exporter(target, downloader, api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(), target),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.Conflict, exception.Error.Code);
        Assert.Empty(downloader.Requests);
        Assert.Equal(2, api.ProbeCount);
        Assert.Equal(1, api.ExistsCount);
        Assert.Equal(0, api.AddCount);
    }

    [Fact]
    public async Task ExportAsyncRejectsStaleQueueScopeAfterTargetEndpointChanges()
    {
        // 旧任务不能在目标端口改变后静默投递到新的本机 Eagle 实例。
        EagleExportTarget queuedTarget = Target();
        EagleExportTarget currentTarget = new(
            "default",
            new Uri("http://127.0.0.1:41596/"));
        var targetStore = new StubTargetStore(currentTarget);
        var policies = new StubPolicyService(enabled: true);
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient();
        var exporter = new EagleEntryExporter(
            targetStore,
            policies,
            downloader,
            api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(), queuedTarget),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.Conflict, exception.Error.Code);
        Assert.Empty(downloader.Requests);
        Assert.Equal(0, api.ProbeCount);
        Assert.Equal(0, api.AddCount);
    }

    [Fact]
    public async Task ExportAsyncHoldsEndpointGenerationUntilExportFinishes()
    {
        // 同进程保存新端点必须等待旧代际导出完成；否则旧任务会在配置已经
        // 对用户显示为新端点后继续向旧端点写入，且事后重读无法撤销副作用。
        EagleExportTarget currentTarget = Target();
        EagleExportTarget nextTarget = new(
            "default",
            new Uri("http://127.0.0.1:41596/"));
        var targetStore = new StubTargetStore(currentTarget);
        var api = new StubEagleApiClient();
        TaskCompletionSource releaseAdd = api.BlockNextAdd();
        var exporter = new EagleEntryExporter(
            targetStore,
            new StubPolicyService(enabled: true),
            SuccessfulDownloader(),
            api);

        Task<EntryExportResult> exporting = exporter.ExportAsync(
            Request(Entry(), currentTarget),
            CancellationToken.None);
        await api.AddStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task saving = targetStore.SaveAsync(
            nextTarget,
            CancellationToken.None);
        Assert.False(saving.IsCompleted);

        releaseAdd.TrySetResult();
        EntryExportResult result = await exporting;
        await saving;

        Assert.True(result.Succeeded);
        Assert.Equal(nextTarget, targetStore.Current);
        CapturedEagleItem item = Assert.Single(api.Items);
        Assert.Equal(currentTarget.Endpoint, item.Endpoint);
    }

    [Fact]
    public async Task ExportAsyncRejectsStaleQueueScopeAfterLibraryChanges()
    {
        // 同一端口切换资源库后，旧任务必须在图片下载和任何条目读写前冲突关闭。
        EagleExportTarget target = Target();
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient
        {
            LibraryRevision = "222222222222222222222222"
        };
        var exporter = Exporter(target, downloader, api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(
                        Entry(),
                        target,
                        DefaultLibraryRevision),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.Conflict, exception.Error.Code);
        Assert.Empty(downloader.Requests);
        Assert.Equal(1, api.ProbeCount);
        Assert.Equal(0, api.ExistsCount);
        Assert.Equal(0, api.AddCount);
    }

    [Fact]
    public async Task ExportAsyncRechecksLibraryScopeAfterImageDownload()
    {
        // Eagle 无法把 item/add 原子绑定到资源库；图片下载后必须再探测一次，
        // 把用户在下载期间切库的窗口收敛到实际写入之前。
        EagleExportTarget target = Target();
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient
        {
            LibraryRevisions = new(
            [
                DefaultLibraryRevision,
                "222222222222222222222222"
            ])
        };
        var exporter = Exporter(target, downloader, api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(), target),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.Conflict, exception.Error.Code);
        Assert.Single(downloader.Requests);
        Assert.Equal(2, api.ProbeCount);
        Assert.Equal(1, api.ExistsCount);
        Assert.Equal(0, api.AddCount);
    }

    [Fact]
    public async Task ExportAsyncRejectsSuccessWhenLibraryChangesDuringItemAdd()
    {
        // item/add 没有资源库身份参数；即使写前探测通过，也必须用写后探测
        // 识别在请求窗口内持续发生的外部切库，不能把未知落库结果报告为成功。
        EagleExportTarget target = Target();
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient
        {
            LibraryRevisions = new(
            [
                DefaultLibraryRevision,
                DefaultLibraryRevision,
                "222222222222222222222222"
            ])
        };
        var exporter = Exporter(target, downloader, api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(), target),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.Conflict, exception.Error.Code);
        Assert.Single(downloader.Requests);
        Assert.Equal(3, api.ProbeCount);
        Assert.Equal(1, api.ExistsCount);
        Assert.Equal(1, api.AddCount);
    }

    [Fact]
    public async Task ExportAsyncMapsProviderFailureWithoutLeakingResponseBody()
    {
        // 本机 API 的响应正文可能包含用户数据，公共异常和内部异常链都不得回显它。
        const string sensitiveBody =
            "provider response body: secret-local-library-name";
        EagleExportTarget target = Target();
        var downloader = SuccessfulDownloader();
        var api = new StubEagleApiClient
        {
            AddFailure = new EagleApiException(
                EagleApiFailure.Unavailable,
                isRetryable: true,
                new HttpRequestException(sensitiveBody))
        };
        var exporter = Exporter(target, downloader, api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(
                () => exporter.ExportAsync(
                    Request(Entry(), target),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.DestinationUnavailable,
            exception.Error.Code);
        Assert.True(exception.Error.IsRetryable);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            sensitiveBody,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    private static EagleEntryExporter Exporter(
        EagleExportTarget target,
        IArticleImageStreamDownloader downloader,
        IEagleApiClient api) =>
        new(
            new StubTargetStore(target),
            new StubPolicyService(enabled: true),
            downloader,
            api);

    private static StubImageStreamDownloader SuccessfulDownloader() =>
        new(_ => Content(PngBytes, "image/png"));

    private static ArticleImageStreamContent Content(
        byte[] bytes,
        string mimeType) =>
        new(
            new MemoryStream(bytes, writable: false),
            mimeType,
            fromCache: true);

    private static byte[] DecodeDataUri(string value)
    {
        int separator = value.IndexOf(',');
        Assert.True(separator > 0, "Eagle 图片必须使用合法 data URI。");
        return Convert.FromBase64String(value[(separator + 1)..]);
    }

    private static EagleExportTarget Target() =>
        new("default", DefaultEndpoint);

    private static EntryExportRequest Request(
        FeedEntry entry,
        EagleExportTarget target,
        string libraryRevision = DefaultLibraryRevision) =>
        EntryExportRequest.Create(
            EagleEntryExporter.ExporterId,
            target.CreateQueueTargetId(libraryRevision),
            entry,
            EntryViewKind.Picture,
            entry.Enclosures.Count > 0
                ? entry.Enclosures[0].Length ?? 0
                : 0);

    private static FeedEntry Entry(
        string title = "Export picture",
        string? normalizedUrl = "https://example.com/articles/42",
        IReadOnlyList<string>? categories = null,
        IReadOnlyList<FeedEnclosure>? enclosures = null,
        long? declaredLength = null) =>
        new(
            "entry-eagle-42",
            "30000000-0000-4000-8000-000000000001",
            "external-eagle-42",
            normalizedUrl,
            title,
            "作者",
            DateTimeOffset.Parse(
                "2026-08-03T12:30:00+00:00",
                CultureInfo.InvariantCulture),
            null,
            "摘要",
            "<p>正文</p>",
            categories ?? ["默认分类"],
            enclosures
                ??
                [
                    new(
                        "https://cdn.example.com/eagle.png",
                        "image/png",
                        declaredLength ?? PngBytes.Length,
                        "cover")
                ],
            new string('a', 64),
            DateTimeOffset.Parse(
                "2026-08-03T12:35:00+00:00",
                CultureInfo.InvariantCulture));

    private sealed class StubTargetStore(EagleExportTarget? current)
        : IEagleExportTargetStore, IDisposable
    {
        private readonly SemaphoreSlim _generationGate = new(1, 1);

        public EagleExportTarget? Current { get; set; } = current;

        public int GetCount { get; private set; }

        public Task<EagleExportTarget?> GetAsync(
            CancellationToken cancellationToken)
        {
            GetCount++;
            return Task.FromResult(Current);
        }

        public async Task<IEagleExportTargetLease> AcquireExportLeaseAsync(
            CancellationToken cancellationToken)
        {
            await _generationGate.WaitAsync(cancellationToken);
            return new StubTargetLease(
                Current,
                _generationGate);
        }

        public async Task SaveAsync(
            EagleExportTarget target,
            CancellationToken cancellationToken)
        {
            await _generationGate.WaitAsync(cancellationToken);
            try
            {
                Current = target;
            }
            finally
            {
                _generationGate.Release();
            }
        }

        public void Dispose() => _generationGate.Dispose();

        private sealed class StubTargetLease(
            EagleExportTarget? target,
            SemaphoreSlim gate)
            : IEagleExportTargetLease
        {
            private int _isDisposed;

            public EagleExportTarget? Target { get; } = target;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
                {
                    gate.Release();
                }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class StubPolicyService(bool enabled)
        : IEntryIntegrationPolicyService
    {
        public List<EntryIntegrationPolicyScope> RequestedScopes { get; } = [];
        public Exception? Failure { get; init; }

        public Task<EntryIntegrationPolicySnapshot> GetAsync(
            EntryIntegrationPolicyScope scope,
            CancellationToken cancellationToken)
        {
            RequestedScopes.Add(scope);
            if (Failure is not null)
            {
                return Task.FromException<
                    EntryIntegrationPolicySnapshot>(Failure);
            }
            IReadOnlyList<EntryIntegrationPolicy> policies = enabled
                ? [new(EntryIntegrationKind.Eagle, true, [])]
                : [];
            return Task.FromResult(
                new EntryIntegrationPolicySnapshot(
                    1,
                    policies,
                    scope));
        }

        public Task<EntryIntegrationPolicyMutationResult> ReplaceAsync(
            IReadOnlyList<EntryIntegrationPolicyInput> inputs,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubImageStreamDownloader(
        Func<DownloadRequest, ArticleImageStreamContent?> handler)
        : IArticleImageStreamDownloader
    {
        public List<DownloadRequest> Requests { get; } = [];

        public Task<ArticleImageStreamContent?> OpenAsync(
            string entryId,
            string imageUrl,
            string? referrer,
            ArticleImageDownloadBudget budget,
            CancellationToken cancellationToken)
        {
            var request = new DownloadRequest(
                entryId,
                imageUrl,
                referrer,
                budget.MaximumResources,
                budget.MaximumNetworkBytes);
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    private sealed class StubEagleApiClient : IEagleApiClient
    {
        private TaskCompletionSource? _releaseAdd;

        public int ProbeCount { get; private set; }

        public int ExistsCount { get; private set; }

        public int AddCount { get; private set; }

        public Uri? LastProbeEndpoint { get; private set; }

        public Exception? ProbeFailure { get; init; }

        public Exception? AddFailure { get; init; }

        public bool ExistingItem { get; init; }

        public string LibraryRevision { get; init; } =
            DefaultLibraryRevision;

        public Queue<string>? LibraryRevisions { get; init; }

        public string? LastExistsItemId { get; private set; }

        public TaskCompletionSource AddStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<CapturedEagleItem> Items { get; } = [];

        public TaskCompletionSource BlockNextAdd()
        {
            _releaseAdd = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _releaseAdd;
        }

        public Task<EagleApiCapability> ProbeAsync(
            Uri endpoint,
            CancellationToken cancellationToken)
        {
            ProbeCount++;
            LastProbeEndpoint = endpoint;
            string revision = LibraryRevisions is { Count: > 0 }
                ? LibraryRevisions.Dequeue()
                : LibraryRevision;
            return ProbeFailure is null
                ? Task.FromResult(new EagleApiCapability(
                    "4.0.0",
                    21,
                    revision))
                : Task.FromException<EagleApiCapability>(ProbeFailure);
        }

        public Task<bool> ExistsAsync(
            Uri endpoint,
            string itemId,
            CancellationToken cancellationToken)
        {
            ExistsCount++;
            LastExistsItemId = itemId;
            return Task.FromResult(ExistingItem);
        }

        public async Task<string> AddAsync(
            Uri endpoint,
            EagleAddItem item,
            CancellationToken cancellationToken)
        {
            AddCount++;
            AddStarted.TrySetResult();
            if (_releaseAdd is not null)
            {
                await _releaseAdd.Task.WaitAsync(cancellationToken);
                _releaseAdd = null;
            }
            if (AddFailure is not null)
            {
                throw AddFailure;
            }

            Items.Add(
                new CapturedEagleItem(
                    endpoint,
                    item.ItemId,
                    item.Name,
                    item.Website,
                    item.Tags.ToArray(),
                    item.DataUri));
            return item.ItemId;
        }
    }

    private sealed record DownloadRequest(
        string EntryId,
        string ImageUrl,
        string? Referrer,
        int MaximumResources,
        long MaximumNetworkBytes);

    private sealed record CapturedEagleItem(
        Uri Endpoint,
        string ItemId,
        string Name,
        string? Website,
        IReadOnlyList<string> Tags,
        string DataUri);
}
