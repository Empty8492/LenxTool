using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

/// <summary>
/// 冻结 Zotero Web API v3 的最小安全契约：凭据只进请求头、固定官方个人库、
/// 确定性 key 写入前后均校验身份，并把第三方正文封闭在适配器边界内。
/// </summary>
public sealed class ZoteroApiClientTests
{
    private static readonly IPAddress PublicAddress =
        IPAddress.Parse("104.20.20.31");
    private static readonly Uri ApiRoot =
        new("https://api.zotero.org/users/12345678/");
    private const string ApiKey = "ABCDEF23456789ABCDEF2345";

    [Fact]
    public void ProductionHandlerPinsOnePublicTargetWithoutRedirectProxyCookieOrDecompression()
    {
        using SocketsHttpHandler handler =
            ZoteroHttpClientSecurity.CreatePrimaryHandler(
                ApiRoot,
                [PublicAddress]);

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(1, handler.MaxConnectionsPerServer);
    }

    [Theory]
    [InlineData("http://api.zotero.org/users/123/")]
    [InlineData("https://www.zotero.org/users/123/")]
    [InlineData("https://api.zotero.org/groups/123/")]
    [InlineData("https://api.zotero.org/users/0/")]
    [InlineData("https://api.zotero.org/users/-1/")]
    [InlineData("https://api.zotero.org/users/123")]
    [InlineData("https://api.zotero.org/users/123/items")]
    [InlineData("https://api.zotero.org:444/users/123/")]
    [InlineData("https://user@api.zotero.org/users/123/")]
    [InlineData("https://api.zotero.org/users/123/?key=secret")]
    [InlineData("https://api.zotero.org/users/123/#fragment")]
    public void TargetRequiresExactOfficialPositiveUserLibraryRoot(string value)
    {
        var target = new ZoteroApiTarget(new(value), false, false);

        Assert.Throws<ArgumentException>(
            () => ZoteroApiClient.ValidateTarget(target));
    }

    [Fact]
    public async Task ProbeUsesVersionAndKeyHeadersAndVerifiesSelectedPermissions()
    {
        var requests = new List<RequestSnapshot>();
        var factory = new StubClientFactory(async (request, _) =>
        {
            requests.Add(await RequestSnapshot.CreateAsync(request));
            return JsonResponse(new
            {
                userID = 12345678,
                access = new
                {
                    user = new
                    {
                        library = true,
                        write = true,
                        notes = true,
                        files = true
                    }
                }
            });
        });
        var resolver = new RecordingResolver([PublicAddress]);
        var client = CreateClient(factory, resolver);

        ZoteroApiCapability capability = await client.ProbeAsync(
            new(ApiRoot, true, true),
            ApiKey,
            CancellationToken.None);

        Assert.Equal(12345678, capability.UserId);
        Assert.True(capability.CanWrite);
        Assert.True(capability.CanWriteNotes);
        Assert.True(capability.CanWriteFiles);
        RequestSnapshot request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.zotero.org/keys/current", request.Uri.AbsoluteUri);
        Assert.Equal("3", Assert.Single(request.Header("Zotero-API-Version")));
        Assert.Equal(ApiKey, Assert.Single(request.Header("Zotero-API-Key")));
        Assert.Empty(request.Header("Authorization"));
        Assert.DoesNotContain("key", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["api.zotero.org"], resolver.Hosts);
        Assert.Equal([PublicAddress], factory.LastPinnedAddresses);
    }

