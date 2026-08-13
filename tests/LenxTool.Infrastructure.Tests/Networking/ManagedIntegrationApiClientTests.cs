using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class ManagedIntegrationApiClientTests
{
    private static readonly EntryIntegrationProbeContext Context = new(
        new Uri("https://integration.example.com/"),
        [IPAddress.Parse("203.0.113.10")]);

    [Fact]
    public async Task OutlineCreatesDeterministicDocumentAfterNotFound()
    {
        var requests = new List<CapturedRequest>();
        var responses = new Queue<HttpResponseMessage>(
        [
            Response(HttpStatusCode.NotFound),
            JsonResponse(
                HttpStatusCode.OK,
                """{"ok":true,"data":{"id":"11111111-1111-4111-8111-111111111111","collectionId":"22222222-2222-4222-8222-222222222222","url":"/doc/test"}}""")
        ]);
        var client = new OutlineApiClient(new StubFactory(
            request => CaptureAndReturnAsync(request, requests, responses)));
        var document = new OutlineDocument(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "Title",
            "Body");

        OutlineDocumentResult result = await client.UpsertAsync(
            Context,
            "outline-secret",
            document,
            CancellationToken.None);

        Assert.Equal(document.Id, result.Id);
        Assert.Equal("/api/documents.info", requests[0].Uri.AbsolutePath);
        Assert.Equal("/api/documents.create", requests[1].Uri.AbsolutePath);
        Assert.All(requests, request =>
            Assert.Equal("Bearer outline-secret", request.Authorization));
        using JsonDocument body = JsonDocument.Parse(requests[1].Body);
        Assert.Equal(
            document.CollectionId.ToString("D"),
            body.RootElement.GetProperty("collectionId").GetString());
        Assert.False(body.RootElement.GetProperty("publish").GetBoolean());
    }

    [Fact]
    public async Task OutlineRefusesToMoveExistingDocumentAcrossCollections()
    {
        Guid documentId = Guid.Parse(
            "11111111-1111-4111-8111-111111111111");
        Guid allowedCollectionId = Guid.Parse(
            "22222222-2222-4222-8222-222222222222");
        Guid existingCollectionId = Guid.Parse(
            "33333333-3333-4333-8333-333333333333");
        int calls = 0;
        var client = new OutlineApiClient(new StubFactory(_ =>
        {
            calls++;
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    ok = true,
                    data = new
                    {
                        id = documentId.ToString("D"),
                        collectionId = existingCollectionId.ToString("D")
                    }
                })));
        }));

        OutlineApiException error = await Assert.ThrowsAsync<OutlineApiException>(
            () => client.UpsertAsync(
                Context,
                "outline-secret",
                new(documentId, allowedCollectionId, "Title", "Body"),
                CancellationToken.None));

        Assert.Equal(OutlineApiFailure.Conflict, error.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OutlineMalformedWriteReceiptHasUnknownOutcome()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            Response(HttpStatusCode.NotFound),
            JsonResponse(HttpStatusCode.OK, """{"ok":true,"data":{}}""")
        ]);
        var client = new OutlineApiClient(new StubFactory(
            _ => Task.FromResult(responses.Dequeue())));

        OutlineApiException error = await Assert.ThrowsAsync<OutlineApiException>(
            () => client.UpsertAsync(
                Context,
                "outline-secret",
                new(
                    Guid.Parse("11111111-1111-4111-8111-111111111111"),
                    Guid.Parse("22222222-2222-4222-8222-222222222222"),
                    "Title",
                    "Body"),
                CancellationToken.None));

        Assert.Equal(OutlineApiFailure.UnknownWriteOutcome, error.Failure);
    }

    [Fact]
    public async Task ReadeckCreatesThenPatchesArchiveWithStableLabel()
    {
        var requests = new List<CapturedRequest>();
        HttpResponseMessage created = Response(HttpStatusCode.Accepted);
        created.Headers.TryAddWithoutValidation("Bookmark-Id", "bookmark-1");
        created.Headers.Location = new Uri("/api/bookmarks/bookmark-1", UriKind.Relative);
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(HttpStatusCode.OK, "[]", ("Total-Count", "0")),
            created,
            JsonResponse(HttpStatusCode.OK, """{"id":"bookmark-1","updated":"2026-08-13T00:00:00Z","href":"/api/bookmarks/bookmark-1"}""")
        ]);
        var client = new ReadeckApiClient(new StubFactory(
            request => CaptureAndReturnAsync(request, requests, responses)));
        var bookmark = new ReadeckBookmark(
            "lenxtool:0123456789abcdef01234567",
            new Uri("https://news.example.com/a"),
            "A title",
            ["lenxtool:0123456789abcdef01234567", "research"],
            IsArchived: true);

        ReadeckBookmarkResult result = await client.UpsertAsync(
            Context,
            "readeck-token",
            bookmark,
            CancellationToken.None);

        Assert.Equal("bookmark-1", result.Id);
        Assert.Contains(
            Uri.EscapeDataString(bookmark.StableLabel),
            requests[0].Uri.Query,
            StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Equal(HttpMethod.Patch, requests[2].Method);
        using JsonDocument update = JsonDocument.Parse(requests[2].Body);
        Assert.True(update.RootElement.GetProperty("is_archived").GetBoolean());
        Assert.Contains(
            update.RootElement.GetProperty("labels").EnumerateArray(),
            value => value.GetString() == bookmark.StableLabel);
    }

    [Fact]
    public async Task ReadeckRejectsAmbiguousStableLabel()
    {
        const string label = "lenxtool:0123456789abcdef01234567";
        string body = $$"""[{"id":"one","href":"/one","labels":["{{label}}"]},{"id":"two","href":"/two","labels":["{{label}}"]}]""";
        var client = new ReadeckApiClient(new StubFactory(
            _ => Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                body,
                ("Total-Count", "2")))));

        ReadeckApiException error = await Assert.ThrowsAsync<ReadeckApiException>(
            () => client.UpsertAsync(
                Context,
                "token",
                new(
                    label,
                    new Uri("https://news.example.com/a"),
                    "Title",
                    [label],
                    false),
                CancellationToken.None));

        Assert.Equal(ReadeckApiFailure.Conflict, error.Failure);
    }

    [Fact]
    public async Task ReadeckReadBodyTimeoutIsTransient()
    {
        var client = new ReadeckApiClient(
            new StubFactory(_ => Task.FromResult(BlockingJsonResponse())),
            TimeSpan.FromMilliseconds(20));

        ReadeckApiException error = await Assert.ThrowsAsync<ReadeckApiException>(
            () => client.ProbeAsync(Context, "token", CancellationToken.None));

        Assert.Equal(ReadeckApiFailure.Unavailable, error.Failure);
    }

    [Fact]
    public async Task ReadeckWriteBodyTimeoutHasUnknownOutcome()
    {
        const string label = "lenxtool:0123456789abcdef01234567";
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(
                HttpStatusCode.OK,
                $$"""[{"id":"one","href":"/api/bookmarks/one","labels":["{{label}}"]}]""",
                ("Total-Count", "1")),
            BlockingJsonResponse()
        ]);
        var client = new ReadeckApiClient(
            new StubFactory(_ => Task.FromResult(responses.Dequeue())),
            TimeSpan.FromMilliseconds(20));

        ReadeckApiException error = await Assert.ThrowsAsync<ReadeckApiException>(
            () => client.UpsertAsync(
                Context,
                "token",
                new(
                    label,
                    new Uri("https://news.example.com/a"),
                    "Title",
                    [label],
                    false),
                CancellationToken.None));

        Assert.Equal(ReadeckApiFailure.UnknownWriteOutcome, error.Failure);
    }

    [Fact]
    public async Task ReadeckDuplicateReceiptHeaderHasUnknownOutcome()
    {
        const string label = "lenxtool:0123456789abcdef01234567";
        HttpResponseMessage created = Response(HttpStatusCode.Accepted);
        created.Headers.TryAddWithoutValidation(
            "Bookmark-Id",
            ["bookmark-1", "bookmark-2"]);
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(HttpStatusCode.OK, "[]", ("Total-Count", "0")),
            created
        ]);
        var client = new ReadeckApiClient(new StubFactory(
            _ => Task.FromResult(responses.Dequeue())));

        ReadeckApiException error = await Assert.ThrowsAsync<ReadeckApiException>(
            () => client.UpsertAsync(
                Context,
                "token",
                new(
                    label,
                    new Uri("https://news.example.com/a"),
                    "Title",
                    [label],
                    false),
                CancellationToken.None));

        Assert.Equal(ReadeckApiFailure.UnknownWriteOutcome, error.Failure);
    }

    [Fact]
    public async Task ReadeckDuplicateTotalCountIsUnavailable()
    {
        const string label = "lenxtool:0123456789abcdef01234567";
        HttpResponseMessage found = JsonResponse(
            HttpStatusCode.OK,
            $$"""[{"id":"one","href":"/api/bookmarks/one","labels":["{{label}}"]}]""");
        found.Headers.TryAddWithoutValidation("Total-Count", ["1", "2"]);
        var client = new ReadeckApiClient(new StubFactory(
            _ => Task.FromResult(found)));

        ReadeckApiException error = await Assert.ThrowsAsync<ReadeckApiException>(
            () => client.UpsertAsync(
                Context,
                "token",
                new(
                    label,
                    new Uri("https://news.example.com/a"),
                    "Title",
                    [label],
                    false),
                CancellationToken.None));

        Assert.Equal(ReadeckApiFailure.Unavailable, error.Failure);
    }

    [Fact]
    public async Task ReadeckEmptyPageWithNonzeroTotalNeverCreates()
    {
        const string label = "lenxtool:0123456789abcdef01234567";
        int calls = 0;
        var client = new ReadeckApiClient(new StubFactory(_ =>
        {
            calls++;
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                "[]",
                ("Total-Count", "1")));
        }));

        ReadeckApiException error = await Assert.ThrowsAsync<ReadeckApiException>(
            () => client.UpsertAsync(
                Context,
                "token",
                new(
                    label,
                    new Uri("https://news.example.com/a"),
                    "Title",
                    [label],
                    false),
                CancellationToken.None));

        Assert.Equal(ReadeckApiFailure.Unavailable, error.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReadeckMalformedLabelProjectionNeverCreates()
    {
        const string label = "lenxtool:0123456789abcdef01234567";
        int calls = 0;
        var client = new ReadeckApiClient(new StubFactory(_ =>
        {
            calls++;
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                """[{"id":"one","href":"/api/bookmarks/one","labels":"invalid"}]""",
                ("Total-Count", "1")));
        }));

        ReadeckApiException error = await Assert.ThrowsAsync<ReadeckApiException>(
            () => client.UpsertAsync(
                Context,
                "token",
                new(
                    label,
                    new Uri("https://news.example.com/a"),
                    "Title",
                    [label],
                    false),
                CancellationToken.None));

        Assert.Equal(ReadeckApiFailure.Unavailable, error.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReadeckWriteReceiptWithForeignLocationHasUnknownOutcome()
    {
        const string label = "lenxtool:0123456789abcdef01234567";
        HttpResponseMessage created = Response(HttpStatusCode.Accepted);
        created.Headers.TryAddWithoutValidation("Bookmark-Id", "bookmark-1");
        created.Headers.Location = new Uri(
            "https://other.example.com/api/bookmarks/bookmark-1");
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(HttpStatusCode.OK, "[]", ("Total-Count", "0")),
            created
        ]);
        var client = new ReadeckApiClient(new StubFactory(
            _ => Task.FromResult(responses.Dequeue())));

        ReadeckApiException error = await Assert.ThrowsAsync<ReadeckApiException>(
            () => client.UpsertAsync(
                Context,
                "token",
                new(
                    label,
                    new Uri("https://news.example.com/a"),
                    "Title",
                    [label],
                    false),
                CancellationToken.None));

        Assert.Equal(ReadeckApiFailure.UnknownWriteOutcome, error.Failure);
    }

    [Fact]
    public async Task WebhookRequiresCapabilityAndSignsExactBody()
    {
        var requests = new List<CapturedRequest>();
        HttpResponseMessage capability = Response(HttpStatusCode.NoContent);
        capability.Headers.TryAddWithoutValidation("LenxTool-Webhook-Version", "1");
        capability.Headers.TryAddWithoutValidation("LenxTool-Idempotency", "required");
        HttpResponseMessage accepted = Response(HttpStatusCode.Accepted);
        accepted.Headers.TryAddWithoutValidation(
            "LenxTool-Ack",
            new string('a', 64));
        var responses = new Queue<HttpResponseMessage>([capability, accepted]);
        var client = new WebhookApiClient(new StubFactory(
            request => CaptureAndReturnAsync(request, requests, responses)));
        var payload = new WebhookEntryPayload(
            new string('a', 64),
            "entry-1",
            "Title",
            new Uri("https://news.example.com/a"),
            "Ada",
            DateTimeOffset.Parse(
                "2026-08-13T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            "Summary",
            ["research"],
            EntryViewKind.Article);

        await client.ProbeAsync(Context, CancellationToken.None);
        await client.SendAsync(Context, "hmac-secret", payload, CancellationToken.None);

        Assert.Equal(HttpMethod.Options, requests[0].Method);
        Assert.Equal(payload.EventId, requests[1].IdempotencyKey);
        string expected = "sha256=" + Convert.ToHexString(
                System.Security.Cryptography.HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes("hmac-secret"),
                    requests[1].Body))
            .ToLowerInvariant();
        Assert.Equal(expected, requests[1].Signature);
        using JsonDocument json = JsonDocument.Parse(requests[1].Body);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("entry.exported", json.RootElement.GetProperty("event").GetString());
        Assert.False(json.RootElement.GetProperty("entry").TryGetProperty("content", out _));
    }

    [Fact]
    public async Task WebhookMissingCapabilityStopsBeforePost()
    {
        int calls = 0;
        var client = new WebhookApiClient(new StubFactory(_ =>
        {
            calls++;
            return Task.FromResult(Response(HttpStatusCode.NoContent));
        }));

        WebhookApiException error = await Assert.ThrowsAsync<WebhookApiException>(
            () => client.ProbeAsync(Context, CancellationToken.None));

        Assert.Equal(WebhookApiFailure.CapabilityMissing, error.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WebhookDuplicateCapabilityHeaderFailsClosed()
    {
        HttpResponseMessage capability = Response(HttpStatusCode.NoContent);
        capability.Headers.TryAddWithoutValidation(
            "LenxTool-Webhook-Version",
            ["1", "1"]);
        capability.Headers.TryAddWithoutValidation(
            "LenxTool-Idempotency",
            "required");
        var client = new WebhookApiClient(new StubFactory(
            _ => Task.FromResult(capability)));

        WebhookApiException error = await Assert.ThrowsAsync<WebhookApiException>(
            () => client.ProbeAsync(Context, CancellationToken.None));

        Assert.Equal(WebhookApiFailure.CapabilityMissing, error.Failure);
    }

    [Fact]
    public async Task WebhookDuplicateAckHasUnknownOutcome()
    {
        HttpResponseMessage capability = Response(HttpStatusCode.NoContent);
        capability.Headers.TryAddWithoutValidation(
            "LenxTool-Webhook-Version",
            "1");
        capability.Headers.TryAddWithoutValidation(
            "LenxTool-Idempotency",
            "required");
        HttpResponseMessage accepted = Response(HttpStatusCode.Accepted);
        accepted.Headers.TryAddWithoutValidation(
            "LenxTool-Ack",
            [new string('a', 64), new string('a', 64)]);
        var responses = new Queue<HttpResponseMessage>([capability, accepted]);
        var client = new WebhookApiClient(new StubFactory(
            _ => Task.FromResult(responses.Dequeue())));
        var payload = new WebhookEntryPayload(
            new string('a', 64),
            "entry-1",
            "Title",
            new Uri("https://news.example.com/a"),
            null,
            null,
            "Summary",
            [],
            EntryViewKind.Article);

        await client.ProbeAsync(Context, CancellationToken.None);
        WebhookApiException error = await Assert.ThrowsAsync<WebhookApiException>(
            () => client.SendAsync(
                Context,
                null,
                payload,
                CancellationToken.None));

        Assert.Equal(WebhookApiFailure.UnknownWriteOutcome, error.Failure);
    }

    [Fact]
    public async Task WebhookRejectsChunkedCapabilityBodyOverBudget()
    {
        HttpResponseMessage capability = Response(HttpStatusCode.NoContent);
        capability.Headers.TryAddWithoutValidation(
            "LenxTool-Webhook-Version",
            "1");
        capability.Headers.TryAddWithoutValidation(
            "LenxTool-Idempotency",
            "required");
        capability.Content = new UnknownLengthContent(new byte[4097]);
        var client = new WebhookApiClient(new StubFactory(
            _ => Task.FromResult(capability)));

        WebhookApiException error = await Assert.ThrowsAsync<WebhookApiException>(
            () => client.ProbeAsync(Context, CancellationToken.None));

        Assert.Equal(WebhookApiFailure.CapabilityMissing, error.Failure);
    }

    [Fact]
    public async Task BoundedReaderTimesOutAfterChunkedResponseHeaders()
    {
        using var content = new StreamContent(new BlockingReadStream());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedHttpContent.ReadAsByteArrayAsync(
                content,
                4096,
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None));
    }

    [Fact]
    public async Task TorrentFetcherClassifiesTemporaryServerFailure()
    {
        var fetcher = new TorrentFileFetcher(
            new StaticResolver(IPAddress.Parse("8.8.8.8")),
            new StubTorrentTransport(Response(HttpStatusCode.ServiceUnavailable)));

        TorrentFileFetchException error =
            await Assert.ThrowsAsync<TorrentFileFetchException>(
                () => fetcher.FetchAsync(
                    new(
                        "https://downloads.example.com/file.torrent",
                        "application/x-bittorrent",
                        1024,
                        null),
                    CancellationToken.None));

        Assert.Equal(TorrentFileFetchFailure.Unavailable, error.Failure);
    }

    [Fact]
    public async Task TorrentFetcherRejectsPrivateDnsBeforeTransport()
    {
        var transport = new StubTorrentTransport(Response(HttpStatusCode.OK));
        var fetcher = new TorrentFileFetcher(
            new StaticResolver(IPAddress.Loopback),
            transport);

        TorrentFileFetchException error =
            await Assert.ThrowsAsync<TorrentFileFetchException>(
                () => fetcher.FetchAsync(
                    new(
                        "https://downloads.example.com/file.torrent",
                        "application/x-bittorrent",
                        1024,
                        null),
                    CancellationToken.None));

        Assert.Equal(TorrentFileFetchFailure.AccessDenied, error.Failure);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task QBittorrentRequiresSupportedVersionAndExistingCategory()
    {
        var requests = new List<CapturedRequest>();
        var responses = new Queue<HttpResponseMessage>(
        [
            TextResponse(HttpStatusCode.OK, "2.14.1"),
            JsonResponse(HttpStatusCode.OK, """{"downloads":{"name":"downloads"}}"""),
            JsonResponse(HttpStatusCode.OK, "[]"),
            JsonResponse(
                HttpStatusCode.OK,
                """{"success_count":1,"pending_count":0,"failure_count":0,"added_torrent_ids":["0123456789abcdef0123456789abcdef01234567"]}"""),
            JsonResponse(
                HttpStatusCode.OK,
                """[{"hash":"0123456789abcdef0123456789abcdef01234567","category":"downloads"}]""")
        ]);
        var client = new QBittorrentApiClient(new StubFactory(
            request => CaptureAndReturnAsync(request, requests, responses)));
        var source = MagnetUriValidator.Validate(
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&tr=https%3A%2F%2Ftracker.example.com%2Fsecret");

        await client.AddAsync(
            Context,
            "qbt_1234567890123456789012345678",
            source,
            "downloads",
            CancellationToken.None);

        Assert.Equal("/api/v2/app/webapiVersion", requests[0].Uri.AbsolutePath);
        Assert.Equal("/api/v2/torrents/categories", requests[1].Uri.AbsolutePath);
        Assert.Equal("/api/v2/torrents/info", requests[2].Uri.AbsolutePath);
        Assert.Equal("/api/v2/torrents/add", requests[3].Uri.AbsolutePath);
        Assert.Equal("/api/v2/torrents/info", requests[4].Uri.AbsolutePath);
        Assert.Contains("name=category", requests[3].Content, StringComparison.Ordinal);
        Assert.Contains("downloads", requests[3].Content, StringComparison.Ordinal);
        Assert.Contains("name=urls", requests[3].Content, StringComparison.Ordinal);
        Assert.All(requests, request => Assert.StartsWith("Bearer qbt_", request.Authorization, StringComparison.Ordinal));
    }

    [Fact]
    public async Task QBittorrentDoesNotCompletePendingAddWithoutVerification()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var responses = new Queue<HttpResponseMessage>(
        [
            TextResponse(HttpStatusCode.OK, "2.14.1"),
            JsonResponse(HttpStatusCode.OK, """{"downloads":{"name":"downloads"}}"""),
            JsonResponse(HttpStatusCode.OK, "[]"),
            JsonResponse(
                HttpStatusCode.Accepted,
                """{"success_count":0,"pending_count":1,"failure_count":0,"added_torrent_ids":[]}"""),
            JsonResponse(HttpStatusCode.OK, "[]")
        ]);
        var client = new QBittorrentApiClient(new StubFactory(
            _ => Task.FromResult(responses.Dequeue())));

        QBittorrentApiException error = await Assert.ThrowsAsync<QBittorrentApiException>(
            () => client.AddAsync(
                Context,
                "qbt_1234567890123456789012345678",
                MagnetUriValidator.Validate($"magnet:?xt=urn:btih:{hash}"),
                "downloads",
                CancellationToken.None));

        Assert.Equal(QBittorrentApiFailure.UnknownWriteOutcome, error.Failure);
    }

    [Fact]
    public async Task QBittorrentExistingMatchingTorrentIsIdempotent()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var requests = new List<CapturedRequest>();
        var responses = new Queue<HttpResponseMessage>(
        [
            TextResponse(HttpStatusCode.OK, "2.14.1"),
            JsonResponse(HttpStatusCode.OK, """{"downloads":{"name":"downloads"}}"""),
            JsonResponse(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new[]
                {
                    new { hash, category = "downloads" }
                }))
        ]);
        var client = new QBittorrentApiClient(new StubFactory(
            request => CaptureAndReturnAsync(request, requests, responses)));

        await client.AddAsync(
            Context,
            "qbt_1234567890123456789012345678",
            MagnetUriValidator.Validate($"magnet:?xt=urn:btih:{hash}"),
            "downloads",
            CancellationToken.None);

        Assert.Equal(3, requests.Count);
        Assert.DoesNotContain(
            requests,
            request => request.Uri.AbsolutePath == "/api/v2/torrents/add");
    }

    [Fact]
    public async Task QBittorrentRejectsMismatchedAddReceipt()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var responses = new Queue<HttpResponseMessage>(
        [
            TextResponse(HttpStatusCode.OK, "2.14.1"),
            JsonResponse(HttpStatusCode.OK, """{"downloads":{"name":"downloads"}}"""),
            JsonResponse(HttpStatusCode.OK, "[]"),
            JsonResponse(
                HttpStatusCode.OK,
                """{"success_count":1,"pending_count":0,"failure_count":0,"added_torrent_ids":["ffffffffffffffffffffffffffffffffffffffff"]}""")
        ]);
        var client = new QBittorrentApiClient(new StubFactory(
            _ => Task.FromResult(responses.Dequeue())));

        QBittorrentApiException error = await Assert.ThrowsAsync<QBittorrentApiException>(
            () => client.AddAsync(
                Context,
                "qbt_1234567890123456789012345678",
                MagnetUriValidator.Validate($"magnet:?xt=urn:btih:{hash}"),
                "downloads",
                CancellationToken.None));

        Assert.Equal(QBittorrentApiFailure.UnknownWriteOutcome, error.Failure);
    }

    [Fact]
    public async Task QBittorrentRejectsOlderWebApiBeforeMutation()
    {
        int calls = 0;
        var client = new QBittorrentApiClient(new StubFactory(_ =>
        {
            calls++;
            return Task.FromResult(TextResponse(HttpStatusCode.OK, "2.14.0"));
        }));

        QBittorrentApiException error = await Assert.ThrowsAsync<QBittorrentApiException>(
            () => client.AddAsync(
                Context,
                "qbt_1234567890123456789012345678",
                MagnetUriValidator.Validate(
                    "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
                "downloads",
                CancellationToken.None));

        Assert.Equal(QBittorrentApiFailure.UnsupportedVersion, error.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void MagnetValidationAcceptsHexAndBase32ButRejectsAmbiguity()
    {
        QBittorrentMagnetSource hex = MagnetUriValidator.Validate(
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567");
        QBittorrentMagnetSource base32 = MagnetUriValidator.Validate(
            "magnet:?xt=urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        Assert.Equal(40, hex.InfoHash.Length);
        Assert.Equal(new string('0', 40), base32.InfoHash);
        Assert.Throws<ArgumentException>(() => MagnetUriValidator.Validate(
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&xt=urn:btih:1111111111111111111111111111111111111111"));
        Assert.Throws<ArgumentException>(() => MagnetUriValidator.Validate(
            "https://example.com/file.torrent"));
    }

    [Fact]
    public void TorrentMetainfoValidationHashesCanonicalInfoAndRejectsMalformedData()
    {
        byte[] valid = Encoding.ASCII.GetBytes(
            "d4:infod6:lengthi1e4:name4:testee");

        QBittorrentFileSource first = TorrentMetainfoValidator.Validate(valid);
        QBittorrentFileSource second = TorrentMetainfoValidator.Validate(valid);

        Assert.Equal(40, first.InfoHash.Length);
        Assert.Equal(first.InfoHash, second.InfoHash);
        Assert.Throws<ArgumentException>(() => TorrentMetainfoValidator.Validate(
            Encoding.ASCII.GetBytes("d4:infod4:name4:test6:lengthi1eee")));
        Assert.Throws<ArgumentException>(() => TorrentMetainfoValidator.Validate(
            Encoding.ASCII.GetBytes("d3:foo3:bare")));
    }

    private static async Task<HttpResponseMessage> CaptureAndReturnAsync(
        HttpRequestMessage request,
        List<CapturedRequest> requests,
        Queue<HttpResponseMessage> responses)
    {
        byte[] body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync();
        requests.Add(new(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? keys)
                ? keys.Single()
                : null,
            request.Headers.TryGetValues("X-LenxTool-Signature", out IEnumerable<string>? signatures)
                ? signatures.Single()
                : null,
            body,
            request.Content is null
                ? string.Empty
                : Encoding.UTF8.GetString(body)));
        return responses.Dequeue();
    }

    private static HttpResponseMessage Response(HttpStatusCode status) =>
        new(status) { Content = new ByteArrayContent([]) };

    private static HttpResponseMessage BlockingJsonResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingReadStream())
        };

    private static HttpResponseMessage TextResponse(
        HttpStatusCode status,
        string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        string body,
        params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        foreach ((string name, string value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    private sealed class StubFactory(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : IIntegrationHttpClientFactory
    {
        public HttpClient Create(EntryIntegrationProbeContext context) =>
            new(new StubHandler(send), disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }

    private sealed class StaticResolver(params IPAddress[] addresses)
        : IFeedHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(addresses);
    }

    private sealed class StubTorrentTransport(HttpResponseMessage response)
        : ITorrentFileTransport
    {
        public int CallCount { get; private set; }

        public Task<TorrentFileHttpResponse> GetAsync(
            Uri uri,
            IReadOnlyList<IPAddress> addresses,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new TorrentFileHttpResponse(response));
        }
    }

    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? IdempotencyKey,
        string? Signature,
        byte[] Body,
        string Content);
}
