using System.Globalization;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Exports;

/// <summary>
/// 冻结 Reader 导出的最小数据与安全边界：队列只保存非秘密目标版本，执行时依次读取
/// ACTIVE 策略和 DPAPI 凭据，正文预览与实际发送内容必须完全一致。
/// </summary>
public sealed class ReadwiseEntryExporterTests
{
    private static readonly Uri SavedDocumentUrl = new(
        "https://read.readwise.io/new/read/document-1");

    [Fact]
    public void CapabilityAndPublicConstantsFreezeQueueContract()
    {
        ReadwiseEntryExporter exporter = CreateExporter();

        Assert.Equal("readwise", ReadwiseEntryExporter.ExporterId);
        Assert.Equal("default", ReadwiseEntryExporter.CredentialTargetId);
        Assert.Equal("default.v1", ReadwiseEntryExporter.QueueTargetId);
        Assert.Equal(
            "https://readwise.io/",
            ReadwiseEntryExporter.ApiRoot.AbsoluteUri);
        Assert.Equal("readwise", exporter.Capability.ExporterId);
        Assert.Equal("Readwise Reader", exporter.Capability.DisplayName);
        Assert.Equal(
            Enum.GetValues<EntryViewKind>(),
            exporter.Capability.SupportedViewKinds);
        Assert.True(exporter.Capability.RequiresCredentials);
        Assert.True(exporter.Capability.IsIdempotent);
        Assert.Equal(16 * 1024, exporter.Capability.MaximumContentBytes);
    }

    [Fact]
    public void ExcerptPreviewUsesSanitizedContentAndFallsBackToSummary()
    {
        FeedEntry entry = Entry() with
        {
            SanitizedContent = "  Alpha\r\n\tBeta \u00a0 Gamma  ",
            Summary = "must not be selected"
        };

        ReadwiseExcerptPreview contentPreview =
            ReadwiseEntryExporter.CreateExcerptPreview(entry);
        ReadwiseExcerptPreview fallbackPreview =
            ReadwiseEntryExporter.CreateExcerptPreview(entry with
            {
                SanitizedContent = " \r\n\t ",
                Summary = "  Summary\r\n fallback  "
            });

        Assert.Equal("Alpha Beta Gamma", contentPreview.Text);
        Assert.False(contentPreview.IsTruncated);
        Assert.Equal("Summary fallback", fallbackPreview.Text);
        Assert.False(fallbackPreview.IsTruncated);
    }