    [Theory]
    [InlineData(12345679, true, true, true)]
    [InlineData(12345678, false, true, true)]
    [InlineData(12345678, true, false, true)]
    [InlineData(12345678, true, true, false)]
    public async Task ProbeFailsClosedForWrongUserOrMissingSelectedPermission(
        long userId,
        bool write,
        bool notes,
        bool files)
    {
        var factory = new StubClientFactory((_, _) => Task.FromResult(
            JsonResponse(new
            {
                userID = userId,
                access = new
                {
                    user = new
                    {
                        library = true,
                        write,
                        notes,
                        files
                    }
                },
                providerPrivateDetail = "do-not-expose"
            })));
        var client = CreateClient(factory);

        ZoteroApiException exception =
            await Assert.ThrowsAsync<ZoteroApiException>(
                () => client.ProbeAsync(
                    new(ApiRoot, true, true),
                    ApiKey,
                    CancellationToken.None));

        Assert.Equal(ZoteroApiFailure.Unauthorized, exception.Failure);
        Assert.False(exception.IsRetryable);
        Assert.DoesNotContain(
            "do-not-expose",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeDoesNotRequireOptionalPermissionFieldsWhenOptionsAreDisabled()
    {
        var factory = new StubClientFactory(_ => JsonResponse(new
        {
            userID = 12345678,
            access = new
            {
                user = new
                {
                    library = true,
                    write = true
                }
            }
        }));
        var client = CreateClient(factory);

        ZoteroApiCapability capability = await client.ProbeAsync(
            new(ApiRoot, false, false),
            ApiKey,
            CancellationToken.None);

        Assert.True(capability.CanWrite);
        Assert.False(capability.CanWriteNotes);
        Assert.False(capability.CanWriteFiles);
    }

    [Fact]
    public async Task PinnedProbeReusesHealthAddressesWithoutResolvingAgain()
    {
        var resolver = new RecordingResolver(
            _ => throw new InvalidOperationException("DNS must not run"));
        var factory = new StubClientFactory((_, _) => Task.FromResult(
            JsonResponse(new
            {
                userID = 12345678,
                access = new
                {
                    user = new
                    {
                        library = true,
                        write = true,
                        notes = false,
                        files = false
                    }
                }
            })));
        var client = CreateClient(factory, resolver);

        ZoteroApiCapability result = await client.ProbePinnedAsync(
            new(ApiRoot, false, false),
            ApiKey,
            [PublicAddress],
            CancellationToken.None);

        Assert.Equal(12345678, result.UserId);
        Assert.Empty(resolver.Hosts);
        Assert.Equal([PublicAddress], factory.LastPinnedAddresses);
    }

    [Theory]
    [InlineData("10.0.0.8")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.0.2.10")]
    [InlineData("::1")]
    public async Task ResolveOrPinnedContextRejectsEveryNonPublicAddress(string value)
    {
        IPAddress unsafeAddress = IPAddress.Parse(value);
        var factory = new StubClientFactory((_, _) =>
            throw new InvalidOperationException("HTTP must not run"));
        var client = CreateClient(factory, new RecordingResolver([unsafeAddress]));

        ZoteroApiException resolved =
            await Assert.ThrowsAsync<ZoteroApiException>(
                () => client.ProbeAsync(
                    new(ApiRoot, false, false),
                    ApiKey,
                    CancellationToken.None));
        ZoteroApiException pinned =
            await Assert.ThrowsAsync<ZoteroApiException>(
                () => client.ProbePinnedAsync(
                    new(ApiRoot, false, false),
                    ApiKey,
                    [unsafeAddress],
                    CancellationToken.None));

        Assert.Equal(ZoteroApiFailure.BlockedEndpoint, resolved.Failure);
        Assert.Equal(ZoteroApiFailure.BlockedEndpoint, pinned.Failure);
        Assert.Equal(0, factory.CreateCount);
    }

    [Theory]
    [InlineData("successful")]
    [InlineData("success")]
    public async Task CreatePostsParentThenOptionalNoteWithOfficialVersionZero(
        string successProperty)
    {
        ZoteroItem parent = ParentItem();
        ZoteroItem note = NoteItem(parent.Key);
        bool created = false;
        RequestSnapshot? post = null;
        var factory = new StubClientFactory(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                string key = request.RequestUri!.Segments[^1];
                return created
                    ? ExistingItemResponse(key == parent.Key ? parent : note)
                    : new(HttpStatusCode.NotFound);
            }

            post = await RequestSnapshot.CreateAsync(request);
            created = true;
            string json = successProperty == "successful"
                ? "{\"successful\":{\"0\":{},\"1\":{}},\"failed\":{}}"
                : "{\"success\":{\"0\":{},\"1\":{}},\"failed\":{}}";
            return JsonTextResponse(json);
        });
        var client = CreateClient(factory);

        IReadOnlyList<string> keys = await client.CreateAsync(
            new(ApiRoot, true, false),
            ApiKey,
            [parent, note],
            CancellationToken.None);

        Assert.Equal([parent.Key, note.Key], keys);
        Assert.NotNull(post);
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal(
            "https://api.zotero.org/users/12345678/items",
            post.Uri.AbsoluteUri);
        Assert.Equal("3", Assert.Single(post.Header("Zotero-API-Version")));
        Assert.Equal(ApiKey, Assert.Single(post.Header("Zotero-API-Key")));
        Assert.Empty(post.Header("Zotero-Write-Token"));
        Assert.Empty(post.Header("If-Unmodified-Since-Version"));
        using JsonDocument body = JsonDocument.Parse(post.Body!);
        JsonElement items = body.RootElement;
        Assert.Equal(2, items.GetArrayLength());
        JsonElement parentJson = items[0];
        JsonElement noteJson = items[1];
        Assert.Equal(parent.Key, parentJson.GetProperty("key").GetString());
        Assert.Equal(0, parentJson.GetProperty("version").GetInt32());
        Assert.Equal("webpage", parentJson.GetProperty("itemType").GetString());
        JsonElement creator = Assert.Single(
            parentJson.GetProperty("creators").EnumerateArray());
        Assert.Equal("author", creator.GetProperty("creatorType").GetString());
        Assert.Equal("Ada Lovelace", creator.GetProperty("name").GetString());
        Assert.False(creator.TryGetProperty("firstName", out _));
        Assert.False(creator.TryGetProperty("lastName", out _));
        Assert.Contains(
            parent.LenxToolMarker,
            parentJson.GetProperty("extra").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(note.Key, noteJson.GetProperty("key").GetString());
        Assert.Equal(0, noteJson.GetProperty("version").GetInt32());
        Assert.Equal(parent.Key, noteJson.GetProperty("parentItem").GetString());
        Assert.Contains(
            note.LenxToolMarker,
            noteJson.GetProperty("note").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAllowsOneImportedFileAttachmentAfterItsParent()
    {
        ZoteroItem parent = ParentItem();
        ZoteroItem note = NoteItem(parent.Key);
        ZoteroItem attachment = AttachmentItem(parent.Key);
        bool created = false;
        RequestSnapshot? post = null;
        var itemsByKey = new Dictionary<string, ZoteroItem>(StringComparer.Ordinal)
        {
            [parent.Key] = parent,
            [note.Key] = note,
            [attachment.Key] = attachment
        };
        var factory = new StubClientFactory(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                string key = request.RequestUri!.Segments[^1];
                return created
                    ? ExistingItemResponse(itemsByKey[key])
                    : new(HttpStatusCode.NotFound);
            }
            post = await RequestSnapshot.CreateAsync(request);
            created = true;
            return JsonTextResponse(
                "{\"successful\":{\"0\":{},\"1\":{},\"2\":{}},\"failed\":{}}");
        });
        var client = CreateClient(factory);

        IReadOnlyList<string> keys = await client.CreateAsync(
            new(ApiRoot, true, true),
            ApiKey,
            [parent, note, attachment],
            CancellationToken.None);

        Assert.Equal([parent.Key, note.Key, attachment.Key], keys);
        Assert.NotNull(post);
        using JsonDocument body = JsonDocument.Parse(post.Body!);
        JsonElement file = body.RootElement[2];
        Assert.Equal("attachment", file.GetProperty("itemType").GetString());
        Assert.Equal("imported_file", file.GetProperty("linkMode").GetString());
        Assert.Equal(parent.Key, file.GetProperty("parentItem").GetString());
        Assert.Equal("image.png", file.GetProperty("filename").GetString());
        Assert.Equal("image/png", file.GetProperty("contentType").GetString());
        Assert.Contains(
            attachment.LenxToolMarker,
            file.GetProperty("note").GetString(),
            StringComparison.Ordinal);
        Assert.False(file.TryGetProperty("md5", out _));
        Assert.False(file.TryGetProperty("mtime", out _));
    }

    [Fact]
    public async Task UploadAttachmentRunsOfficialThreeStagesAndNeverLeaksKeyToStorage()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("abc");
        var requests = new List<RequestSnapshot>();
        var factory = new StubClientFactory(async (request, _) =>
        {
            RequestSnapshot snapshot = await RequestSnapshot.CreateAsync(request);
            requests.Add(snapshot);
            if (request.RequestUri!.Host == "upload.example.net")
            {
                return new(HttpStatusCode.Created);
            }
            if (requests.Count == 1)
            {
                var response = JsonResponse(new
                {
                    url = "https://upload.example.net/signed/object?X-Amz-Signature=abc123",
                    contentType = "multipart/form-data; boundary=ZoteroBoundary",
                    prefix = "--ZoteroBoundary\r\n\r\n",
                    suffix = "\r\n--ZoteroBoundary--\r\n",
                    uploadKey = "opaque-upload-key"
                });
                response.Headers.TryAddWithoutValidation("Backoff", "5");
                return response;
            }
            return new(HttpStatusCode.NoContent);
        });
        var resolver = new RecordingResolver([PublicAddress]);
        var clock = new RecordingClock();
        var client = CreateClient(factory, resolver, clock);
        var upload = new ZoteroAttachmentUpload(
            "JKLM2345",
            "image.png",
            "image/png",
            bytes,
            ModifiedTimeMilliseconds: 1785715200123);

        await client.UploadAttachmentAsync(
            new(ApiRoot, false, true),
            ApiKey,
            upload,
            CancellationToken.None);

        Assert.Equal(3, requests.Count);
        RequestSnapshot authorize = requests[0];
        Assert.Equal(
            "https://api.zotero.org/users/12345678/items/JKLM2345/file",
            authorize.Uri.AbsoluteUri);
        Assert.Equal(ApiKey, Assert.Single(authorize.Header("Zotero-API-Key")));
        Assert.Equal("3", Assert.Single(authorize.Header("Zotero-API-Version")));
        Assert.Equal("*", Assert.Single(authorize.Header("If-None-Match")));
        Assert.Contains("md5=900150983cd24fb0d6963f7d28e17f72", authorize.Body);
        Assert.Contains("filename=image.png", authorize.Body);
        Assert.Contains("filesize=3", authorize.Body);
        Assert.Contains("mtime=1785715200123", authorize.Body);

        RequestSnapshot storage = requests[1];
        Assert.Equal(
            "https://upload.example.net/signed/object?X-Amz-Signature=abc123",
            storage.Uri.AbsoluteUri);
        Assert.Empty(storage.Header("Zotero-API-Key"));
        Assert.Empty(storage.Header("Zotero-API-Version"));
        Assert.Empty(storage.Header("Authorization"));
        byte[] expectedBody = Encoding.UTF8.GetBytes(
                "--ZoteroBoundary\r\n\r\n")
            .Concat(bytes)
            .Concat(Encoding.UTF8.GetBytes(
                "\r\n--ZoteroBoundary--\r\n"))
            .ToArray();
        Assert.Equal(expectedBody, storage.BodyBytes);
        Assert.StartsWith(
            "multipart/form-data",
            Assert.Single(storage.Header("Content-Type")),
            StringComparison.OrdinalIgnoreCase);

        RequestSnapshot register = requests[2];
        Assert.Equal(authorize.Uri, register.Uri);
        Assert.Equal(ApiKey, Assert.Single(register.Header("Zotero-API-Key")));
        Assert.Equal("*", Assert.Single(register.Header("If-None-Match")));
        Assert.Equal("upload=opaque-upload-key", register.Body);
        Assert.Equal(
            ["api.zotero.org", "upload.example.net"],
            resolver.Hosts);
        Assert.Contains(clock.Delays, delay => delay >= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UploadExistsResponseIsIdempotentWithoutStorageOrRegisterRequests()
    {
        int requests = 0;
        var resolver = new RecordingResolver([PublicAddress]);
        var factory = new StubClientFactory((_, _) =>
        {
            requests++;
            return Task.FromResult(JsonTextResponse("{\"exists\":1}"));
        });
        var client = CreateClient(factory, resolver);

        await client.UploadAttachmentAsync(
            new(ApiRoot, false, true),
            ApiKey,
            AttachmentUpload(),
            CancellationToken.None);

        Assert.Equal(1, requests);
        Assert.Equal(["api.zotero.org"], resolver.Hosts);
    }

    [Theory]
    [InlineData("http://upload.example.net/file")]
    [InlineData("https://user@upload.example.net/file")]
    [InlineData("https://upload.example.net/file#fragment")]
    [InlineData("https://127.0.0.1/file")]
    [InlineData("https://upload.example.net:444/file")]
    public async Task UploadAuthorizationRejectsUnsafeSignedUrl(string signedUrl)
    {
        var factory = new StubClientFactory((_, _) => Task.FromResult(
            JsonResponse(new
            {
                url = signedUrl,
                contentType = "multipart/form-data; boundary=x",
                prefix = "--x\r\n",
                suffix = "\r\n--x--",
                uploadKey = "upload-key"
            })));
        var resolver = new RecordingResolver([PublicAddress]);
        var client = CreateClient(factory, resolver);

        ZoteroApiException exception =
            await Assert.ThrowsAsync<ZoteroApiException>(() =>
                client.UploadAttachmentAsync(
                    new(ApiRoot, false, true),
                    ApiKey,
                    AttachmentUpload(),
                    CancellationToken.None));

        Assert.Equal(ZoteroApiFailure.BlockedEndpoint, exception.Failure);
        Assert.Equal(["api.zotero.org"], resolver.Hosts);
    }

    [Fact]
    public async Task UploadSignedHostDnsMustRemainPublicBeforeStorageRequest()
    {
        var resolver = new RecordingResolver(host =>
            host == "api.zotero.org"
                ? [PublicAddress]
                : [IPAddress.Parse("10.0.0.9")]);
        var factory = new StubClientFactory((_, _) => Task.FromResult(
            JsonResponse(new
            {
                url = "https://upload.example.net/file?signature=abc",
                contentType = "multipart/form-data; boundary=x",
                prefix = "--x\r\n",
                suffix = "\r\n--x--",
                uploadKey = "upload-key"
            })));
        var client = CreateClient(factory, resolver);

        ZoteroApiException exception =
            await Assert.ThrowsAsync<ZoteroApiException>(() =>
                client.UploadAttachmentAsync(
                    new(ApiRoot, false, true),
                    ApiKey,
                    AttachmentUpload(),
                    CancellationToken.None));

        Assert.Equal(ZoteroApiFailure.BlockedEndpoint, exception.Failure);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task StorageRedirectIsRejectedAndNeverRegistered()
    {
        int officialRequests = 0;
        var factory = new StubClientFactory((request, _) =>
        {
            if (request.RequestUri!.Host == "api.zotero.org")
            {
                officialRequests++;
                return Task.FromResult(JsonResponse(new
                {
                    url = "https://upload.example.net/file?signature=abc",
                    contentType = "multipart/form-data; boundary=x",
                    prefix = "--x\r\n",
                    suffix = "\r\n--x--",
                    uploadKey = "upload-key"
                }));
            }
            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.TemporaryRedirect));
        });
        var client = CreateClient(factory);

        ZoteroApiException exception =
            await Assert.ThrowsAsync<ZoteroApiException>(() =>
                client.UploadAttachmentAsync(
                    new(ApiRoot, false, true),
                    ApiKey,
                    AttachmentUpload(),
                    CancellationToken.None));

        Assert.Equal(ZoteroApiFailure.Rejected, exception.Failure);
        Assert.Equal(1, officialRequests);
    }

    [Fact]
    public async Task UploadInputBoundsFailBeforeNetwork()
    {
        var factory = new StubClientFactory((_, _) =>
            throw new InvalidOperationException("network must not run"));
        var client = CreateClient(factory);
        ZoteroAttachmentUpload valid = AttachmentUpload();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.UploadAttachmentAsync(
                new(ApiRoot, false, true),
                ApiKey,
                valid with { FileName = "../image.png" },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.UploadAttachmentAsync(
                new(ApiRoot, false, true),
                ApiKey,
                valid with { ContentType = "text/html" },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.UploadAttachmentAsync(
                new(ApiRoot, false, true),
                ApiKey,
                valid with { Content = ReadOnlyMemory<byte>.Empty },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.UploadAttachmentAsync(
                new(ApiRoot, false, true),
                ApiKey,
                valid with
                {
                    Content = new byte[12 * 1024 * 1024 + 1]
                },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.UploadAttachmentAsync(
                new(ApiRoot, false, true),
                ApiKey,
                valid with { ModifiedTimeMilliseconds = -1 },
                CancellationToken.None));

        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task ExistingMatchingKeysAreAReplayWithoutPost()
    {
        ZoteroItem parent = ParentItem();
        ZoteroItem note = NoteItem(parent.Key);
        int postCount = 0;
        var factory = new StubClientFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postCount++;
            }
            string key = request.RequestUri!.Segments[^1];
            return Task.FromResult(
                ExistingItemResponse(key == parent.Key ? parent : note));
        });
        var client = CreateClient(factory);

        IReadOnlyList<string> keys = await client.CreateAsync(
            new(ApiRoot, true, false),
            ApiKey,
            [parent, note],
            CancellationToken.None);

        Assert.Equal([parent.Key, note.Key], keys);
        Assert.Equal(0, postCount);
    }

    [Theory]
    [InlineData("itemType")]
    [InlineData("url")]
    [InlineData("parentItem")]
    [InlineData("marker")]
    public async Task ExistingKeyCollisionNeverOverwritesMismatchedIdentity(
        string mismatch)
    {
        ZoteroItem expected = ParentItem();
        object body = ExistingItemData(
            expected,
            itemType: mismatch == "itemType" ? "journalArticle" : null,
            url: mismatch == "url" ? "https://attacker.example/item" : null,
            parentItem: mismatch == "parentItem" ? "BCDE2345" : null,
            marker: mismatch == "marker" ? "other-owner" : null);
        int postCount = 0;
        var factory = new StubClientFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Post) postCount++;
            return Task.FromResult(JsonResponse(body));
        });
        var client = CreateClient(factory);

        ZoteroApiException exception =
            await Assert.ThrowsAsync<ZoteroApiException>(
                () => client.CreateAsync(
                    new(ApiRoot, false, false),
                    ApiKey,
                    [expected],
                    CancellationToken.None));

        Assert.Equal(ZoteroApiFailure.Collision, exception.Failure);
        Assert.False(exception.IsRetryable);
        Assert.Equal(0, postCount);
    }

