using System.Globalization;
using System.Net;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Exports;

/// <summary>
/// 冻结 Outline 的确定性文档 ID、collection 白名单、受控端点、正文预算和安全重试契约。
/// </summary>
public sealed class OutlineEntryExporterTests
{
    private static readonly Guid CollectionId =
        Guid.Parse("10000000-0000-4000-8000-000000000017");
    private static readonly Uri Endpoint =
        new("https://outline.example.com/");

    [Fact]
    public void TargetQueueIdentityBindsEndpointAndCollectionWithoutDisclosure()
    {
        OutlineExportTarget target = Target();

        string queueTargetId = target.CreateQueueTargetId();

        Assert.Matches("^default\\.[0-9a-f]{24}$", queueTargetId);
        Assert.DoesNotContain("outline", queueTargetId, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CollectionId.ToString("D"),
            queueTargetId,
            StringComparison.Ordinal);
        Assert.NotEqual(
            queueTargetId,
            (target with { CollectionId = Guid.NewGuid() })
                .CreateQueueTargetId());
    }

    [Fact]
    public void StableDocumentIdDoesNotChangeWithContentHash()
    {
        FeedEntry entry = Entry();

        string queueTargetId = Target().CreateQueueTargetId();
        Guid first = OutlineEntryExporter.CreateDocumentId(
            queueTargetId,
            entry.Id);
        Guid changed = OutlineEntryExporter.CreateDocumentId(
            queueTargetId,
            (entry with { ContentHash = new string('b', 64) }).Id);

        Assert.Equal(first, changed);
        Assert.Equal(5, first.ToByteArray()[7] >> 4);
    }