    [Fact]
    public void ExcerptPreviewStopsAtFourThousandUnicodeTextElements()
    {
        // UTF-16 中一个 emoji 占两个 char；此断言防止实现按 char 截断并制造坏代理项。
        string value = string.Concat(Enumerable.Repeat("😀", 4001));

        ReadwiseExcerptPreview preview =
            ReadwiseEntryExporter.CreateExcerptPreview(
                Entry() with { SanitizedContent = value });

        Assert.True(preview.IsTruncated);
        Assert.Equal(4000, new StringInfo(preview.Text).LengthInTextElements);
        Assert.Equal(16000, preview.Utf8Bytes);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(preview.Text),
            preview.Utf8Bytes);
    }

    [Fact]
    public void ExcerptPreviewStopsAtUtf8LimitWithoutSplittingTextElement()
    {
        // 一个字素包含基字符和多个组合符，用它触发 16 KiB 边界可覆盖“字素未超限但字节超限”。
        const string textElement = "a\u0301\u0301\u0301\u0301";
        string value = string.Concat(Enumerable.Repeat(textElement, 4000));

        ReadwiseExcerptPreview preview =
            ReadwiseEntryExporter.CreateExcerptPreview(
                Entry() with { SanitizedContent = value });

        Assert.True(preview.IsTruncated);
        Assert.InRange(preview.Utf8Bytes, 1, 16 * 1024);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(preview.Text),
            preview.Utf8Bytes);
        Assert.All(
            EnumerateTextElements(preview.Text),
            element => Assert.Equal(textElement, element));
    }

    [Theory]
    [InlineData("https://news.example.com/articles/1")]
    [InlineData("http://sub.example.org/a?b=1")]
    public void CanExportEntryAcceptsPublicHttpDnsUrl(string url)
    {
        Assert.True(ReadwiseEntryExporter.CanExportEntry(
            Entry() with { NormalizedUrl = url }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" https://example.com/article")]
    [InlineData("ftp://example.com/article")]
    [InlineData("https://example.com:444/article")]
    [InlineData("https://localhost/article")]
    [InlineData("https://reader.local/article")]
    [InlineData("https://127.0.0.1/article")]
    [InlineData("https://10.0.0.8/article")]
    [InlineData("https://[::1]/article")]
    [InlineData("https://user:password@example.com/article")]
    [InlineData("https://example.com/article#private-fragment")]
    public void CanExportEntryRejectsNonPublicOrNonCanonicalUrl(string? url)
    {
        Assert.False(ReadwiseEntryExporter.CanExportEntry(
            Entry() with { NormalizedUrl = url }));
    }

    [Fact]
    public void CanExportEntryRejectsUrlLongerThanTwoThousandFortyEightChars()
    {
        string url = $"https://example.com/{new string('a', 2049)}";

        Assert.False(ReadwiseEntryExporter.CanExportEntry(
            Entry() with { NormalizedUrl = url }));
    }

    [Fact]
    public void ContentBytesMatchesExactPreviewUtf8Count()
    {
        FeedEntry entry = Entry() with
        {
            SanitizedContent = "正文 😀 e\u0301 with spaces"
        };
        ReadwiseExcerptPreview preview =
            ReadwiseEntryExporter.CreateExcerptPreview(entry);

        Assert.Equal(
            preview.Utf8Bytes,
            ReadwiseEntryExporter.GetExportContentBytes(entry));
        Assert.Equal(
            Encoding.UTF8.GetByteCount(preview.Text),
            ReadwiseEntryExporter.GetExportContentBytes(entry));
    }

    [Fact]
    public async Task ExportMapsStableBoundedDocumentAfterPolicyAndCredential()
    {
        var events = new List<string>();
        var policies = new FakePolicyService(events: events);
        var credentials = new FakeCredentialStore(events: events);
        var api = new FakeReadwiseApiClient(events);
        ReadwiseEntryExporter exporter = CreateExporter(
            policies,
            credentials,
            api);
        FeedEntry entry = Entry() with
        {
            Title = "  A\t stable title  ",
            Author = "  Ada\r\nLovelace  ",
            PublishedAt = new DateTimeOffset(
                2026, 8, 3, 8, 0, 0, TimeSpan.FromHours(8)),
            SanitizedContent = "  Safe\r\nexcerpt  ",
            Summary = "private fallback must not win",
            Categories = [" RSS ", "rss", " research\nnotes ", ""]
        };

        EntryExportResult result = await exporter.ExportAsync(
            Request(entry),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["policy", "credential", "api"], events);
        Assert.Equal(EntryIntegrationPolicyScope.Active, policies.LastScope);
        Assert.Equal(EntryIntegrationKind.Readwise, credentials.LastKind);
        Assert.Equal(
            ReadwiseEntryExporter.CredentialTargetId,
            credentials.LastTargetId);
        Assert.Equal("private-readwise-token", api.AccessToken);
        ReadwiseDocument document = Assert.IsType<ReadwiseDocument>(api.Document);
        Assert.Equal(entry.NormalizedUrl, document.Url);
        Assert.Equal("A stable title", document.Title);
        Assert.Equal("Ada Lovelace", document.Author);
        Assert.Equal("Safe excerpt", document.Summary);
        Assert.Equal(
            "2026-08-03T00:00:00.0000000Z",
            document.PublishedDate);
        Assert.Equal(["RSS", "research notes"], document.Tags);
        Assert.Null(document.ImageUrl);
        Assert.Null(document.Notes);
    }

    [Fact]
    public async Task MetadataIsBoundedAndSummaryFallsBackWithoutPrivateFields()
    {
        var api = new FakeReadwiseApiClient();
        ReadwiseEntryExporter exporter = CreateExporter(api: api);
        FeedEntry entry = Entry() with
        {
            Title = new string('T', 1200),
            Author = new string('A', 700),
            SanitizedContent = " \r\n ",
            Summary = "  fallback\ttext  ",
            Categories = Enumerable.Range(0, 40)
                .Select(index => $" tag-{index:D2} ")
                .ToArray()
        };

        await exporter.ExportAsync(
            Request(entry),
            CancellationToken.None);

        ReadwiseDocument document = Assert.IsType<ReadwiseDocument>(api.Document);
        Assert.Equal(1024, new StringInfo(document.Title!).LengthInTextElements);
        Assert.Equal(512, new StringInfo(document.Author!).LengthInTextElements);
        Assert.Equal("fallback text", document.Summary);
        Assert.Equal(32, document.Tags.Count);
        Assert.All(
            document.Tags,
            tag => Assert.InRange(
                new StringInfo(tag).LengthInTextElements,
                1,
                64));
        Assert.Null(document.Notes);
        Assert.Null(document.ImageUrl);
    }

    [Fact]
    public async Task EmptyExcerptOmitsOptionalSummaryInsteadOfSendingBlankText()
    {
        var api = new FakeReadwiseApiClient();
        ReadwiseEntryExporter exporter = CreateExporter(api: api);
        FeedEntry entry = Entry() with
        {
            SanitizedContent = " \r\n ",
            Summary = "\t"
        };

        await exporter.ExportAsync(
            Request(entry),
            CancellationToken.None);

        Assert.Null(Assert.IsType<ReadwiseDocument>(api.Document).Summary);
    }

    [Fact]
    public async Task ExistingDocumentResponseIsSuccessfulIdempotentReplay()
    {
        var api = new FakeReadwiseApiClient
        {
            Result = new(
                "existing-document",
                SavedDocumentUrl,
                AlreadyExisted: true)
        };
        ReadwiseEntryExporter exporter = CreateExporter(api: api);
        EntryExportRequest request = Request(Entry());

        EntryExportResult result = await exporter.ExportAsync(
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(request.IdempotencyKey, result.IdempotencyKey);
        Assert.Equal("existing-document", result.RemoteId);
        Assert.Equal(SavedDocumentUrl, result.RemoteUrl);
        Assert.Equal(1, api.SaveCount);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledOrHostlessPolicyStopsBeforeCredentialAndClient(
        bool isEnabled,
        bool includesOfficialHost)
    {
        var events = new List<string>();
        var policies = new FakePolicyService(
            isEnabled,
            includesOfficialHost,
            events);
        var credentials = new FakeCredentialStore(events: events);
        var api = new FakeReadwiseApiClient(events);
        ReadwiseEntryExporter exporter = CreateExporter(
            policies,
            credentials,
            api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(Entry()),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.AccessDenied, exception.Error.Code);
        Assert.Equal(["policy"], events);
        Assert.Equal(0, credentials.GetCount);
        Assert.Equal(0, api.SaveCount);
    }

    [Fact]
    public async Task SimilarPolicyHostDoesNotAuthorizeReadwise()
    {
        var policies = new FakePolicyService(
            allowedHosts: ["api.readwise.io", "readwise.io.example"]);
        var credentials = new FakeCredentialStore();
        var api = new FakeReadwiseApiClient();
        ReadwiseEntryExporter exporter = CreateExporter(
            policies,
            credentials,
            api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(Entry()),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.AccessDenied, exception.Error.Code);
        Assert.Equal(0, credentials.GetCount);
        Assert.Equal(0, api.SaveCount);
    }

    [Fact]
    public async Task MissingCredentialUsesDefaultSlotAndStopsBeforeClient()
    {
        var credentials = new FakeCredentialStore(value: " \r\n ");
        var api = new FakeReadwiseApiClient();
        ReadwiseEntryExporter exporter = CreateExporter(
            credentials: credentials,
            api: api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(Entry()),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.CredentialsRequired,
            exception.Error.Code);
        Assert.Equal(EntryIntegrationKind.Readwise, credentials.LastKind);
        Assert.Equal("default", credentials.LastTargetId);
        Assert.Equal(0, api.SaveCount);
    }

    [Fact]
    public async Task WrongQueueTargetFailsBeforeExternalDependencies()
    {
        var policies = new FakePolicyService();
        var credentials = new FakeCredentialStore();
        var api = new FakeReadwiseApiClient();
        ReadwiseEntryExporter exporter = CreateExporter(
            policies,
            credentials,
            api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(Entry(), targetId: "default.v2"),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.Conflict, exception.Error.Code);
        Assert.Equal(0, policies.GetCount);
        Assert.Equal(0, credentials.GetCount);
        Assert.Equal(0, api.SaveCount);
    }

    public static TheoryData<
        ReadwiseApiFailure,
        EntryExportErrorCode,
        bool> ApiFailures => new()
        {
            {
                ReadwiseApiFailure.Unauthorized,
                EntryExportErrorCode.AccessDenied,
                false
            },
            {
                ReadwiseApiFailure.Rejected,
                EntryExportErrorCode.ProviderRejected,
                false
            },
            {
                ReadwiseApiFailure.RateLimited,
                EntryExportErrorCode.RateLimited,
                true
            },
            {
                ReadwiseApiFailure.Unavailable,
                EntryExportErrorCode.DestinationUnavailable,
                true
            },
            {
                ReadwiseApiFailure.UnknownWriteOutcome,
                EntryExportErrorCode.DestinationUnavailable,
                true
            },
            {
                ReadwiseApiFailure.BlockedEndpoint,
                EntryExportErrorCode.AccessDenied,
                false
            },
            {
                ReadwiseApiFailure.Cancelled,
                EntryExportErrorCode.DestinationUnavailable,
                true
            }
        };

    [Theory]
    [MemberData(nameof(ApiFailures))]
    public async Task ApiFailuresMapToClosedErrorsWithoutLeakingSecrets(
        ReadwiseApiFailure failure,
        EntryExportErrorCode expectedCode,
        bool expectedRetryable)
    {
        var api = new FakeReadwiseApiClient
        {
            SaveException = new(
                failure,
                expectedRetryable,
                TimeSpan.FromSeconds(37))
        };
        ReadwiseEntryExporter exporter = CreateExporter(api: api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(Entry()),
                    CancellationToken.None));

        Assert.Equal(expectedCode, exception.Error.Code);
        Assert.Equal(expectedRetryable, exception.Error.IsRetryable);
        Assert.Equal(
            failure is ReadwiseApiFailure.RateLimited
                or ReadwiseApiFailure.Unavailable
                ? TimeSpan.FromSeconds(37)
                : null,
            exception.Error.RetryAfter);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            "private-readwise-token",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutWrapping()
    {
        using var cancellation = new CancellationTokenSource();
        var api = new FakeReadwiseApiClient
        {
            OnSave = cancellation.Cancel
        };
        ReadwiseEntryExporter exporter = CreateExporter(api: api);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportAsync(
                Request(Entry()),
                cancellation.Token));
    }

    [Fact]
    public async Task ApiCancelledFailurePropagatesWhenCallerTokenIsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var api = new FakeReadwiseApiClient
        {
            OnSave = cancellation.Cancel,
            SaveException = new(
                ReadwiseApiFailure.Cancelled,
                isRetryable: false)
        };
        ReadwiseEntryExporter exporter = CreateExporter(api: api);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportAsync(
                Request(Entry()),
                cancellation.Token));
    }

    private static ReadwiseEntryExporter CreateExporter(
        FakePolicyService? policies = null,
        FakeCredentialStore? credentials = null,
        FakeReadwiseApiClient? api = null) => new(
            policies ?? new FakePolicyService(),
            credentials ?? new FakeCredentialStore(),
            api ?? new FakeReadwiseApiClient());

    private static EntryExportRequest Request(
        FeedEntry entry,
        string targetId = ReadwiseEntryExporter.QueueTargetId) =>
        EntryExportRequest.Create(
            ReadwiseEntryExporter.ExporterId,
            targetId,
            entry,
            EntryViewKind.Article,
            ReadwiseEntryExporter.GetExportContentBytes(entry));

    private static FeedEntry Entry() => new(
        "entry-1",
        "feed-1",
        "external-1",
        "https://news.example.com/articles/1",
        "A safe article",
        "Ada",
        new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero),
        null,
        "Short summary",
        "Safe sanitized content",
        ["rss", "research"],
        [],
        new string('a', 64),
        new DateTimeOffset(2026, 8, 3, 1, 0, 0, TimeSpan.Zero));

    private static List<string> EnumerateTextElements(string value)
    {
        var elements = new List<string>();
        TextElementEnumerator enumerator =
            StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }
        return elements;
    }

    private sealed class FakePolicyService : IEntryIntegrationPolicyService
    {
        private readonly bool _isEnabled;
        private readonly IReadOnlyList<string> _allowedHosts;
        private readonly List<string>? _events;

        public FakePolicyService(
            bool isEnabled = true,
            bool includesOfficialHost = true,
            List<string>? events = null,
            IReadOnlyList<string>? allowedHosts = null)
        {
            _isEnabled = isEnabled;
            _allowedHosts = allowedHosts
                ?? (includesOfficialHost ? ["readwise.io"] : []);
            _events = events;
        }

        public int GetCount { get; private set; }
        public EntryIntegrationPolicyScope? LastScope { get; private set; }

        public Task<EntryIntegrationPolicySnapshot> GetAsync(
            EntryIntegrationPolicyScope scope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCount++;
            LastScope = scope;
            _events?.Add("policy");
            IReadOnlyList<EntryIntegrationPolicy> policies =
            [
                new(
                    EntryIntegrationKind.Readwise,
                    _isEnabled,
                    _allowedHosts)
            ];
            return Task.FromResult(new EntryIntegrationPolicySnapshot(
                1,
                policies,
                scope,
                new DateTimeOffset(
                    2026, 8, 3, 0, 0, 0, TimeSpan.Zero)));
        }

        public Task<EntryIntegrationPolicyMutationResult> ReplaceAsync(
            IReadOnlyList<EntryIntegrationPolicyInput> inputs,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCredentialStore : IEntryIntegrationCredentialStore
    {
        private readonly string? _value;
        private readonly List<string>? _events;

        public FakeCredentialStore(
            string? value = "private-readwise-token",
            List<string>? events = null)
        {
            _value = value;
            _events = events;
        }

        public int GetCount { get; private set; }
        public EntryIntegrationKind? LastKind { get; private set; }
        public string? LastTargetId { get; private set; }

        public Task<string?> GetAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCount++;
            LastKind = kind;
            LastTargetId = targetId;
            _events?.Add("credential");
            return Task.FromResult(_value);
        }

        public Task<bool> ExistsAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_value is not null);

        public Task SetAsync(
            EntryIntegrationKind kind,
            string targetId,
            string value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReadwiseApiClient : IReadwiseApiClient
    {
        private readonly List<string>? _events;

        public FakeReadwiseApiClient(List<string>? events = null) =>
            _events = events;

        public ReadwiseSaveResult Result { get; init; } = new(
            "document-1",
            SavedDocumentUrl,
            AlreadyExisted: false);
        public ReadwiseApiException? SaveException { get; init; }
        public Action? OnSave { get; init; }
        public int SaveCount { get; private set; }
        public string? AccessToken { get; private set; }
        public ReadwiseDocument? Document { get; private set; }

        public Task ProbeAsync(
            string accessToken,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ProbePinnedAsync(
            string accessToken,
            IReadOnlyList<System.Net.IPAddress> pinnedAddresses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReadwiseSaveResult> SaveAsync(
            string accessToken,
            ReadwiseDocument document,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            AccessToken = accessToken;
            Document = document;
            _events?.Add("api");
            OnSave?.Invoke();
            if (SaveException is not null)
            {
                throw SaveException;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }
}