    [Fact]
    public async Task ExistingKeyWithMalformedIdentityShapeIsAlsoACollision()
    {
        ZoteroItem expected = ParentItem();
        var factory = new StubClientFactory(_ => JsonTextResponse(
            "{\"data\":{" +
            "\"key\":\"ABCD2345\"," +
            "\"itemType\":\"webpage\"," +
            "\"url\":\"https://news.example.com/articles/1\"," +
            "\"parentItem\":{}," +
            "\"extra\":\"LenxTool-Marker: entry-0123456789abcdef\"}}"));
        var client = CreateClient(factory);

        ZoteroApiException exception =
            await Assert.ThrowsAsync<ZoteroApiException>(
                () => client.CreateAsync(
                    new(ApiRoot, false, false),
                    ApiKey,
                    [expected],
                    CancellationToken.None));

        Assert.Equal(ZoteroApiFailure.Collision, exception.Failure);
    }

    [Theory]
    [InlineData(401, ZoteroApiFailure.Unauthorized, false)]
    [InlineData(403, ZoteroApiFailure.Unauthorized, false)]
    [InlineData(409, ZoteroApiFailure.Conflict, true)]
    [InlineData(412, ZoteroApiFailure.Conflict, false)]
    [InlineData(413, ZoteroApiFailure.RequestTooLarge, false)]
    [InlineData(428, ZoteroApiFailure.Conflict, false)]
    [InlineData(429, ZoteroApiFailure.RateLimited, true)]
    [InlineData(500, ZoteroApiFailure.Unavailable, true)]
    [InlineData(503, ZoteroApiFailure.Unavailable, true)]
    public async Task HttpFailuresMapWithoutResponseBodyOrCredential(
        int status,
        ZoteroApiFailure expected,
        bool retryable)
    {
        const string ProviderBody = "private-provider-response";
        var factory = new StubClientFactory((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(ProviderBody)
            }));
        var client = CreateClient(factory);

