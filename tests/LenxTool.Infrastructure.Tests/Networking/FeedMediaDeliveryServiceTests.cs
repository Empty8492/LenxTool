using System.Net;
using System.Net.Http.Headers;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedMediaDeliveryServiceTests : IDisposable
{
    private static readonly IPAddress PublicAddress =
        IPAddress.Parse("93.184.216.34");
    private static readonly byte[] Mp3Bytes =
        "ID3\u0004\u0000\u0000\u0000\u0000\u0000\u0015audio-payload"u8.ToArray();
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DeliverAsyncDownloadsVerifiedAudioAndQueuesTraceableJob()
    {
        await using TestContext context = await CreateContextAsync(
            (_, addresses, _) =>
            {
                Assert.Equal([PublicAddress], addresses);
                return Response(HttpStatusCode.OK, "audio/mpeg", Mp3Bytes);
            });

        FeedMediaDeliveryRegistration result = await context.Service.DeliverAsync(
            CreateEntry(),
            CreateEnclosure(),
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(MediaJobStatus.Queued, result.Job.Status);
        Assert.Equal("FeedTranscription", result.Job.Kind);
        Assert.Equal("audio/mpeg", result.Delivery.MediaType);
        Assert.Equal("entry-audio", result.Delivery.EntryId);
        Assert.Equal(
            Path.GetFullPath(context.Paths.FeedMediaDirectory),
            Path.GetDirectoryName(result.Job.InputPath));
        Assert.Equal(Mp3Bytes, await File.ReadAllBytesAsync(result.Job.InputPath));
        Assert.Empty(Directory.GetFiles(context.Paths.FeedMediaTempDirectory));
        Assert.Single(await new MediaJobRepository(context.Database).GetQueuedAsync(
            CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(SupportedMediaCases))]
    public async Task DeliverAsyncAcceptsSupportedVerifiedMediaContainers(
        string extension,
        string declaredMediaType,
        string responseMediaType,
        byte[] bytes)
    {
        await using TestContext context = await CreateContextAsync(
            (_, _, _) => Response(
                HttpStatusCode.OK,
                responseMediaType,
                bytes));
        var enclosure = new FeedEnclosure(
            $"https://media.example/episodes/daily{extension}",
            declaredMediaType,
            bytes.Length,
            "媒体附件");

        FeedMediaDeliveryRegistration result = await context.Service.DeliverAsync(
            CreateEntry(),
            enclosure,
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.EndsWith(extension, result.Job.InputPath, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.Job.InputPath));
    }

    [Fact]
    public async Task DeliverAsyncReturnsExistingJobWithoutSecondNetworkRequest()
    {
        await using TestContext context = await CreateContextAsync(
            (_, _, _) => Response(HttpStatusCode.OK, "audio/mpeg", Mp3Bytes));
        FeedEntry entry = CreateEntry();
        FeedEnclosure enclosure = CreateEnclosure();

        FeedMediaDeliveryRegistration first = await context.Service.DeliverAsync(
            entry,
            enclosure,
            CancellationToken.None);
        FeedMediaDeliveryRegistration duplicate = await context.Service.DeliverAsync(
            entry,
            enclosure,
            CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Delivery, duplicate.Delivery);
        Assert.Equal(first.Job, duplicate.Job);
        Assert.Equal(1, context.Transport.CallCount);
        Assert.Single(Directory.GetFiles(context.Paths.FeedMediaDirectory));
    }

    [Fact]
    public async Task DeliverAsyncSerializesConcurrentDuplicateDownloads()
    {
        var firstRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(async (_, _, cancellationToken) =>
        {
            firstRequest.SetResult();
            await releaseRequest.Task.WaitAsync(cancellationToken);
            return Response(HttpStatusCode.OK, "audio/mpeg", Mp3Bytes);
        });
        await using TestContext context = await CreateContextAsync(transport);
        FeedEntry entry = CreateEntry();
        FeedEnclosure enclosure = CreateEnclosure();

        Task<FeedMediaDeliveryRegistration>[] deliveries = Enumerable
            .Range(0, 8)
            .Select(_ => context.Service.DeliverAsync(
                entry,
                enclosure,
                CancellationToken.None))
            .ToArray();
        await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(1, transport.CallCount);
        releaseRequest.SetResult();

        FeedMediaDeliveryRegistration[] results = await Task.WhenAll(deliveries);

        Assert.Single(results, result => result.Created);
        Assert.Equal(1, transport.CallCount);
        Assert.Single(Directory.GetFiles(context.Paths.FeedMediaDirectory));
    }

    [Fact]
    public async Task DeliverAsyncRedownloadsMissingFileIntoExistingJob()
    {
        await using TestContext context = await CreateContextAsync(
            (_, _, _) => Response(HttpStatusCode.OK, "audio/mpeg", Mp3Bytes));
        FeedMediaDeliveryRegistration first = await context.Service.DeliverAsync(
            CreateEntry(),
            CreateEnclosure(),
            CancellationToken.None);
        File.Delete(first.Job.InputPath);

        FeedMediaDeliveryRegistration recovered = await context.Service.DeliverAsync(
            CreateEntry(),
            CreateEnclosure(),
            CancellationToken.None);

        Assert.False(recovered.Created);
        Assert.Equal(first.Delivery, recovered.Delivery);
        Assert.True(File.Exists(first.Job.InputPath));
        Assert.Equal(2, context.Transport.CallCount);
        Assert.Single(await new MediaJobRepository(context.Database).GetQueuedAsync(
            CancellationToken.None));
    }

    [Fact]
    public async Task DeliverAsyncRejectsDeclaredOrResponseSizeAboveLimitWithoutResidue()
    {
        FeedMediaDeliveryOptions options =
            TestOptions() with { MaximumBytes = Mp3Bytes.Length - 1 };
        await using TestContext declared = await CreateContextAsync(
            (_, _, _) => Response(HttpStatusCode.OK, "audio/mpeg", Mp3Bytes),
            options);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            declared.Service.DeliverAsync(
                CreateEntry(),
                CreateEnclosure(Mp3Bytes.Length),
                CancellationToken.None));
        Assert.Equal(0, declared.Transport.CallCount);
        await AssertNoResidueAsync(declared);

        await using TestContext response = await CreateContextAsync(
            (_, _, _) => Response(HttpStatusCode.OK, "audio/mpeg", Mp3Bytes),
            options);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            response.Service.DeliverAsync(
                CreateEntry(),
                CreateEnclosure(length: null),
                CancellationToken.None));
        Assert.Equal(1, response.Transport.CallCount);
        await AssertNoResidueAsync(response);
    }

    [Fact]
    public async Task DeliverAsyncBoundsUnknownLengthWhileStreaming()
    {
        FeedMediaDeliveryOptions options =
            TestOptions() with { MaximumBytes = Mp3Bytes.Length - 1 };
        await using TestContext context = await CreateContextAsync(
            (_, _, _) => StreamResponse(
                "audio/mpeg",
                new MemoryStream(Mp3Bytes, writable: false)),
            options);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            context.Service.DeliverAsync(
                CreateEntry(),
                CreateEnclosure(length: null),
                CancellationToken.None));

        await AssertNoResidueAsync(context);
    }

    [Fact]
    public async Task DeliverAsyncRejectsInsufficientDiskBeforeNetwork()
    {
        FeedMediaDeliveryOptions options =
            TestOptions() with
            {
                MaximumBytes = long.MaxValue / 4
            };
        await using TestContext context = await CreateContextAsync(
            (_, _, _) => Response(
                HttpStatusCode.OK,
                "audio/mpeg",
                Mp3Bytes),
            options);

        await Assert.ThrowsAsync<IOException>(() =>
            context.Service.DeliverAsync(
                CreateEntry(),
                CreateEnclosure(length: null),
                CancellationToken.None));

        Assert.Equal(0, context.Transport.CallCount);
        await AssertNoResidueAsync(context);
    }

    [Fact]
    public async Task DeliverAsyncRejectsUnverifiedAttachmentBeforeNetwork()
    {
        await using TestContext context = await CreateContextAsync(
            (_, _, _) => Response(HttpStatusCode.OK, "audio/mpeg", Mp3Bytes));
        var enclosure = new FeedEnclosure(
            "https://media.example/episodes/daily.bin",
            "audio/mpeg",
            null,
            "伪装附件");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            context.Service.DeliverAsync(
                CreateEntry(),
                enclosure,
                CancellationToken.None));

        Assert.Equal(0, context.Transport.CallCount);
        await AssertNoResidueAsync(context);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("audio/wav")]
    public async Task DeliverAsyncRejectsResponseMimeMismatchWithoutResidue(
        string responseMediaType)
    {
        await using TestContext context = await CreateContextAsync(
            (_, _, _) => Response(
                HttpStatusCode.OK,
                responseMediaType,
                Mp3Bytes));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            context.Service.DeliverAsync(
                CreateEntry(),
                CreateEnclosure(),
                CancellationToken.None));

        await AssertNoResidueAsync(context);
    }

    [Fact]
    public async Task DeliverAsyncRejectsSpoofedMediaSignatureWithoutResidue()
    {
        await using TestContext context = await CreateContextAsync(
            (_, _, _) => Response(
                HttpStatusCode.OK,
                "audio/mpeg",
                "<html>not audio</html>"u8.ToArray()));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            context.Service.DeliverAsync(
                CreateEntry(),
                CreateEnclosure(),
                CancellationToken.None));

        await AssertNoResidueAsync(context);
    }

    [Fact]
    public async Task DeliverAsyncRevalidatesRedirectAndBlocksPrivateTarget()
    {
        await using TestContext context = await CreateContextAsync(
            (_, _, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new("https://10.0.0.5/private.mp3");
                return new(response);
            });

        AppException error = await Assert.ThrowsAsync<AppException>(() =>
            context.Service.DeliverAsync(
                CreateEntry(),
                CreateEnclosure(),
                CancellationToken.None));

        Assert.Equal(AppErrorCode.AccessDenied, error.Error.Code);
        Assert.Equal(1, context.Transport.CallCount);
        await AssertNoResidueAsync(context);
    }

    [Fact]
    public async Task DeliverAsyncRejectsHttpSourceUnlessHostIsExplicitlyAllowed()
    {
        await using TestContext context = await CreateContextAsync(
            (_, _, _) => Response(HttpStatusCode.OK, "audio/mpeg", Mp3Bytes));
        var enclosure = new FeedEnclosure(
            "http://media.example/episodes/daily.mp3",
            "audio/mpeg",
            null,
            "不安全来源");

        AppException error = await Assert.ThrowsAsync<AppException>(() =>
            context.Service.DeliverAsync(
                CreateEntry(),
                enclosure,
                CancellationToken.None));

        Assert.Equal(AppErrorCode.InvalidRequest, error.Error.Code);
        Assert.Equal(0, context.Transport.CallCount);
        await AssertNoResidueAsync(context);
    }

    [Fact]
    public async Task DeliverAsyncPropagatesCallerCancellationAndCleansTemporaryFile()
    {
        var stream = new BlockingReadStream(Mp3Bytes);
        await using TestContext context = await CreateContextAsync(
            (_, _, _) => StreamResponse("audio/mpeg", stream));
        using var cancellation = new CancellationTokenSource();

        Task<FeedMediaDeliveryRegistration> delivery = context.Service.DeliverAsync(
            CreateEntry(),
            CreateEnclosure(length: null),
            cancellation.Token);
        await stream.FirstRead.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        await AssertNoResidueAsync(context);
    }

    [Fact]
    public async Task DeliverAsyncEnforcesTotalTimeoutWithoutResidue()
    {
        var transport = new FakeTransport(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        await using TestContext context = await CreateContextAsync(
            transport,
            TestOptions() with { TotalTimeout = TimeSpan.FromMilliseconds(50) });

        await Assert.ThrowsAsync<TimeoutException>(() =>
            context.Service.DeliverAsync(
                CreateEntry(),
                CreateEnclosure(),
                CancellationToken.None));

        await AssertNoResidueAsync(context);
    }

    private async Task<TestContext> CreateContextAsync(
        Func<
            Uri,
            IReadOnlyList<IPAddress>,
            CancellationToken,
            FeedMediaHttpResponse> send,
        FeedMediaDeliveryOptions? options = null) =>
        await CreateContextAsync(new FakeTransport(send), options);

    private async Task<TestContext> CreateContextAsync(
        FakeTransport transport,
        FeedMediaDeliveryOptions? options = null)
    {
        var paths = new AppPaths(Path.Combine(
            _testRoot,
            Guid.NewGuid().ToString("N")));
        var database = new SqliteDatabase(
            paths,
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedMediaDeliveryRepository(database);
        var resolver = new FakeResolver();
        var service = new FeedMediaDeliveryService(
            repository,
            resolver,
            transport,
            FeedDiscoveryOptions.Default,
            options ?? TestOptions(),
            paths,
            TimeProvider.System);
        return new(database, paths, service, transport);
    }

    private static FeedMediaDeliveryOptions TestOptions() => new(
        MaximumBytes: 1_024,
        TotalTimeout: TimeSpan.FromSeconds(5),
        MaximumRedirects: 3,
        MaximumConcurrentDownloads: 2);

    public static TheoryData<
        string,
        string,
        string,
        byte[]> SupportedMediaCases => new()
        {
            {
                ".mp3",
                "audio/mpeg",
                "audio/mpeg",
                Mp3Bytes
            },
            {
                ".m4a",
                "audio/x-m4a",
                "audio/mp4",
                [0, 0, 0, 24, .. "ftypM4A "u8.ToArray(), 0, 0, 0, 0]
            },
            {
                ".aac",
                "audio/aac",
                "audio/aac",
                [0xFF, 0xF1, 0x50, 0x80]
            },
            {
                ".ogg",
                "audio/ogg",
                "audio/ogg",
                "OggS-audio"u8.ToArray()
            },
            {
                ".opus",
                "audio/opus",
                "audio/opus",
                "OggS-opus"u8.ToArray()
            },
            {
                ".wav",
                "audio/x-wav",
                "audio/wav",
                "RIFF0000WAVEdata"u8.ToArray()
            },
            {
                ".flac",
                "audio/flac",
                "audio/flac",
                "fLaC-audio"u8.ToArray()
            },
            {
                ".mp4",
                "video/mp4",
                "video/mp4",
                [0, 0, 0, 24, .. "ftypisom"u8.ToArray(), 0, 0, 0, 0]
            },
            {
                ".webm",
                "video/webm",
                "video/webm",
                [0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0]
            },
            {
                ".mov",
                "video/quicktime",
                "video/quicktime",
                [0, 0, 0, 24, .. "ftypqt  "u8.ToArray(), 0, 0, 0, 0]
            },
            {
                ".ogv",
                "video/ogg",
                "video/ogg",
                "OggS-video"u8.ToArray()
            }
        };

    private static FeedEntry CreateEntry()
    {
        FeedEnclosure enclosure = CreateEnclosure();
        return new(
            "entry-audio",
            "feed-tech",
            "episode-1",
            "https://news.example/episodes/1",
            "AI 新闻播客",
            "Lenx",
            new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero),
            null,
            "本期 AI 新闻",
            "本期 AI 新闻",
            ["AI"],
            [enclosure],
            "content-hash",
            new DateTimeOffset(2026, 7, 26, 9, 5, 0, TimeSpan.Zero));
    }

    private static FeedEnclosure CreateEnclosure(long? length = null) => new(
        "https://media.example/episodes/daily.mp3",
        "audio/mpeg",
        length,
        "每日音频");

    private static FeedMediaHttpResponse Response(
        HttpStatusCode status,
        string mediaType,
        byte[] bytes)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new(response);
    }

    private static FeedMediaHttpResponse StreamResponse(
        string mediaType,
        Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new(response);
    }

    private static async Task AssertNoResidueAsync(TestContext context)
    {
        Assert.Empty(Directory.GetFiles(context.Paths.FeedMediaDirectory));
        Assert.Empty(Directory.GetFiles(context.Paths.FeedMediaTempDirectory));
        Assert.Empty(await new MediaJobRepository(context.Database).GetRecentAsync(
            10,
            CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private sealed class TestContext(
        SqliteDatabase database,
        AppPaths paths,
        FeedMediaDeliveryService service,
        FakeTransport transport) : IAsyncDisposable
    {
        public SqliteDatabase Database { get; } = database;
        public AppPaths Paths { get; } = paths;
        public FeedMediaDeliveryService Service { get; } = service;
        public FakeTransport Transport { get; } = transport;

        public ValueTask DisposeAsync()
        {
            Service.Dispose();
            Database.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeResolver : IFeedHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([PublicAddress]);
    }

    private sealed class FakeTransport : IFeedMediaTransport
    {
        private readonly Func<
            Uri,
            IReadOnlyList<IPAddress>,
            CancellationToken,
            Task<FeedMediaHttpResponse>> _send;

        public FakeTransport(
            Func<
                Uri,
                IReadOnlyList<IPAddress>,
                CancellationToken,
                FeedMediaHttpResponse> send)
        {
            _send = (uri, addresses, cancellationToken) =>
                Task.FromResult(send(uri, addresses, cancellationToken));
        }

        public FakeTransport(
            Func<
                Uri,
                IReadOnlyList<IPAddress>,
                CancellationToken,
                Task<FeedMediaHttpResponse>> send)
        {
            _send = send;
        }

        public int CallCount { get; private set; }

        public async Task<FeedMediaHttpResponse> SendAsync(
            Uri uri,
            IReadOnlyList<IPAddress> addresses,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return await _send(uri, addresses, cancellationToken);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly byte[] _firstChunk;
        private readonly TaskCompletionSource _firstReadSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _servedFirstChunk;

        public BlockingReadStream(byte[] firstChunk)
        {
            _firstChunk = firstChunk;
        }

        public Task FirstRead => _firstReadSignal.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_servedFirstChunk)
            {
                _servedFirstChunk = true;
                _firstChunk.CopyTo(buffer);
                _firstReadSignal.SetResult();
                return _firstChunk.Length;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

    }
}
