using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Exports;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Exports;

public sealed class EntryExportCoordinatorTests
{
    [Fact]
    public void RequestFactoryCreatesStableScopedSha256IdempotencyKeys()
    {
        FeedEntry entry = Entry("content-a");

        EntryExportRequest first = EntryExportRequest.Create(
            "markdown",
            "vault-a",
            entry,
            EntryViewKind.Article,
            contentBytes: 128);
        EntryExportRequest repeated = EntryExportRequest.Create(
            "markdown",
            "vault-a",
            entry,
            EntryViewKind.Article,
            contentBytes: 128);
        EntryExportRequest otherTarget = EntryExportRequest.Create(
            "markdown",
            "vault-b",
            entry,
            EntryViewKind.Article,
            contentBytes: 128);
        EntryExportRequest otherContent = EntryExportRequest.Create(
            "markdown",
            "vault-a",
            Entry("content-b"),
            EntryViewKind.Article,
            contentBytes: 128);
        EntryExportRequest otherView = EntryExportRequest.Create(
            "markdown",
            "vault-a",
            entry,
            EntryViewKind.Picture,
            contentBytes: 128);

        Assert.Equal(first.IdempotencyKey, repeated.IdempotencyKey);
        Assert.Matches("^[0-9a-f]{64}$", first.IdempotencyKey);
        Assert.NotEqual(first.IdempotencyKey, otherTarget.IdempotencyKey);
        Assert.NotEqual(first.IdempotencyKey, otherContent.IdempotencyKey);
        Assert.NotEqual(first.IdempotencyKey, otherView.IdempotencyKey);
    }

    [Fact]
    public void CapabilitiesAreValidatedCopiedAndSorted()
    {
        EntryViewKind[] mutableKinds =
            [EntryViewKind.Video, EntryViewKind.Article];
        var second = new StubExporter(
            Capability(
                "zotero",
                mutableKinds,
                requiresCredentials: true,
                maximumContentBytes: 4096,
                isIdempotent: false));
        var first = new StubExporter(
            Capability(
                "markdown",
                [EntryViewKind.Article],
                requiresCredentials: false,
                maximumContentBytes: null,
                isIdempotent: true));

        var coordinator = new EntryExportCoordinator([second, first]);
        mutableKinds[0] = EntryViewKind.Notification;

        Assert.Equal(
            ["markdown", "zotero"],
            coordinator.Capabilities.Select(value => value.ExporterId));
        EntryExportCapability capability = coordinator.Capabilities[1];
        Assert.Equal(
            [EntryViewKind.Article, EntryViewKind.Video],
            capability.SupportedViewKinds);
        Assert.True(capability.RequiresCredentials);
        Assert.Equal(4096, capability.MaximumContentBytes);
        Assert.False(capability.IsIdempotent);
    }

    [Fact]
    public async Task RoutesValidRequestAndReturnsStructuredSuccess()
    {
        var exporter = new StubExporter(
            Capability(
                "markdown",
                [EntryViewKind.Article],
                requiresCredentials: false,
                maximumContentBytes: 1024,
                isIdempotent: true));
        var coordinator = new EntryExportCoordinator([exporter]);
        EntryExportRequest request = Request();
        exporter.Handler = (received, cancellationToken) =>
        {
            Assert.Same(request, received);
            Assert.True(cancellationToken.CanBeCanceled);
            return Task.FromResult(
                EntryExportResult.Success(
                    received.IdempotencyKey,
                    "remote-42",
                    new("https://exports.example/items/42")));
        };
        using var cancellation = new CancellationTokenSource();

        EntryExportResult result = await coordinator.ExportAsync(
            request,
            cancellation.Token);

        Assert.True(result.Succeeded);
        Assert.Equal("remote-42", result.RemoteId);
        Assert.Equal(
            new Uri("https://exports.example/items/42"),
            result.RemoteUrl);
        Assert.Null(result.Error);
        Assert.Equal(1, exporter.CallCount);
    }