        ZoteroApiException exception =
            await Assert.ThrowsAsync<ZoteroApiException>(
                () => client.ProbeAsync(
                    new(ApiRoot, false, false),
                    ApiKey,
                    CancellationToken.None));

        Assert.Equal(expected, exception.Failure);
        Assert.Equal(retryable, exception.IsRetryable);
        Assert.DoesNotContain(ProviderBody, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http200StillRejectsPerItemFailureAndDoesNotExposeItsMessage()
    {
        ZoteroItem parent = ParentItem();
        int request = 0;
        var factory = new StubClientFactory((message, _) =>
        {
            request++;
            if (message.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(JsonTextResponse(
                "{\"successful\":{},\"failed\":{\"0\":{\"key\":\"ABCD2345\",\"code\":403,\"message\":\"private-library-name\"}}}"));
        });
        var client = CreateClient(factory);

        ZoteroApiException exception =
            await Assert.ThrowsAsync<ZoteroApiException>(
                () => client.CreateAsync(
                    new(ApiRoot, false, false),
                    ApiKey,
                    [parent],
                    CancellationToken.None));

        Assert.Equal(ZoteroApiFailure.Unauthorized, exception.Failure);
        Assert.DoesNotContain(
            "private-library-name",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.True(request >= 2);
    }

    [Fact]
    public async Task RetryAfterAndBackoffPauseTargetBeforeBoundedRetry()
    {
        ZoteroItem parent = ParentItem();
        var clock = new RecordingClock();
        int postCount = 0;
        bool created = false;
        var factory = new StubClientFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(
                    created
                        ? ExistingItemResponse(parent)
                        : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            postCount++;
            if (postCount == 1)
            {
                var unavailable = new HttpResponseMessage(
                    HttpStatusCode.ServiceUnavailable);
                unavailable.Headers.TryAddWithoutValidation("Retry-After", "12");
                unavailable.Headers.TryAddWithoutValidation("Backoff", "20");
                return Task.FromResult(unavailable);
            }
            created = true;
            return Task.FromResult(JsonTextResponse(
                "{\"successful\":{\"0\":{}},\"failed\":{}}"));
        });
        var client = CreateClient(factory, clock: clock);

        IReadOnlyList<string> result = await client.CreateAsync(
            new(ApiRoot, false, false),
            ApiKey,
            [parent],
            CancellationToken.None);

        Assert.Equal([parent.Key], result);
        Assert.Equal(2, postCount);
        Assert.Contains(clock.Delays, delay => delay >= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task UncertainPostReconcilesMatchingKeyBeforeSendingAgain()
    {
        ZoteroItem parent = ParentItem();
        int postCount = 0;
        bool created = false;
        var factory = new StubClientFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(
                    created
                        ? ExistingItemResponse(parent)
                        : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            postCount++;
            created = true;
            throw new HttpRequestException("response lost after write");
        });
        var client = CreateClient(factory);

        IReadOnlyList<string> result = await client.CreateAsync(
            new(ApiRoot, false, false),
            ApiKey,
            [parent],
            CancellationToken.None);

        Assert.Equal([parent.Key], result);
        Assert.Equal(1, postCount);
    }

    [Fact]
    public async Task SameTargetOperationsAreSerialized()
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximumActive = 0;
        var factory = new StubClientFactory(async (_, cancellationToken) =>
        {
            int now = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, now);
            if (!firstEntered.Task.IsCompleted)
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            Interlocked.Decrement(ref active);
            return JsonResponse(new
            {
                userID = 12345678,
                access = new
                {
                    user = new
                    {
                        library = true,
                        write = true,
                        notes = false,
                        files = false
                    }
                }
            });
        });
        var client = CreateClient(factory);

        Task<ZoteroApiCapability> first = client.ProbeAsync(
            new(ApiRoot, false, false),
            ApiKey,
            CancellationToken.None);
        await firstEntered.Task;
        Task<ZoteroApiCapability> second = client.ProbeAsync(
            new(ApiRoot, false, false),
            ApiKey,
            CancellationToken.None);

        await Task.Yield();
        Assert.False(second.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeJsonParsing()
    {
        string oversized = new('A', 256 * 1024 + 1);
        var factory = new StubClientFactory((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    oversized,
                    Encoding.UTF8,
                    "application/json")
            }));
        var client = CreateClient(factory);

        ZoteroApiException exception =
            await Assert.ThrowsAsync<ZoteroApiException>(
                () => client.ProbeAsync(
                    new(ApiRoot, false, false),
                    ApiKey,
                    CancellationToken.None));

        Assert.Equal(ZoteroApiFailure.Rejected, exception.Failure);
        Assert.DoesNotContain(oversized[..128], exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationMapsToClosedClientException()
    {
        var factory = new StubClientFactory(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        var client = CreateClient(factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        ZoteroApiException exception =
            await Assert.ThrowsAsync<ZoteroApiException>(
                () => client.ProbeAsync(
                    new(ApiRoot, false, false),
                    ApiKey,
                    cancellation.Token));

        Assert.Equal(ZoteroApiFailure.Cancelled, exception.Failure);
        Assert.False(exception.IsRetryable);
        Assert.DoesNotContain(ApiKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportConstructionFailureAlsoMapsToClosedUnavailableError()
    {
        var client = new ZoteroApiClient(
            new RecordingResolver([PublicAddress]),
            new ThrowingClientFactory(),
            new RecordingClock());

        ZoteroApiException exception =
            await Assert.ThrowsAsync<ZoteroApiException>(
                () => client.ProbeAsync(
                    new(ApiRoot, false, false),
                    ApiKey,
                    CancellationToken.None));

        Assert.Equal(ZoteroApiFailure.Unavailable, exception.Failure);
        Assert.True(exception.IsRetryable);
        Assert.DoesNotContain(
            "private-transport-detail",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthProbeUsesCurrentTargetOptionsAndPinnedContextWithoutWriting()
    {
        var target = new ZoteroExportTarget(
            ZoteroExportTarget.DefaultTargetId,
            12345678,
            ZoteroItemType.Webpage,
            IncludeSummaryNote: true,
            UploadFirstImageAttachment: false);
        var store = new FakeTargetStore { Current = target };
        var client = new RecordingApiClient();
        var probe = new ZoteroEntryIntegrationHealthProbe(client, store);

        EntryIntegrationProbeResult result = await probe.ProbeAsync(
            new(ApiRoot, [PublicAddress]),
            ApiKey,
            CancellationToken.None);

        Assert.Equal(EntryIntegrationHealthStatus.Healthy, result.Status);
        Assert.NotNull(client.ProbedTarget);
        Assert.True(client.ProbedTarget.RequireNotesPermission);
        Assert.False(client.ProbedTarget.RequireFilesPermission);
        Assert.Equal([PublicAddress], client.ProbedAddresses);
        Assert.Equal(0, client.CreateCount);
    }

    [Fact]
    public async Task HealthProbeFailsClosedWhenTargetMissingOrEndpointDoesNotMatch()
    {
        var client = new RecordingApiClient();
        var missing = new ZoteroEntryIntegrationHealthProbe(
            client,
            new FakeTargetStore());
        var mismatched = new ZoteroEntryIntegrationHealthProbe(
            client,
            new FakeTargetStore
            {
                Current = new(
                    ZoteroExportTarget.DefaultTargetId,
                    87654321,
                    ZoteroItemType.Webpage,
                    IncludeSummaryNote: false,
                    UploadFirstImageAttachment: false)
            });

        EntryIntegrationProbeResult missingResult = await missing.ProbeAsync(
            new(ApiRoot, [PublicAddress]),
            ApiKey,
            CancellationToken.None);
        EntryIntegrationProbeResult mismatchedResult = await mismatched.ProbeAsync(
            new(ApiRoot, [PublicAddress]),
            ApiKey,
            CancellationToken.None);

        Assert.Equal(EntryIntegrationHealthStatus.Unauthorized, missingResult.Status);
        Assert.Equal(EntryIntegrationHealthStatus.BlockedEndpoint, mismatchedResult.Status);
        Assert.Null(client.ProbedTarget);
    }

    [Fact]
    public async Task HealthProbeTreatsMalformedCredentialAsUnauthorizedNotEndpointFailure()
    {
        var configured = new ZoteroExportTarget(
            ZoteroExportTarget.DefaultTargetId,
            12345678,
            ZoteroItemType.Webpage,
            IncludeSummaryNote: false,
            UploadFirstImageAttachment: false);
        var api = CreateClient(new StubClientFactory((_, _) =>
            throw new InvalidOperationException("HTTP must not run")));
        var probe = new ZoteroEntryIntegrationHealthProbe(
            api,
            new FakeTargetStore { Current = configured });

        EntryIntegrationProbeResult result = await probe.ProbeAsync(
            new(ApiRoot, [PublicAddress]),
            "bad\r\nkey",
            CancellationToken.None);

        Assert.Equal(EntryIntegrationHealthStatus.Unauthorized, result.Status);
    }

    private static ZoteroApiClient CreateClient(
        StubClientFactory factory,
        RecordingResolver? resolver = null,
        RecordingClock? clock = null) =>
        new(
            resolver ?? new RecordingResolver([PublicAddress]),
            factory,
            clock ?? new RecordingClock());

    private static ZoteroItem ParentItem() => new(
        "ABCD2345",
        "webpage",
        "A deterministic webpage",
        "https://news.example.com/articles/1",
        ParentItem: null,
        Date: "2026-08-03",
        ContainerTitle: null,
        NoteHtml: null,
        LenxToolMarker: "entry-0123456789abcdef",
        Creators: [new("Ada Lovelace")],
        Tags: ["research", "中文"]);

    private static ZoteroItem NoteItem(string parentKey) => new(
        "EFGH6789",
        "note",
        Title: string.Empty,
        Url: string.Empty,
        ParentItem: parentKey,
        Date: null,
        ContainerTitle: null,
        NoteHtml: "<p>Summary generated locally.</p>",
        LenxToolMarker: "note-0123456789abcdef",
        Creators: [],
        Tags: []);

    private static ZoteroItem AttachmentItem(string parentKey) => new(
        "JKLM2345",
        "attachment",
        Title: "First image",
        Url: string.Empty,
        ParentItem: parentKey,
        Date: null,
        ContainerTitle: null,
        NoteHtml: null,
        LenxToolMarker: "attachment-0123456789abcdef",
        Creators: [],
        Tags: [],
        ContentType: "image/png",
        FileName: "image.png");

    private static ZoteroAttachmentUpload AttachmentUpload() => new(
        "JKLM2345",
        "image.png",
        "image/png",
        Encoding.ASCII.GetBytes("abc"),
        ModifiedTimeMilliseconds: 1785715200123);

    private static HttpResponseMessage ExistingItemResponse(ZoteroItem item) =>
        JsonResponse(ExistingItemData(item));

    private static object ExistingItemData(
        ZoteroItem item,
        string? itemType = null,
        string? url = null,
        string? parentItem = null,
        string? marker = null)
    {
        string actualMarker = marker ?? item.LenxToolMarker;
        return new
        {
            key = item.Key,
            version = 42,
            data = new
            {
                key = item.Key,
                version = 42,
                itemType = itemType ?? item.ItemType,
                url = url ?? item.Url,
                parentItem = parentItem ?? item.ParentItem,
                extra = item.ItemType is "note" or "attachment"
                    ? string.Empty
                    : $"LenxTool-Marker: {actualMarker}",
                note = item.ItemType is "note" or "attachment"
                    ? $"{item.NoteHtml}\n<!-- LenxTool-Marker:{actualMarker} -->"
                    : string.Empty,
                linkMode = item.ItemType == "attachment"
                    ? "imported_file"
                    : null,
                contentType = item.ContentType,
                filename = item.FileName
            }
        };
    }

    private static HttpResponseMessage JsonResponse(object body) =>
        JsonTextResponse(JsonSerializer.Serialize(body));

    private static HttpResponseMessage JsonTextResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Headers,
        string? Body,
        byte[]? BodyBytes)
    {
        public IReadOnlyList<string> Header(string name) =>
            Headers.TryGetValue(name, out IReadOnlyList<string>? values)
                ? values
                : [];

        public static async Task<RequestSnapshot> CreateAsync(
            HttpRequestMessage request)
        {
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> contentHeaders =
                request.Content is null
                    ? []
                    : request.Content.Headers;
            var headers = request.Headers
                .Concat(contentHeaders)
                .ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            byte[]? bodyBytes = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync();
            string? body = bodyBytes is null
                ? null
                : Encoding.UTF8.GetString(bodyBytes);
            return new(
                request.Method,
                request.RequestUri!,
                headers,
                body,
                bodyBytes);
        }
    }

    private sealed class StubClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : IZoteroHttpClientFactory
    {
        public StubClientFactory(
            Func<HttpRequestMessage, HttpResponseMessage> send)
            : this((request, _) => Task.FromResult(send(request)))
        {
        }

        public int CreateCount { get; private set; }
        public IReadOnlyList<IPAddress> LastPinnedAddresses { get; private set; } = [];
        public List<Uri> CreatedEndpoints { get; } = [];

        public HttpClient Create(
            Uri endpoint,
            IReadOnlyList<IPAddress> pinnedAddresses)
        {
            CreateCount++;
            CreatedEndpoints.Add(endpoint);
            LastPinnedAddresses = pinnedAddresses.ToArray();
            return new(new StubHandler(send), disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class ThrowingClientFactory : IZoteroHttpClientFactory
    {
        public HttpClient Create(
            Uri endpoint,
            IReadOnlyList<IPAddress> pinnedAddresses) =>
            throw new InvalidOperationException("private-transport-detail");
    }

    private sealed class RecordingResolver : IFeedHostResolver
    {
        private readonly Func<string, IReadOnlyList<IPAddress>> _resolve;

        public RecordingResolver(IReadOnlyList<IPAddress> addresses)
            : this(_ => addresses)
        {
        }

        public RecordingResolver(Func<string, IReadOnlyList<IPAddress>> resolve) =>
            _resolve = resolve;

        public List<string> Hosts { get; } = [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken)
        {
            Hosts.Add(host);
            return Task.FromResult(_resolve(host));
        }
    }

    private sealed class RecordingClock : IZoteroClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingApiClient : IZoteroApiClient
    {
        public ZoteroApiTarget? ProbedTarget { get; private set; }
        public IReadOnlyList<IPAddress> ProbedAddresses { get; private set; } = [];
        public int CreateCount { get; private set; }

        public Task<ZoteroApiCapability> ProbeAsync(
            ZoteroApiTarget target,
            string apiKey,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Health must use pinned addresses.");

        public Task<ZoteroApiCapability> ProbePinnedAsync(
            ZoteroApiTarget target,
            string apiKey,
            IReadOnlyList<IPAddress> pinnedAddresses,
            CancellationToken cancellationToken)
        {
            ProbedTarget = target;
            ProbedAddresses = pinnedAddresses.ToArray();
            return Task.FromResult(new ZoteroApiCapability(
                12345678,
                CanWrite: true,
                CanWriteNotes: target.RequireNotesPermission,
                CanWriteFiles: target.RequireFilesPermission));
        }

        public Task<IReadOnlyList<string>> CreateAsync(
            ZoteroApiTarget target,
            string apiKey,
            IReadOnlyList<ZoteroItem> items,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.FromResult<IReadOnlyList<string>>(
                items.Select(item => item.Key).ToArray());
        }

        public Task UploadAttachmentAsync(
            ZoteroApiTarget target,
            string apiKey,
            ZoteroAttachmentUpload upload,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTargetStore : IZoteroExportTargetStore
    {
        public ZoteroExportTarget? Current { get; init; }

        public Task<ZoteroExportTarget?> GetAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task<IZoteroExportTargetLease> AcquireExportLeaseAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Health must not acquire a write lease.");

        public Task SaveAsync(
            ZoteroExportTarget target,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Health must not write settings.");
    }
}