    [Fact]
    public void DocumentIdChangesWithCollectionTargetRevision()
    {
        OutlineExportTarget firstTarget = Target();
        OutlineExportTarget secondTarget = firstTarget with
        {
            CollectionId = Guid.Parse(
                "20000000-0000-4000-8000-000000000017")
        };

        Guid first = OutlineEntryExporter.CreateDocumentId(
            firstTarget.CreateQueueTargetId(),
            Entry().Id);
        Guid second = OutlineEntryExporter.CreateDocumentId(
            secondTarget.CreateQueueTargetId(),
            Entry().Id);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task ExportUpdatesDeterministicDocumentInAllowedCollection()
    {
        var api = new FakeOutlineApiClient();
        var authorizer = new FakeEndpointAuthorizer();
        OutlineExportTarget target = Target();
        var exporter = new OutlineEntryExporter(
            new FakeTargetStore<OutlineExportTarget>(target),
            new FakePolicyService(new EntryIntegrationPolicy(
                EntryIntegrationKind.Outline,
                true,
                ["outline.example.com"])
            {
                AllowedResources = [CollectionId.ToString("D")]
            }),
            new FakeCredentialStore("outline-token"),
            authorizer,
            api);
        EntryExportRequest request = Request(target, Entry());

        EntryExportResult result = await exporter.ExportAsync(
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(request.IdempotencyKey, result.IdempotencyKey);
        Assert.Equal(1, authorizer.CallCount);
        Assert.Equal("outline-token", api.Token);
        OutlineDocument document = Assert.IsType<OutlineDocument>(api.Document);
        Assert.Equal(
            OutlineEntryExporter.CreateDocumentId(
                request.TargetId,
                request.Entry.Id),
            document.Id);
        Assert.Equal(CollectionId, document.CollectionId);
        Assert.Contains("Safe body", document.Text, StringComparison.Ordinal);
        Assert.Contains(
            "https://news.example.com/articles/1",
            document.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<img", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongCollectionStopsBeforeEndpointCredentialAndApi()
    {
        var authorizer = new FakeEndpointAuthorizer();
        var credentials = new FakeCredentialStore("outline-token");
        var api = new FakeOutlineApiClient();
        OutlineExportTarget target = Target();
        var exporter = new OutlineEntryExporter(
            new FakeTargetStore<OutlineExportTarget>(target),
            new FakePolicyService(new EntryIntegrationPolicy(
                EntryIntegrationKind.Outline,
                true,
                ["outline.example.com"])
            {
                AllowedResources = [Guid.NewGuid().ToString("D")]
            }),
            credentials,
            authorizer,
            api);

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(target, Entry()),
                    CancellationToken.None));

        Assert.Equal(EntryExportErrorCode.AccessDenied, exception.Error.Code);
        Assert.Equal(0, authorizer.CallCount);
        Assert.Equal(0, credentials.GetCount);
        Assert.Equal(0, api.UpsertCount);
    }

    [Fact]
    public async Task OversizedMarkdownFailsBeforeExternalDependencies()
    {
        OutlineExportTarget target = Target();
        var authorizer = new FakeEndpointAuthorizer();
        var exporter = new OutlineEntryExporter(
            new FakeTargetStore<OutlineExportTarget>(target),
            new FakePolicyService(new EntryIntegrationPolicy(
                EntryIntegrationKind.Outline,
                true,
                ["outline.example.com"])
            {
                AllowedResources = [CollectionId.ToString("D")]
            }),
            new FakeCredentialStore("outline-token"),
            authorizer,
            new FakeOutlineApiClient());
        FeedEntry oversized = Entry() with
        {
            SanitizedContent = $"<p>{new string('x', 70 * 1024)}</p>"
        };

        EntryExportException exception =
            await Assert.ThrowsAsync<EntryExportException>(() =>
                exporter.ExportAsync(
                    Request(target, oversized),
                    CancellationToken.None));

        Assert.Equal(
            EntryExportErrorCode.ContentTooLarge,
            exception.Error.Code);
        Assert.Equal(0, authorizer.CallCount);
    }

    private static OutlineExportTarget Target() => new(
        OutlineExportTarget.DefaultTargetId,
        Endpoint,
        CollectionId,
        CredentialVersion: 1);

    private static EntryExportRequest Request(
        OutlineExportTarget target,
        FeedEntry entry) =>
        EntryExportRequest.Create(
            OutlineEntryExporter.ExporterId,
            target.CreateQueueTargetId(),
            entry,
            EntryViewKind.Article,
            Encoding.UTF8.GetByteCount(entry.SanitizedContent));

    private static FeedEntry Entry() => new(
        "entry-1",
        "feed-1",
        "external-1",
        "https://news.example.com/articles/1",
        "A title",
        "Ada",
        DateTimeOffset.Parse(
            "2026-08-13T00:00:00Z",
            CultureInfo.InvariantCulture),
        null,
        "Summary",
        "<p>Safe body</p><img src=\"https://images.example.com/a.jpg\">",
        ["research"],
        [],
        new string('a', 64),
        DateTimeOffset.Parse(
            "2026-08-13T00:01:00Z",
            CultureInfo.InvariantCulture));

    private sealed class FakeTargetStore<T>(T target)
        : IIntegrationExportTargetStore<T>
        where T : class
    {
        public Task<T?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<T?>(target);

        public Task<IIntegrationExportTargetLease<T>>
            AcquireExportLeaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IIntegrationExportTargetLease<T>>(
                new FakeLease<T>(target));

        public Task SaveAsync(T value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLease<T>(T target)
        : IIntegrationExportTargetLease<T>
        where T : class
    {
        public T? Target { get; } = target;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePolicyService(EntryIntegrationPolicy policy)
        : IEntryIntegrationPolicyService
    {
        public Task<EntryIntegrationPolicySnapshot> GetAsync(
            EntryIntegrationPolicyScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EntryIntegrationPolicySnapshot(
                1,
                [policy],
                scope));

        public Task<EntryIntegrationPolicyMutationResult> ReplaceAsync(
            IReadOnlyList<EntryIntegrationPolicyInput> inputs,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCredentialStore(string? token)
        : IEntryIntegrationCredentialStore
    {
        public int GetCount { get; private set; }

        public Task<string?> GetAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            GetCount++;
            return Task.FromResult(token);
        }

        public Task<bool> ExistsAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken) =>
            Task.FromResult(token is not null);

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

    private sealed class FakeEndpointAuthorizer
        : IEntryIntegrationEndpointAuthorizer
    {
        public int CallCount { get; private set; }

        public Task<EntryIntegrationProbeContext?> AuthorizeAsync(
            EntryIntegrationTarget target,
            EntryIntegrationPolicy policy,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<EntryIntegrationProbeContext?>(new(
                target.Endpoint,
                [IPAddress.Parse("93.184.216.34")]));
        }
    }

    private sealed class FakeOutlineApiClient : IOutlineApiClient
    {
        public int UpsertCount { get; private set; }
        public string? Token { get; private set; }
        public OutlineDocument? Document { get; private set; }

        public Task<OutlineCapability> ProbeAsync(
            EntryIntegrationProbeContext context,
            string token,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OutlineCapability("1"));

        public Task<OutlineDocumentResult> UpsertAsync(
            EntryIntegrationProbeContext context,
            string token,
            OutlineDocument document,
            CancellationToken cancellationToken)
        {
            UpsertCount++;
            Token = token;
            Document = document;
            return Task.FromResult(new OutlineDocumentResult(
                document.Id,
                new Uri("https://outline.example.com/doc/a-title-id")));
        }
    }
}
