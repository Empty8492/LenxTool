using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.App.Tests.Services;

public sealed class FeedVideoDeliveryPlanningServiceTests
{
    private const long Mebibyte = 1024L * 1024;
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(
        10 * Mebibyte,
        2_000 * Mebibyte,
        FeedVideoDeliveryPlanStatus.Ready,
        false)]
    [InlineData(
        25 * Mebibyte,
        2_000 * Mebibyte,
        FeedVideoDeliveryPlanStatus.Ready,
        true)]
    [InlineData(
        null,
        2_000 * Mebibyte,
        FeedVideoDeliveryPlanStatus.Ready,
        true)]
    [InlineData(
        513 * Mebibyte,
        2_000 * Mebibyte,
        FeedVideoDeliveryPlanStatus.ExceedsLimit,
        false)]
    [InlineData(
        25 * Mebibyte,
        0,
        FeedVideoDeliveryPlanStatus.InsufficientSpace,
        false)]
    public async Task PlanExplainsSizeTargetConfirmationAndSpace(
        long? declaredBytes,
        long availableBytes,
        FeedVideoDeliveryPlanStatus expectedStatus,
        bool expectedConfirmation)
    {
        using var directory = new TemporaryDirectory();
        var options = new FeedMediaDeliveryOptions(
            512 * Mebibyte,
            TimeSpan.FromMinutes(10),
            5,
            2);
        var planner = new FeedVideoDeliveryPlanningService(
            new StubDeliveryRepository(),
            options,
            new AppPaths(directory.Path),
            new StubStorageProbe(availableBytes));

        FeedVideoDeliveryPlan plan = await planner.PlanAsync(
            Entry(declaredBytes),
            Enclosure(declaredBytes),
            CancellationToken.None);

        Assert.Equal(expectedStatus, plan.Status);
        Assert.Equal(expectedConfirmation, plan.RequiresConfirmation);
        Assert.Equal(declaredBytes, plan.DeclaredBytes);
        Assert.Equal(options.MaximumBytes, plan.MaximumBytes);
        Assert.Equal(availableBytes, plan.AvailableBytes);
        Assert.Equal(
            Path.Combine(directory.Path, "Data", "FeedMedia"),
            plan.TargetDirectory);
        Assert.Equal(
            declaredBytes ?? options.MaximumBytes,
            plan.RequiredMediaBytes);
    }

    [Fact]
    public async Task ExistingIdempotentDeliveryNeedsNoDownloadConfirmation()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        Directory.CreateDirectory(paths.FeedMediaDirectory);
        string inputPath = Path.Combine(
            paths.FeedMediaDirectory,
            "existing.mp4");
        await File.WriteAllBytesAsync(inputPath, [0, 1, 2, 3]);
        FeedEntry entry = Entry(100 * Mebibyte);
        FeedEnclosure enclosure = Enclosure(100 * Mebibyte);
        var repository = new StubDeliveryRepository(
            Registration(entry, enclosure, inputPath));
        var planner = new FeedVideoDeliveryPlanningService(
            repository,
            FeedMediaDeliveryOptions.Default,
            paths,
            new StubStorageProbe(0));

        FeedVideoDeliveryPlan plan = await planner.PlanAsync(
            entry,
            enclosure,
            CancellationToken.None);

        Assert.Equal(
            FeedVideoDeliveryPlanStatus.AlreadyAvailable,
            plan.Status);
        Assert.True(plan.AlreadyAvailable);
        Assert.False(plan.RequiresConfirmation);
        Assert.True(plan.CanDeliver);
    }

    private static FeedEntry Entry(long? length) =>
        new(
            "video-entry",
            "30000000-0000-4000-8000-000000000001",
            "video-entry",
            "https://example.com/video-entry",
            "Video entry",
            "Author",
            Now,
            Now,
            "Summary",
            "Content",
            [],
            [Enclosure(length)],
            new string('a', 64),
            Now);

    private static FeedEnclosure Enclosure(long? length) =>
        new(
            "https://cdn.example/video-entry.mp4",
            "video/mp4",
            length,
            "Video");

    private static FeedMediaDeliveryRegistration Registration(
        FeedEntry entry,
        FeedEnclosure enclosure,
        string inputPath)
    {
        var job = new MediaJob(
            "feed-video-job",
            "FeedTranscription",
            inputPath,
            null,
            MediaJobStatus.Queued,
            0,
            TranscriptionEngine.Groq,
            "whisper-large-v3",
            0,
            0,
            null,
            Now,
            Now);
        return new(
            new(
                entry.Id,
                entry.FeedId,
                entry.Title,
                enclosure.Url,
                enclosure.Title,
                enclosure.MediaType!,
                enclosure.Length,
                job.Id,
                Now),
            job,
            Created: false);
    }

    private sealed class StubDeliveryRepository(
        FeedMediaDeliveryRegistration? existing = null)
        : IFeedMediaDeliveryRepository
    {
        public Task<FeedMediaDeliveryRegistration?> GetAsync(
            string entryId,
            string sourceUrl,
            CancellationToken cancellationToken) =>
            Task.FromResult(existing);

        public Task<FeedMediaDeliveryRegistration> CreateOrGetQueuedAsync(
            FeedMediaDelivery delivery,
            MediaJob queuedJob,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubStorageProbe(long availableBytes)
        : IFeedMediaStorageProbe
    {
        public long GetAvailableBytes(string targetDirectory) =>
            availableBytes;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "LenxTool.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
