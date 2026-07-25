using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedRefreshServiceTests
{
    private const string FeedId = "30000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SuccessfulFetchParsesEntriesAndPersistsValidatorsAndSchedule()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var transport = new FakeRefreshTransport((_, _, request, _) =>
        {
            Assert.Null(request.ETag);
            Assert.Null(request.LastModified);
            return Task.FromResult(Response(
                HttpStatusCode.OK,
                "<rss version='2.0'><channel><title>Daily</title><item><guid>one</guid><title>One</title></item></channel></rss>",
                "\"v1\"",
                "Tue, 21 Jul 2026 10:00:00 GMT"));
        });
        using FeedRefreshService service = CreateService(repository, transport);

        FeedRefreshResult result = await service.RefreshAsync(FeedId, force: true, CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
        Assert.Equal(1, result.ParsedEntryCount);
        FeedFetchState saved = Assert.IsType<FeedFetchState>(repository.Saved);
        Assert.Equal("\"v1\"", saved.ETag);
        Assert.Equal("Tue, 21 Jul 2026 10:00:00 GMT", saved.LastModified);
        Assert.Equal(Now, saved.LastSuccessAt);
        Assert.Equal(Now.AddMinutes(60), saved.NextFetchAt);
        Assert.Equal(0, saved.ConsecutiveFailures);
        Assert.Null(saved.ErrorCode);
    }

    [Fact]
    public async Task NotModifiedSendsValidatorsWithoutParsingOrReplacingEntries()
    {
        var state = new FeedFetchState(
            FeedId,
            "\"old\"",
            "Mon, 20 Jul 2026 10:00:00 GMT",
            Now.AddMinutes(-1),
            Now.AddHours(-1),
            null,
            0,
            null,
            Now.AddHours(-1));
        var repository = new FakeFeedFetchStateRepository(Target(state));
        var transport = new FakeRefreshTransport((_, _, request, _) =>
        {
            Assert.Equal("\"old\"", request.ETag);
            Assert.Equal("Mon, 20 Jul 2026 10:00:00 GMT", request.LastModified);
            return Task.FromResult(new FeedRefreshHttpResponse(new HttpResponseMessage(HttpStatusCode.NotModified)));
        });
        var writer = new FakeFeedEntryWriter();
        var planning = new FakeFeedAutomationPlanningService();
        using FeedRefreshService service = CreateService(
            repository,
            transport,
            entryWriter: writer,
            automationPlanning: planning);

        FeedRefreshResult result = await service.RefreshAsync(FeedId, force: false, CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.NotModified, result.Outcome);
        Assert.Equal(0, result.ParsedEntryCount);
        Assert.Equal(Now.AddMinutes(60), repository.Saved?.NextFetchAt);
        Assert.Equal(Now, repository.Saved?.LastSuccessAt);
        Assert.Equal("\"old\"", repository.Saved?.ETag);
        Assert.Equal(0, writer.Calls);
        Assert.Equal(0, planning.StageCalls);
    }

    [Fact]
    public async Task UnconditionalNotModifiedIsRejectedToAvoidMissingInitialEntries()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var writer = new FakeFeedEntryWriter();
        var transport = new FakeRefreshTransport((_, _, _, _) => Task.FromResult(
            new FeedRefreshHttpResponse(new HttpResponseMessage(HttpStatusCode.NotModified))));
        using FeedRefreshService service = CreateService(repository, transport, entryWriter: writer);

        FeedRefreshResult result = await service.RefreshAsync(FeedId, force: true, CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Failed, result.Outcome);
        Assert.Equal("invalid_response", result.ErrorCode);
        Assert.Equal(0, writer.Calls);
        Assert.Null(repository.Saved?.ETag);
    }

    [Fact]
    public async Task EntriesAreWrittenBeforeSuccessfulValidatorsAreCommitted()
    {
        var writer = new FakeFeedEntryWriter();
        var repository = new FakeFeedFetchStateRepository(Target())
        {
            BeforeSave = () => Assert.Equal(1, writer.Calls)
        };
        var transport = new FakeRefreshTransport((_, _, _, _) => Task.FromResult(
            Response(HttpStatusCode.OK, Rss("persisted"), "\"new-validator\"")));
        using FeedRefreshService service = CreateService(repository, transport, entryWriter: writer);

        FeedRefreshResult result = await service.RefreshAsync(FeedId, force: true, CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
        FeedEntry entry = Assert.Single(writer.LastEntries);
        Assert.Equal("persisted", entry.ExternalId);
        Assert.Equal("\"new-validator\"", repository.Saved?.ETag);
    }

    [Fact]
    public async Task SuccessfulRefreshQueuesPersistedEntriesForAiAutomation()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var automation = new FakeFeedAiAutomationQueueService();
        var transport = new FakeRefreshTransport((_, _, _, _) => Task.FromResult(
            Response(HttpStatusCode.OK, Rss("queued"))));
        using FeedRefreshService service = CreateService(
            repository,
            transport,
            aiAutomationQueue: automation);

        FeedRefreshResult result = await service.RefreshAsync(
            FeedId,
            force: true,
            CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
        Assert.Equal(1, automation.EnqueueCalls);
        Assert.Equal("queued", Assert.Single(automation.LastEntries).ExternalId);
    }

    [Fact]
    public async Task AiQueueFailureDoesNotChangeSuccessfulRefreshOutcome()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var automation = new FakeFeedAiAutomationQueueService
        {
            Failure = new IOException("queue unavailable")
        };
        var transport = new FakeRefreshTransport((_, _, _, _) => Task.FromResult(
            Response(HttpStatusCode.OK, Rss("still-persisted"))));
        using FeedRefreshService service = CreateService(
            repository,
            transport,
            aiAutomationQueue: automation);

        FeedRefreshResult result = await service.RefreshAsync(
            FeedId,
            force: true,
            CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
        Assert.Equal(1, automation.EnqueueCalls);
        Assert.Equal(Now, repository.Saved?.LastSuccessAt);
    }

    [Fact]
    public async Task SuccessfulRefreshStagesPersistedEntriesForRules()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var planning = new FakeFeedAutomationPlanningService();
        var transport = new FakeRefreshTransport((_, _, _, _) =>
            Task.FromResult(
                Response(
                    HttpStatusCode.OK,
                    Rss("rule-planned"))));
        using FeedRefreshService service = CreateService(
            repository,
            transport,
            automationPlanning: planning);

        FeedRefreshResult result = await service.RefreshAsync(
            FeedId,
            force: true,
            CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
        Assert.Equal(1, planning.StageCalls);
        Assert.Equal(FeedId, planning.LastFeed?.Id);
        Assert.Equal(
            "rule-planned",
            Assert.Single(planning.LastEntries).ExternalId);
        Assert.Equal(Now, repository.Saved?.LastSuccessAt);
    }

    [Fact]
    public async Task RulePlanningFailureDoesNotChangeSuccessfulRefreshOutcome()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var planning = new FakeFeedAutomationPlanningService
        {
            Failure = new IOException("rule cache unavailable")
        };
        var transport = new FakeRefreshTransport((_, _, _, _) =>
            Task.FromResult(
                Response(
                    HttpStatusCode.OK,
                    Rss("still-successful"))));
        using FeedRefreshService service = CreateService(
            repository,
            transport,
            automationPlanning: planning);

        FeedRefreshResult result = await service.RefreshAsync(
            FeedId,
            force: true,
            CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
        Assert.Equal(1, planning.StageCalls);
        Assert.Equal(Now, repository.Saved?.LastSuccessAt);
    }

    [Fact]
    public async Task EntryWriteFailureDoesNotCommitNewValidator()
    {
        var oldState = new FeedFetchState(
            FeedId, "\"old\"", null, Now.AddMinutes(-1), Now.AddHours(-1), null, 0, null, Now);
        var repository = new FakeFeedFetchStateRepository(Target(oldState));
        var writer = new FakeFeedEntryWriter { Failure = new IOException("disk details") };
        var transport = new FakeRefreshTransport((_, _, _, _) => Task.FromResult(
            Response(HttpStatusCode.OK, Rss("not-committed"), "\"new\"")));
        using FeedRefreshService service = CreateService(repository, transport, entryWriter: writer);

        FeedRefreshResult result = await service.RefreshAsync(FeedId, force: true, CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Failed, result.Outcome);
        Assert.Equal("storage", result.ErrorCode);
        Assert.Equal("\"old\"", repository.Saved?.ETag);
        Assert.DoesNotContain("disk", repository.Saved?.ErrorCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FutureScheduleSkipsNetworkUnlessForced()
    {
        var state = new FeedFetchState(
            FeedId, null, null, Now.AddMinutes(10), Now.AddHours(-1), null, 0, null, Now);
        var repository = new FakeFeedFetchStateRepository(Target(state));
        int calls = 0;
        var transport = new FakeRefreshTransport((_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Response(HttpStatusCode.OK, Rss("forced")));
        });
        using FeedRefreshService service = CreateService(repository, transport);

        FeedRefreshResult skipped = await service.RefreshAsync(FeedId, force: false, CancellationToken.None);
        FeedRefreshResult forced = await service.RefreshAsync(FeedId, force: true, CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.SkippedNotDue, skipped.Outcome);
        Assert.Equal(FeedRefreshOutcome.Updated, forced.Outcome);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 32)]
    [InlineData(20, 360)]
    public async Task ServerFailuresUseCappedExponentialBackoff(int previousFailures, int expectedMinutes)
    {
        var state = new FeedFetchState(
            FeedId, "\"keep\"", null, Now.AddMinutes(-1), Now.AddHours(-1), null,
            previousFailures, null, Now.AddHours(-1));
        var repository = new FakeFeedFetchStateRepository(Target(state));
        var transport = new FakeRefreshTransport((_, _, _, _) => Task.FromResult(
            new FeedRefreshHttpResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        using FeedRefreshService service = CreateService(repository, transport);

        FeedRefreshResult result = await service.RefreshAsync(FeedId, force: false, CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Failed, result.Outcome);
        Assert.Equal("http_503", result.ErrorCode);
        Assert.Equal(previousFailures + 1, repository.Saved?.ConsecutiveFailures);
        Assert.Equal(Now.AddMinutes(expectedMinutes), repository.Saved?.NextFetchAt);
        Assert.Equal("\"keep\"", repository.Saved?.ETag);
    }

    [Fact]
    public async Task RateLimitHonorsRetryAfterWithinMaximumBackoff()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var transport = new FakeRefreshTransport((_, _, _, _) =>
        {
            var message = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            message.Headers.RetryAfter = new(TimeSpan.FromHours(2));
            return Task.FromResult(new FeedRefreshHttpResponse(message));
        });
        using FeedRefreshService service = CreateService(repository, transport);

        FeedRefreshResult result = await service.RefreshAsync(FeedId, force: true, CancellationToken.None);

        Assert.Equal("http_429", result.ErrorCode);
        Assert.Equal(Now.AddHours(2), repository.Saved?.NextFetchAt);
    }

    [Fact]
    public async Task InvalidFeedIsIsolatedAndRecordedWithoutLeakingParserDetails()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var transport = new FakeRefreshTransport((_, _, _, _) => Task.FromResult(
            Response(HttpStatusCode.OK, "<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///secret'>]><rss version='2.0'><channel><title>&x;</title></channel></rss>")));
        using FeedRefreshService service = CreateService(repository, transport);

        FeedRefreshResult result = await service.RefreshAsync(FeedId, force: true, CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Failed, result.Outcome);
        Assert.Equal("invalid_feed", result.ErrorCode);
        Assert.Equal("invalid_feed", repository.Saved?.ErrorCode);
        Assert.DoesNotContain("secret", repository.Saved?.ErrorCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentRefreshOfSameFeedMakesOnlyOneRequest()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        int calls = 0;
        var transport = new FakeRefreshTransport(async (_, _, _, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(30, cancellationToken);
            return Response(HttpStatusCode.OK, Rss("single"));
        });
        using FeedRefreshService service = CreateService(repository, transport);

        FeedRefreshResult[] results = await Task.WhenAll(
            service.RefreshAsync(FeedId, force: false, CancellationToken.None),
            service.RefreshAsync(FeedId, force: false, CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Contains(results, result => result.Outcome == FeedRefreshOutcome.Updated);
        Assert.Contains(results, result => result.Outcome == FeedRefreshOutcome.SkippedNotDue);
    }

    [Fact]
    public async Task BatchContinuesWhenOneSourceFails()
    {
        const string secondId = "30000000-0000-4000-8000-000000000002";
        var repository = new FakeFeedFetchStateRepository(Target(), Target(feedId: secondId));
        var transport = new FakeRefreshTransport((uri, _, _, _) => Task.FromResult(
            uri.AbsolutePath.Contains(FeedId, StringComparison.Ordinal)
                ? new FeedRefreshHttpResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError))
                : Response(HttpStatusCode.OK, Rss("healthy"))));
        using FeedRefreshService service = CreateService(repository, transport);

        FeedRefreshBatchResult result = await service.RefreshDueAsync(CancellationToken.None);

        Assert.Equal(2, result.Attempted);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Failed);
        Assert.Equal("http_500", repository.SavedById[FeedId].ErrorCode);
        Assert.Null(repository.SavedById[secondId].ErrorCode);
    }

    [Fact]
    public async Task CallerCancellationStopsRequestWithoutRecordingFailure()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var transport = new FakeRefreshTransport(async (_, _, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using FeedRefreshService service = CreateService(repository, transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RefreshAsync(FeedId, force: true, cancellation.Token));

        Assert.Null(repository.Saved);
    }

    [Fact]
    public async Task TotalTimeoutRecordsRetryableFailure()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var transport = new FakeRefreshTransport(async (_, _, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        FeedDiscoveryOptions networkOptions = FeedDiscoveryOptions.Default with
        {
            TotalTimeout = TimeSpan.FromMilliseconds(40),
            ConnectTimeout = TimeSpan.FromMilliseconds(20)
        };
        using FeedRefreshService service = CreateService(repository, transport, networkOptions: networkOptions);

        FeedRefreshResult result = await service.RefreshAsync(FeedId, force: true, CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Failed, result.Outcome);
        Assert.Equal("timeout", result.ErrorCode);
        Assert.Equal(Now.AddMinutes(1), repository.Saved?.NextFetchAt);
    }

    [Fact]
    public async Task DueBatchHonorsConfiguredConcurrencyBound()
    {
        FeedRefreshTarget[] targets = Enumerable.Range(1, 5)
            .Select(index => Target(feedId: $"30000000-0000-4000-8000-{index:D12}"))
            .ToArray();
        var repository = new FakeFeedFetchStateRepository(targets);
        int active = 0;
        int maximumActive = 0;
        var transport = new FakeRefreshTransport(async (_, _, _, cancellationToken) =>
        {
            int current = Interlocked.Increment(ref active);
            int observed;
            do
            {
                observed = maximumActive;
            }
            while (current > observed
                && Interlocked.CompareExchange(ref maximumActive, current, observed) != observed);
            await Task.Delay(25, cancellationToken);
            Interlocked.Decrement(ref active);
            return Response(HttpStatusCode.OK, Rss("bounded"));
        });
        FeedRefreshOptions options = FeedRefreshOptions.Default with { MaximumConcurrency = 2 };
        using FeedRefreshService service = CreateService(repository, transport, options);

        FeedRefreshBatchResult result = await service.RefreshDueAsync(CancellationToken.None);

        Assert.Equal(5, result.Updated);
        Assert.InRange(maximumActive, 1, 2);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightScheduledFetch()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeRefreshTransport(async (_, _, _, cancellationToken) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("unreachable");
        });
        var service = CreateService(repository, transport);

        await service.InitializeAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        service.Dispose();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Null(repository.Saved);
    }

    [Fact]
    public async Task ProductionTransportPinsAddressAndWritesConditionalHeaders()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        string? captured = null;
        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token);
            NetworkStream stream = client.GetStream();
            byte[] requestBuffer = new byte[4096];
            int received = 0;
            while (received < requestBuffer.Length)
            {
                int read = await stream.ReadAsync(requestBuffer.AsMemory(received), timeout.Token);
                if (read == 0) break;
                received += read;
                captured = Encoding.ASCII.GetString(requestBuffer, 0, received);
                if (captured.Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 304 Not Modified\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, timeout.Token);
        }, timeout.Token);
        FeedDiscoveryOptions options = FeedDiscoveryOptions.Default with
        {
            ConnectTimeout = TimeSpan.FromSeconds(1)
        };
        var transport = new PinnedFeedRefreshTransport(options);

        using FeedRefreshHttpResponse response = await transport.SendAsync(
            new Uri($"http://must-not-resolve.invalid:{port}/feed"),
            [IPAddress.Loopback],
            new FeedRefreshHttpRequest("\"v1\"", "Tue, 21 Jul 2026 10:00:00 GMT"),
            timeout.Token);
        await server;
        listener.Stop();

        Assert.Equal(HttpStatusCode.NotModified, response.Message.StatusCode);
        Assert.Contains("If-None-Match: \"v1\"", captured, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("If-Modified-Since: Tue, 21 Jul 2026 10:00:00 GMT", captured, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrossAuthorityRedirectIsRevalidatedWithoutForwardingValidators()
    {
        var state = new FeedFetchState(
            FeedId, "\"private-validator\"", null, Now.AddMinutes(-1), Now.AddHours(-1), null, 0, null, Now);
        var repository = new FakeFeedFetchStateRepository(Target(state));
        int calls = 0;
        var transport = new FakeRefreshTransport((uri, _, request, _) =>
        {
            calls++;
            if (uri.Host == "feeds.example")
            {
                Assert.Equal("\"private-validator\"", request.ETag);
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://redirect.example/feed.xml") }
                };
                return Task.FromResult(new FeedRefreshHttpResponse(redirect));
            }

            Assert.Equal("redirect.example", uri.Host);
            Assert.Null(request.ETag);
            return Task.FromResult(Response(HttpStatusCode.OK, Rss("redirected")));
        });
        var resolver = new RecordingResolver();
        using FeedRefreshService service = CreateService(repository, transport, resolver: resolver);

        FeedRefreshResult result = await service.RefreshAsync(FeedId, force: true, CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
        Assert.Equal(2, calls);
        Assert.Equal(["feeds.example", "redirect.example"], resolver.Hosts);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedAndBackedOff()
    {
        var repository = new FakeFeedFetchStateRepository(Target());
        var transport = new FakeRefreshTransport((_, _, _, _) => Task.FromResult(
            Response(HttpStatusCode.OK, new string('x', 1200))));
        FeedDiscoveryOptions networkOptions = FeedDiscoveryOptions.Default with
        {
            MaximumCompressedBytes = 1024,
            MaximumDecompressedBytes = 2048
        };
        using FeedRefreshService service = CreateService(repository, transport, networkOptions: networkOptions);

        FeedRefreshResult result = await service.RefreshAsync(FeedId, force: true, CancellationToken.None);

        Assert.Equal(FeedRefreshOutcome.Failed, result.Outcome);
        Assert.Equal("invalid_response", result.ErrorCode);
        Assert.Equal(Now.AddMinutes(1), result.NextFetchAt);
    }

    private static FeedRefreshService CreateService(
        IFeedFetchStateRepository repository,
        IFeedRefreshTransport transport,
        FeedRefreshOptions? options = null,
        FeedDiscoveryOptions? networkOptions = null,
        IFeedHostResolver? resolver = null,
        IFeedEntryWriter? entryWriter = null,
        IFeedAiAutomationQueueService? aiAutomationQueue = null,
        IFeedAutomationPlanningService? automationPlanning = null) => new(
            repository,
            entryWriter ?? new FakeFeedEntryWriter(),
            new FeedDocumentParser(),
            resolver ?? new FakeResolver(),
            transport,
            networkOptions ?? FeedDiscoveryOptions.Default,
            options ?? FeedRefreshOptions.Default,
            new FixedTimeProvider(Now),
            aiAutomationQueue,
            automationPlanning);

    private static FeedRefreshTarget Target(
        FeedFetchState? state = null,
        string feedId = FeedId) => new(
        new(
            feedId,
            $"https://feeds.example/{feedId}.xml",
            $"https://feeds.example/{feedId}.xml",
            "Daily",
            "https://feeds.example/",
            null,
            FeedViewKind.Article,
            60,
            10,
            true,
            1,
            Now.AddDays(-1),
            Now.AddDays(-1)),
        state);

    private static string Rss(string id) =>
        $"<rss version='2.0'><channel><title>Daily</title><item><guid>{id}</guid><title>{id}</title></item></channel></rss>";

    private static FeedRefreshHttpResponse Response(
        HttpStatusCode status,
        string body,
        string? etag = null,
        string? lastModified = null)
    {
        var message = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/rss+xml")
        };
        if (etag is not null) message.Headers.TryAddWithoutValidation("ETag", etag);
        if (lastModified is not null) message.Content.Headers.TryAddWithoutValidation("Last-Modified", lastModified);
        return new(message);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeResolver : IFeedHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("93.184.216.34")]);
    }

    private sealed class RecordingResolver : IFeedHostResolver
    {
        public List<string> Hosts { get; } = [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Hosts.Add(host);
            return Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("93.184.216.34")]);
        }
    }

    private sealed class FakeFeedEntryWriter : IFeedEntryWriter
    {
        public int Calls { get; private set; }
        public IReadOnlyList<FeedEntry> LastEntries { get; private set; } = [];
        public Exception? Failure { get; init; }

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastEntries = entries;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class FakeFeedAiAutomationQueueService : IFeedAiAutomationQueueService
    {
        public int EnqueueCalls { get; private set; }
        public IReadOnlyList<FeedEntry> LastEntries { get; private set; } = [];
        public Exception? Failure { get; init; }

        public Task EnqueueAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            CancellationToken cancellationToken)
        {
            EnqueueCalls++;
            LastEntries = entries;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public Task<int> ProcessBackgroundBatchAsync(CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class FakeFeedAutomationPlanningService
        : IFeedAutomationPlanningService
    {
        public int StageCalls { get; private set; }
        public FeedCatalogItem? LastFeed { get; private set; }
        public IReadOnlyList<FeedEntry> LastEntries { get; private set; } = [];
        public Exception? Failure { get; init; }

        public Task<FeedAutomationPlanningResult> StageAsync(
            FeedCatalogItem feed,
            IReadOnlyList<FeedEntry> entries,
            CancellationToken cancellationToken)
        {
            StageCalls++;
            LastFeed = feed;
            LastEntries = entries;
            return Failure is null
                ? Task.FromResult(
                    new FeedAutomationPlanningResult(
                        1,
                        entries.Count,
                        entries.Count,
                        entries.Count))
                : Task.FromException<FeedAutomationPlanningResult>(
                    Failure);
        }
    }

    private sealed class FakeRefreshTransport(
        Func<Uri, IReadOnlyList<IPAddress>, FeedRefreshHttpRequest, CancellationToken, Task<FeedRefreshHttpResponse>> send)
        : IFeedRefreshTransport
    {
        public Task<FeedRefreshHttpResponse> SendAsync(
            Uri uri,
            IReadOnlyList<IPAddress> addresses,
            FeedRefreshHttpRequest request,
            CancellationToken cancellationToken) => send(uri, addresses, request, cancellationToken);
    }

    private sealed class FakeFeedFetchStateRepository : IFeedFetchStateRepository
    {
        private readonly ConcurrentDictionary<string, FeedRefreshTarget> _targets;

        public FakeFeedFetchStateRepository(params FeedRefreshTarget?[] targets)
        {
            _targets = new ConcurrentDictionary<string, FeedRefreshTarget>(
                targets
                    .Where(target => target is not null)
                    .Select(target => target!)
                    .ToDictionary(target => target.Feed.Id, StringComparer.Ordinal),
                StringComparer.Ordinal);
        }

        public FeedFetchState? Saved { get; private set; }
        public ConcurrentDictionary<string, FeedFetchState> SavedById { get; } = new(StringComparer.Ordinal);
        public Action? BeforeSave { get; init; }

        public Task<FeedRefreshTarget?> GetTargetAsync(string feedId, CancellationToken cancellationToken) =>
            Task.FromResult(_targets.GetValueOrDefault(feedId));

        public Task<IReadOnlyList<FeedRefreshTarget>> GetAllTargetsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FeedRefreshTarget>>(_targets.Values
                .OrderBy(target => target.Feed.SortOrder)
                .ThenBy(target => target.Feed.Id, StringComparer.Ordinal)
                .ToArray());

        public Task<IReadOnlyList<FeedRefreshTarget>> GetDueTargetsAsync(
            DateTimeOffset now,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FeedRefreshTarget>>(_targets.Values
                .Where(target => target.State?.NextFetchAt is null || target.State.NextFetchAt <= now)
                .Take(maximumCount)
                .ToArray());

        public Task<bool> SaveStateAsync(FeedFetchState state, CancellationToken cancellationToken)
        {
            BeforeSave?.Invoke();
            Saved = state;
            SavedById[state.FeedId] = state;
            if (!_targets.TryGetValue(state.FeedId, out FeedRefreshTarget? target))
                return Task.FromResult(false);
            _targets[state.FeedId] = target with { State = state };
            return Task.FromResult(true);
        }
    }
}
