using System.Collections.ObjectModel;
using System.Net;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Exports;

/// <summary>
/// 冻结 Zotero 个人库导出的幂等对象身份、RSS 字段映射和执行时安全门。
/// </summary>
public sealed class ZoteroEntryExporterTests
{
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47,
        0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D
    ];

    [Fact]
    public void CapabilitySupportsEveryFeedViewAndRequiresCredential()
    {
        ZoteroEntryExporter exporter = CreateExporter();

        Assert.Equal("zotero", exporter.Capability.ExporterId);
        Assert.Equal(
            Enum.GetValues<EntryViewKind>(),
            exporter.Capability.SupportedViewKinds.Order());
        Assert.True(exporter.Capability.RequiresCredentials);
        Assert.True(exporter.Capability.IsIdempotent);
        Assert.NotNull(exporter.Capability.MaximumContentBytes);
    }

    [Fact]
    public async Task WebpageMapsStableFieldsAndKeepsFlatAuthorAsSingleField()
    {
        ZoteroExportTarget target = Target();
        var api = new FakeZoteroApiClient();
        ZoteroEntryExporter exporter = CreateExporter(target: target, api: api);
        FeedEntry entry = Entry() with
        {
            Title = "  标题\r\n控制  ",
            Author = "  Research Team  ",
            PublishedAt = new DateTimeOffset(
                2026,
                8,
                3,
                12,
                30,
                0,
                TimeSpan.FromHours(8)),
            Categories = [" AI ", "ai", "研究\r\n", "  "]
        };
        EntryExportRequest request = Request(target, entry);

        EntryExportResult result = await exporter.ExportAsync(
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        ZoteroItem item = Assert.Single(api.CreatedItems);
        Assert.Equal("webpage", item.ItemType);
        Assert.Matches("^[23456789ABCDEFGHIJKLMNPQRSTUVWXYZ]{8}$", item.Key);
        Assert.Equal(item.Key, result.RemoteId);
        Assert.Equal("标题 控制", item.Title);
        Assert.Equal(entry.NormalizedUrl, item.Url);
        Assert.Equal("2026-08-03", item.Date);
        Assert.Equal("Research Team", Assert.Single(item.Creators).Name);
        Assert.Equal(["AI", "研究"], item.Tags);
        Assert.Null(item.ParentItem);
        Assert.Null(item.NoteHtml);
        Assert.StartsWith("lt:v1:parent:", item.LenxToolMarker);
        Assert.Equal("private-zotero-key", api.ApiKey);
        Assert.Equal(target.ApiRoot, api.Target?.Endpoint);
        Assert.False(api.Target?.RequireNotesPermission);
        Assert.False(api.Target?.RequireFilesPermission);
    }

    [Fact]
    public async Task JournalArticleCreatesHtmlEncodedSummaryChildAfterParent()
    {
        ZoteroExportTarget target = Target() with
        {
            ItemType = ZoteroItemType.JournalArticle,
            IncludeSummaryNote = true
        };
        var api = new FakeZoteroApiClient();
        ZoteroEntryExporter exporter = CreateExporter(target: target, api: api);
        FeedEntry entry = Entry() with
        {
            Summary = "<script>alert('x')</script> & 摘要"
        };

        await exporter.ExportAsync(
            Request(target, entry),
            CancellationToken.None);

        Assert.Equal(2, api.CreatedItems.Count);
        ZoteroItem parent = api.CreatedItems[0];
        ZoteroItem note = api.CreatedItems[1];
        Assert.Equal("journalArticle", parent.ItemType);
        Assert.Equal("note", note.ItemType);
        Assert.Equal(parent.Key, note.ParentItem);
        Assert.NotEqual(parent.Key, note.Key);
        Assert.Contains("&lt;script&gt;", note.NoteHtml, StringComparison.Ordinal);
        Assert.Contains("&amp; 摘要", note.NoteHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", note.NoteHtml, StringComparison.Ordinal);
        Assert.Empty(note.Creators);
        Assert.Empty(note.Tags);
        Assert.True(api.Target?.RequireNotesPermission);
    }

    [Fact]
    public async Task EnabledAttachmentCreatesImportedFileThenUploadsVerifiedBytes()
    {
        ZoteroExportTarget target = Target() with
        {
            IncludeSummaryNote = true,
            UploadFirstImageAttachment = true
        };
        FeedEntry entry = Entry() with
        {
            Enclosures =
            [
                new(
                    "https://cdn.example.com/cover.png",
                    "image/png",
                    PngBytes.Length,
                    "cover")
            ]
        };
        var images = new FakeImageStreamDownloader(
            PngBytes,
            "image/png");
        var api = new FakeZoteroApiClient();
        ZoteroEntryExporter exporter = CreateExporter(
            target: target,
            images: images,
            api: api);

        EntryExportResult result = await exporter.ExportAsync(
            Request(target, entry),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, api.CreatedItems.Count);
        ZoteroItem parent = api.CreatedItems[0];
        ZoteroItem note = api.CreatedItems[1];
        ZoteroItem attachment = api.CreatedItems[2];
        Assert.Equal("note", note.ItemType);
        Assert.Equal("attachment", attachment.ItemType);
        Assert.Equal(parent.Key, attachment.ParentItem);
        Assert.Matches(
            "^[23456789ABCDEFGHIJKLMNPQRSTUVWXYZ]{8}$",
            attachment.Key);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal($"LT{attachment.Key}.png", attachment.FileName);
        Assert.Equal(attachment.FileName, attachment.Title);
        ZoteroAttachmentUpload upload = Assert.IsType<
            ZoteroAttachmentUpload>(api.Upload);
        Assert.Equal(attachment.Key, upload.ItemKey);
        Assert.Equal(attachment.FileName, upload.FileName);
        Assert.Equal(attachment.ContentType, upload.ContentType);
        Assert.Equal(PngBytes, upload.Content.ToArray());
        Assert.Equal(
            entry.PublishedAt?.ToUnixTimeMilliseconds(),
            upload.ModifiedTimeMilliseconds);
        Assert.Equal(1, api.UploadCount);
        Assert.True(api.Target?.RequireFilesPermission);
        Assert.Single(images.Requests);
    }

    [Fact]
    public async Task AttachmentOptionWithoutVerifiedImageStillExportsParentOnly()
    {
        ZoteroExportTarget target = Target() with
        {
            UploadFirstImageAttachment = true
        };
        FeedEntry entry = Entry() with
        {
            Enclosures =
            [
                new(
                    "https://cdn.example.com/cover.avif",
                    "image/avif",
                    1024,
                    "unsupported")
            ]
        };
        var images = new FakeImageStreamDownloader(
            PngBytes,
            "image/png");
        var api = new FakeZoteroApiClient();

        await CreateExporter(
                target: target,
                images: images,
                api: api)
            .ExportAsync(Request(target, entry), CancellationToken.None);

        Assert.Single(api.CreatedItems);
        Assert.Equal(0, api.UploadCount);
        Assert.Empty(images.Requests);
    }

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", false)]
    public async Task AttachmentMimeAndMagicMustBothMatch(
        string responseMime,
        bool usePngBytes)
    {
        ZoteroExportTarget target = Target() with
        {
            UploadFirstImageAttachment = true
        };
        FeedEntry entry = Entry() with
        {
            Enclosures =
            [
                new(
                    "https://cdn.example.com/cover.png",
                    "image/png",
                    1024,
                    "cover")
            ]
        };
        var api = new FakeZoteroApiClient();
        byte[] bytes = usePngBytes
            ? PngBytes
            : "<html>not an image</html>"u8.ToArray();

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                CreateExporter(
                        target: target,
                        images: new FakeImageStreamDownloader(
                            bytes,
                            responseMime),
                        api: api)
                    .ExportAsync(
                        Request(target, entry),
                        CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.UnsupportedContent,
            exception.Error.Code);
        Assert.Equal(0, api.CreateCount);
        Assert.Equal(0, api.UploadCount);
    }

    [Fact]
    public async Task AttachmentActualSizeLimitStopsBeforeRemoteObjects()
    {
        ZoteroExportTarget target = Target() with
        {
            UploadFirstImageAttachment = true
        };
        FeedEntry entry = Entry() with
        {
            Enclosures =
            [
                new(
                    "https://cdn.example.com/cover.png",
                    "image/png",
                    null,
                    "cover")
            ]
        };
        byte[] oversized = new byte[12 * 1024 * 1024 + 1];
        PngBytes.CopyTo(oversized, 0);
        var api = new FakeZoteroApiClient();

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                CreateExporter(
                        target: target,
                        images: new FakeImageStreamDownloader(
                            oversized,
                            "image/png"),
                        api: api)
                    .ExportAsync(
                        Request(target, entry),
                        CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.ContentTooLarge,
            exception.Error.Code);
        Assert.Equal(0, api.CreateCount);
    }

    [Fact]
    public async Task AttachmentUploadFailureUsesSameClosedApiMapping()
    {
        ZoteroExportTarget target = Target() with
        {
            UploadFirstImageAttachment = true
        };
        FeedEntry entry = Entry() with
        {
            Enclosures =
            [
                new(
                    "https://cdn.example.com/cover.png",
                    "image/png",
                    PngBytes.Length,
                    "cover")
            ]
        };
        var api = new FakeZoteroApiClient
        {
            UploadException = new(
                ZoteroApiFailure.RateLimited,
                isRetryable: true,
                retryAfter: TimeSpan.FromSeconds(19))
        };

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                CreateExporter(
                        target: target,
                        images: new FakeImageStreamDownloader(
                            PngBytes,
                            "image/png"),
                        api: api)
                    .ExportAsync(
                        Request(target, entry),
                        CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.RateLimited, exception.Error.Code);
        Assert.True(exception.Error.IsRetryable);
        Assert.Equal(TimeSpan.FromSeconds(19), exception.Error.RetryAfter);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task DeterministicKeysRepeatButRolesUseDifferentSalts()
    {
        ZoteroExportTarget target = Target() with
        {
            IncludeSummaryNote = true
        };
        var firstApi = new FakeZoteroApiClient();
        var secondApi = new FakeZoteroApiClient();
        EntryExportRequest request = Request(target, Entry());

        await CreateExporter(target: target, api: firstApi)
            .ExportAsync(request, CancellationToken.None);
        await CreateExporter(target: target, api: secondApi)
            .ExportAsync(request, CancellationToken.None);

        Assert.Equal(
            firstApi.CreatedItems.Select(item => item.Key),
            secondApi.CreatedItems.Select(item => item.Key));
        Assert.Equal(2, firstApi.CreatedItems.Select(item => item.Key).Distinct().Count());
        Assert.Equal(
            firstApi.CreatedItems.Select(item => item.LenxToolMarker),
            secondApi.CreatedItems.Select(item => item.LenxToolMarker));
    }

    [Theory]
    [InlineData(false, "summary")]
    [InlineData(true, "  \r\n  ")]
    public async Task SummaryChildIsOmittedWhenDisabledOrBlank(
        bool includeSummary,
        string summary)
    {
        ZoteroExportTarget target = Target() with
        {
            IncludeSummaryNote = includeSummary
        };
        var api = new FakeZoteroApiClient();

        await CreateExporter(target: target, api: api).ExportAsync(
            Request(target, Entry() with { Summary = summary }),
            CancellationToken.None);

        Assert.Single(api.CreatedItems);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledOrHostlessPolicyStopsBeforeTargetAndCredential(
        bool isEnabled,
        bool includesOfficialHost)
    {
        var targets = new FakeTargetStore(Target());
        var credentials = new FakeCredentialStore();
        var policies = new FakePolicyService(
            isEnabled,
            includesOfficialHost);
        ZoteroEntryExporter exporter = CreateExporter(
            targets: targets,
            policies: policies,
            credentials: credentials);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(Target(), Entry()),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.AccessDenied, exception.Error.Code);
        Assert.Equal(0, targets.AcquireCount);
        Assert.Equal(0, credentials.GetCount);
    }

    [Fact]
    public async Task ChangedTargetGenerationReturnsConflictWithoutCredentialOrApi()
    {
        ZoteroExportTarget queued = Target();
        ZoteroExportTarget current = queued with
        {
            ItemType = ZoteroItemType.JournalArticle
        };
        var credentials = new FakeCredentialStore();
        var api = new FakeZoteroApiClient();
        ZoteroEntryExporter exporter = CreateExporter(
            target: current,
            credentials: credentials,
            api: api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(queued, Entry()),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.Conflict, exception.Error.Code);
        Assert.Equal(0, credentials.GetCount);
        Assert.Equal(0, api.ProbeCount);
    }

    [Fact]
    public async Task MissingCredentialReturnsStructuredFailureBeforeNetwork()
    {
        ZoteroExportTarget target = Target();
        var api = new FakeZoteroApiClient();
        ZoteroEntryExporter exporter = CreateExporter(
            target: target,
            credentials: new FakeCredentialStore(value: null),
            api: api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(target, Entry()),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.CredentialsRequired,
            exception.Error.Code);
        Assert.Equal(0, api.ProbeCount);
    }

    [Theory]
    [InlineData(99999999, true, true, true)]
    [InlineData(12345678, false, true, true)]
    [InlineData(12345678, true, false, true)]
    [InlineData(12345678, true, true, false)]
    public async Task ProbeMustMatchUserAndRequestedPermissions(
        long userId,
        bool canWrite,
        bool canWriteNotes,
        bool canWriteFiles)
    {
        ZoteroExportTarget target = Target() with
        {
            IncludeSummaryNote = true,
            UploadFirstImageAttachment = true
        };
        var api = new FakeZoteroApiClient
        {
            Capability = new(
                userId,
                canWrite,
                canWriteNotes,
                canWriteFiles)
        };
        ZoteroEntryExporter exporter = CreateExporter(target: target, api: api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(target, Entry()),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.AccessDenied, exception.Error.Code);
        Assert.Equal(0, api.CreateCount);
    }

    public static TheoryData<ZoteroApiFailure, EntryExportErrorCode, bool>
        ApiFailures => new()
        {
            { ZoteroApiFailure.Unauthorized, EntryExportErrorCode.AccessDenied, false },
            { ZoteroApiFailure.BlockedEndpoint, EntryExportErrorCode.AccessDenied, false },
            { ZoteroApiFailure.Conflict, EntryExportErrorCode.Conflict, true },
            { ZoteroApiFailure.RequestTooLarge, EntryExportErrorCode.ContentTooLarge, false },
            { ZoteroApiFailure.RateLimited, EntryExportErrorCode.RateLimited, true },
            { ZoteroApiFailure.Unavailable, EntryExportErrorCode.DestinationUnavailable, true },
            { ZoteroApiFailure.Rejected, EntryExportErrorCode.ProviderRejected, false },
            { ZoteroApiFailure.Collision, EntryExportErrorCode.Conflict, false }
        };

    [Theory]
    [MemberData(nameof(ApiFailures))]
    public async Task ApiFailuresMapWithoutLeakingProviderException(
        ZoteroApiFailure failure,
        EntryExportErrorCode expectedCode,
        bool expectedRetryable)
    {
        ZoteroExportTarget target = Target();
        var api = new FakeZoteroApiClient
        {
            CreateException = new(
                failure,
                expectedRetryable,
                retryAfter: TimeSpan.FromSeconds(37))
        };
        ZoteroEntryExporter exporter = CreateExporter(target: target, api: api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(target, Entry()),
                    CancellationToken.None));

        Assert.Equal(expectedCode, exception.Error.Code);
        Assert.Equal(expectedRetryable, exception.Error.IsRetryable);
        Assert.Equal(
            failure == ZoteroApiFailure.RateLimited
                ? TimeSpan.FromSeconds(37)
                : null,
            exception.Error.RetryAfter);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task CancellationPropagatesAndLeaseCoversLastApiCall()
    {
        ZoteroExportTarget target = Target();
        var targets = new FakeTargetStore(target);
        var api = new FakeZoteroApiClient
        {
            OnCreate = () => Assert.False(targets.LastLease?.IsDisposed)
        };
        ZoteroEntryExporter exporter = CreateExporter(
            targets: targets,
            api: api);

        await exporter.ExportAsync(
            Request(target, Entry()),
            CancellationToken.None);

        Assert.True(targets.LastLease?.IsDisposed);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportAsync(
                Request(target, Entry()),
                cancellation.Token));
    }

    private static ZoteroEntryExporter CreateExporter(
        ZoteroExportTarget? target = null,
        FakeTargetStore? targets = null,
        FakePolicyService? policies = null,
        FakeCredentialStore? credentials = null,
        IArticleImageStreamDownloader? images = null,
        FakeZoteroApiClient? api = null) => new(
            targets ?? new FakeTargetStore(target ?? Target()),
            policies ?? new FakePolicyService(),
            credentials ?? new FakeCredentialStore(),
            images ?? new FakeImageStreamDownloader(null, null),
            api ?? new FakeZoteroApiClient());

    private static ZoteroExportTarget Target() => new(
        ZoteroExportTarget.DefaultTargetId,
        12345678,
        ZoteroItemType.Webpage,
        IncludeSummaryNote: false,
        UploadFirstImageAttachment: false);

    private static EntryExportRequest Request(
        ZoteroExportTarget target,
        FeedEntry entry) => EntryExportRequest.Create(
            "zotero",
            target.CreateQueueTargetId(),
            entry,
            EntryViewKind.Article,
            1024);

    private static FeedEntry Entry() => new(
        "entry-1",
        "feed-1",
        "external-1",
        "https://example.com/articles/1",
        "测试标题",
        "作者",
        new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero),
        null,
        "测试摘要",
        "<p>正文不会进入 Zotero 笔记</p>",
        ["RSS", "测试"],
        [],
        new string('a', 64),
        new DateTimeOffset(2026, 8, 3, 1, 0, 0, TimeSpan.Zero));

    private sealed class FakePolicyService(
        bool isEnabled = true,
        bool includesOfficialHost = true)
        : IEntryIntegrationPolicyService
    {
        public Task<EntryIntegrationPolicySnapshot> GetAsync(
            EntryIntegrationPolicyScope scope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<EntryIntegrationPolicy> policies =
            [
                new(
                    EntryIntegrationKind.Zotero,
                    isEnabled,
                    includesOfficialHost ? ["api.zotero.org"] : [])
            ];
            return Task.FromResult(new EntryIntegrationPolicySnapshot(
                1,
                policies,
                scope,
                DateTimeOffset.UtcNow));
        }

        public Task<EntryIntegrationPolicyMutationResult> ReplaceAsync(
            IReadOnlyList<EntryIntegrationPolicyInput> inputs,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTargetStore(ZoteroExportTarget? target)
        : IZoteroExportTargetStore
    {
        public int AcquireCount { get; private set; }
        public FakeLease? LastLease { get; private set; }

        public Task<ZoteroExportTarget?> GetAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(target);

        public Task<IZoteroExportTargetLease> AcquireExportLeaseAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            LastLease = new(target);
            return Task.FromResult<IZoteroExportTargetLease>(LastLease);
        }

        public Task SaveAsync(
            ZoteroExportTarget value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLease(ZoteroExportTarget? target)
        : IZoteroExportTargetLease
    {
        public ZoteroExportTarget? Target { get; } = target;
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCredentialStore(
        string? value = "private-zotero-key")
        : IEntryIntegrationCredentialStore
    {
        public int GetCount { get; private set; }

        public Task<string?> GetAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCount++;
            return Task.FromResult(value);
        }

        public Task<bool> ExistsAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken) =>
            Task.FromResult(value is not null);

        public Task SetAsync(
            EntryIntegrationKind kind,
            string targetId,
            string credential,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeImageStreamDownloader(
        byte[]? content,
        string? mimeType)
        : IArticleImageStreamDownloader
    {
        public List<(string EntryId, string ImageUrl, string? Referrer)>
            Requests
        { get; } = [];

        public Task<ArticleImageStreamContent?> OpenAsync(
            string entryId,
            string imageUrl,
            string? referrer,
            ArticleImageDownloadBudget budget,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((entryId, imageUrl, referrer));
            ArticleImageStreamContent? result = content is null
                || mimeType is null
                ? null
                : new(
                    new MemoryStream(content, writable: false),
                    mimeType,
                    fromCache: true);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeZoteroApiClient : IZoteroApiClient
    {
        public ZoteroApiCapability Capability { get; init; } =
            new(12345678, true, true, true);
        public ZoteroApiException? CreateException { get; init; }
        public ZoteroApiException? UploadException { get; init; }
        public Action? OnCreate { get; init; }
        public ZoteroApiTarget? Target { get; private set; }
        public string? ApiKey { get; private set; }
        public int ProbeCount { get; private set; }
        public int CreateCount { get; private set; }
        public int UploadCount { get; private set; }
        public ZoteroAttachmentUpload? Upload { get; private set; }
        public ReadOnlyCollection<ZoteroItem> CreatedItems
        {
            get;
            private set;
        } = Array.AsReadOnly(Array.Empty<ZoteroItem>());

        public Task<ZoteroApiCapability> ProbeAsync(
            ZoteroApiTarget target,
            string apiKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Target = target;
            ApiKey = apiKey;
            ProbeCount++;
            return Task.FromResult(Capability);
        }

        public Task<ZoteroApiCapability> ProbePinnedAsync(
            ZoteroApiTarget target,
            string apiKey,
            IReadOnlyList<IPAddress> pinnedAddresses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> CreateAsync(
            ZoteroApiTarget target,
            string apiKey,
            IReadOnlyList<ZoteroItem> items,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Target = target;
            ApiKey = apiKey;
            CreatedItems = Array.AsReadOnly(items.ToArray());
            CreateCount++;
            OnCreate?.Invoke();
            if (CreateException is not null)
            {
                throw CreateException;
            }
            return Task.FromResult<IReadOnlyList<string>>(
                Array.AsReadOnly(items.Select(item => item.Key).ToArray()));
        }

        public Task UploadAttachmentAsync(
            ZoteroApiTarget target,
            string apiKey,
            ZoteroAttachmentUpload upload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Target = target;
            ApiKey = apiKey;
            Upload = upload;
            UploadCount++;
            if (UploadException is not null)
            {
                throw UploadException;
            }
            return Task.CompletedTask;
        }
    }
}