    [Fact]
    public async Task UnsupportedViewReturnsFailureWithoutCallingExporter()
    {
        var exporter = new StubExporter(
            Capability(
                "markdown",
                [EntryViewKind.Article],
                requiresCredentials: false,
                maximumContentBytes: null,
                isIdempotent: true));
        var coordinator = new EntryExportCoordinator([exporter]);
        EntryExportRequest request = EntryExportRequest.Create(
            "markdown",
            "vault-a",
            Entry(),
            EntryViewKind.Video,
            contentBytes: 128);

        EntryExportResult result = await coordinator.ExportAsync(
            request,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(
            EntryExportErrorCode.UnsupportedContent,
            result.Error?.Code);
        Assert.False(result.Error?.IsRetryable);
        Assert.Equal(0, exporter.CallCount);
    }

    [Fact]
    public async Task OversizedRequestReturnsFailureWithoutCallingExporter()
    {
        var exporter = new StubExporter(
            Capability(
                "markdown",
                [EntryViewKind.Article],
                requiresCredentials: false,
                maximumContentBytes: 127,
                isIdempotent: true));
        var coordinator = new EntryExportCoordinator([exporter]);

        EntryExportResult result = await coordinator.ExportAsync(
            Request(contentBytes: 128),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(
            EntryExportErrorCode.ContentTooLarge,
            result.Error?.Code);
        Assert.False(result.Error?.IsRetryable);
        Assert.Equal(0, exporter.CallCount);
    }

    [Fact]
    public async Task MissingExporterReturnsStructuredFailure()
    {
        var coordinator = new EntryExportCoordinator([]);

        EntryExportResult result = await coordinator.ExportAsync(
            Request(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(
            EntryExportErrorCode.ExporterNotFound,
            result.Error?.Code);
        Assert.Equal(Request().IdempotencyKey, result.IdempotencyKey);
    }

    [Fact]
    public async Task MapsTypedFailureAndBoundsRetryAfter()
    {
        var exporter = new StubExporter(
            Capability(
                "markdown",
                [EntryViewKind.Article],
                requiresCredentials: false,
                maximumContentBytes: null,
                isIdempotent: true))
        {
            Handler = (_, _) => throw new EntryExportException(
                new(
                    EntryExportErrorCode.RateLimited,
                    IsRetryable: true,
                    RetryAfter: TimeSpan.FromDays(30)),
                new InvalidOperationException(
                    "provider response with secret-token"))
        };
        var coordinator = new EntryExportCoordinator([exporter]);

        EntryExportResult result = await coordinator.ExportAsync(
            Request(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(
            EntryExportErrorCode.RateLimited,
            result.Error?.Code);
        Assert.True(result.Error?.IsRetryable);
        Assert.Equal(
            EntryExportCoordinator.MaximumRetryAfter,
            result.Error?.RetryAfter);
        Assert.DoesNotContain(
            "secret-token",
            result.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedAdapterFailureDoesNotExposeProviderDetails()
    {
        var exporter = new StubExporter(
            Capability(
                "markdown",
                [EntryViewKind.Article],
                requiresCredentials: false,
                maximumContentBytes: null,
                isIdempotent: true))
        {
            Handler = (_, _) => throw new InvalidOperationException(
                "provider-body password=super-secret")
        };
        var coordinator = new EntryExportCoordinator([exporter]);

        EntryExportResult result = await coordinator.ExportAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(EntryExportErrorCode.Unknown, result.Error?.Code);
        Assert.False(result.Error?.IsRetryable);
        Assert.DoesNotContain(
            "super-secret",
            result.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationPropagatesToAdapter()
    {
        var exporter = new StubExporter(
            Capability(
                "markdown",
                [EntryViewKind.Article],
                requiresCredentials: false,
                maximumContentBytes: null,
                isIdempotent: true))
        {
            Handler = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }
        };
        var coordinator = new EntryExportCoordinator([exporter]);
        using var cancellation = new CancellationTokenSource();

        Task<EntryExportResult> operation = coordinator.ExportAsync(
            Request(),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation);
    }

    [Fact]
    public async Task RejectsAdapterResultForAnotherIdempotencyKey()
    {
        var exporter = new StubExporter(
            Capability(
                "markdown",
                [EntryViewKind.Article],
                requiresCredentials: false,
                maximumContentBytes: null,
                isIdempotent: true))
        {
            Handler = (_, _) => Task.FromResult(
                EntryExportResult.Success(
                    new string('f', 64),
                    "wrong-request",
                    remoteUrl: null))
        };
        var coordinator = new EntryExportCoordinator([exporter]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ExportAsync(
                Request(),
                CancellationToken.None));
    }

    [Fact]
    public async Task RejectsForgedOrNonCanonicalRequestsBeforeAdapter()
    {
        var exporter = new StubExporter(
            Capability(
                "markdown",
                [EntryViewKind.Article],
                requiresCredentials: false,
                maximumContentBytes: null,
                isIdempotent: true));
        var coordinator = new EntryExportCoordinator([exporter]);
        EntryExportRequest valid = Request();

        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.ExportAsync(
                valid with
                {
                    IdempotencyKey = new string('f', 64)
                },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.ExportAsync(
                EntryExportRequest.Create(
                    "markdown",
                    " vault-a ",
                    Entry(),
                    EntryViewKind.Article,
                    contentBytes: 128),
                CancellationToken.None));
        Assert.Equal(0, exporter.CallCount);
    }

    [Fact]
    public async Task RejectsUnsafeRemoteUrlFromAdapter()
    {
        var exporter = new StubExporter(
            Capability(
                "markdown",
                [EntryViewKind.Article],
                requiresCredentials: false,
                maximumContentBytes: null,
                isIdempotent: true))
        {
            Handler = (request, _) => Task.FromResult(
                EntryExportResult.Success(
                    request.IdempotencyKey,
                    "remote",
                    new("https://token@example.com/item")))
        };
        var coordinator = new EntryExportCoordinator([exporter]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ExportAsync(
                Request(),
                CancellationToken.None));
    }

    [Fact]
    public void RejectsDuplicateExporterIdsAndInvalidCapabilities()
    {
        var first = new StubExporter(
            Capability(
                "markdown",
                [EntryViewKind.Article],
                requiresCredentials: false,
                maximumContentBytes: null,
                isIdempotent: true));
        var duplicate = new StubExporter(first.Capability);
        var invalid = new StubExporter(
            Capability(
                "markdown",
                [],
                requiresCredentials: false,
                maximumContentBytes: null,
                isIdempotent: true));

        Assert.Throws<ArgumentException>(
            () => new EntryExportCoordinator([first, duplicate]));
        Assert.Throws<ArgumentException>(
            () => new EntryExportCoordinator([invalid]));
    }

    [Fact]
    public void PublicOperationModelsDoNotExposeCredentialsOrProviderPayloads()
    {
        Type[] operationModels =
        [
            typeof(EntryExportRequest),
            typeof(EntryExportResult),
            typeof(EntryExportError)
        ];

        Assert.All(
            operationModels.SelectMany(type => type.GetProperties()),
            property =>
            {
                Assert.DoesNotContain(
                    "Token",
                    property.Name,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "Password",
                    property.Name,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "Credential",
                    property.Name,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "Response",
                    property.Name,
                    StringComparison.OrdinalIgnoreCase);
            });
    }

    private static EntryExportCapability Capability(
        string exporterId,
        IReadOnlyList<EntryViewKind> supportedViewKinds,
        bool requiresCredentials,
        long? maximumContentBytes,
        bool isIdempotent) =>
        new(
            exporterId,
            exporterId,
            supportedViewKinds,
            requiresCredentials,
            maximumContentBytes,
            isIdempotent);

    private static EntryExportRequest Request(long contentBytes = 128) =>
        EntryExportRequest.Create(
            "markdown",
            "vault-a",
            Entry(),
            EntryViewKind.Article,
            contentBytes);

    private static FeedEntry Entry(string contentHashSeed = "content-a")
    {
        DateTimeOffset now =
            new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        string contentHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(contentHashSeed)))
            .ToLowerInvariant();
        return new(
            "entry-42",
            "30000000-0000-4000-8000-000000000001",
            "external-42",
            "https://example.com/items/42",
            "Export item",
            "Author",
            now,
            null,
            "Summary",
            "Sanitized content",
            [],
            [],
            contentHash,
            now);
    }

    private sealed class StubExporter(
        EntryExportCapability capability)
        : IEntryExporter
    {
        public EntryExportCapability Capability { get; } = capability;
        public int CallCount { get; private set; }
        public Func<
            EntryExportRequest,
            CancellationToken,
            Task<EntryExportResult>>? Handler { get; set; }

        public Task<EntryExportResult> ExportAsync(
            EntryExportRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Handler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(
                    EntryExportResult.Success(
                        request.IdempotencyKey,
                        "remote",
                        remoteUrl: null));
        }
    }
}
