using System.IO;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.App.Services;

public enum FeedVideoDeliveryPlanStatus
{
    Ready,
    AlreadyAvailable,
    ExceedsLimit,
    InsufficientSpace
}

public sealed record FeedVideoDeliveryPlan(
    string EntryId,
    string SourceUrl,
    string TargetDirectory,
    long? DeclaredBytes,
    long RequiredMediaBytes,
    long MaximumBytes,
    long AvailableBytes,
    FeedVideoDeliveryPlanStatus Status,
    bool RequiresConfirmation,
    bool AlreadyAvailable)
{
    public bool CanDeliver =>
        Status is
            FeedVideoDeliveryPlanStatus.Ready or
            FeedVideoDeliveryPlanStatus.AlreadyAvailable;
}

public interface IFeedMediaStorageProbe
{
    long GetAvailableBytes(string targetDirectory);
}

public interface IFeedVideoDeliveryPlanningService
{
    Task<FeedVideoDeliveryPlan> PlanAsync(
        FeedEntry entry,
        FeedEnclosure enclosure,
        CancellationToken cancellationToken);
}

public sealed class FeedMediaStorageProbe : IFeedMediaStorageProbe
{
    public long GetAvailableBytes(string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        string fullPath = Path.GetFullPath(targetDirectory);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("无法确定媒体目标目录所在磁盘。");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}

public sealed class FeedVideoDeliveryPlanningService :
    IFeedVideoDeliveryPlanningService
{
    public const long LargeMediaConfirmationBytes =
        20L * 1024 * 1024;
    private readonly IFeedMediaDeliveryRepository _repository;
    private readonly FeedMediaDeliveryOptions _options;
    private readonly AppPaths _paths;
    private readonly IFeedMediaStorageProbe _storage;

    public FeedVideoDeliveryPlanningService(
        IFeedMediaDeliveryRepository repository,
        FeedMediaDeliveryOptions options,
        AppPaths paths,
        IFeedMediaStorageProbe storage)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(storage);
        _repository = repository;
        _options = options;
        _paths = paths;
        _storage = storage;
    }

    public async Task<FeedVideoDeliveryPlan> PlanAsync(
        FeedEntry entry,
        FeedEnclosure enclosure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(enclosure);
        FeedAttachmentClassification attachment =
            FeedAttachmentClassifier.Classify(
                enclosure,
                entry.NormalizedUrl);
        if (attachment.UrlStatus != FeedAttachmentUrlStatus.Allowed
            || !attachment.IsTypeVerified
            || attachment.Kind != FeedAttachmentKind.Video)
        {
            throw new InvalidDataException(
                "视频附件未通过地址与类型一致性验证。");
        }

        string targetDirectory =
            Path.GetFullPath(_paths.FeedMediaDirectory);
        long requiredMediaBytes =
            attachment.Length ?? _options.MaximumBytes;
        FeedMediaDeliveryRegistration? existing =
            await _repository.GetAsync(
                entry.Id,
                attachment.SafeUrl!,
                cancellationToken).ConfigureAwait(false);
        bool alreadyAvailable =
            existing is not null
            && File.Exists(existing.Job.InputPath);
        long availableBytes = 0;
        if (!alreadyAvailable
            && requiredMediaBytes <= _options.MaximumBytes)
        {
            availableBytes = Math.Max(
                0,
                _storage.GetAvailableBytes(targetDirectory));
        }

        FeedVideoDeliveryPlanStatus status;
        if (alreadyAvailable)
        {
            status = FeedVideoDeliveryPlanStatus.AlreadyAvailable;
        }
        else if (requiredMediaBytes > _options.MaximumBytes)
        {
            status = FeedVideoDeliveryPlanStatus.ExceedsLimit;
        }
        else if (!HasSpace(availableBytes, requiredMediaBytes))
        {
            status = FeedVideoDeliveryPlanStatus.InsufficientSpace;
        }
        else
        {
            status = FeedVideoDeliveryPlanStatus.Ready;
        }

        bool requiresConfirmation =
            status == FeedVideoDeliveryPlanStatus.Ready
            && (attachment.Length is null
                || attachment.Length >= LargeMediaConfirmationBytes);
        return new(
            entry.Id,
            attachment.SafeUrl!,
            targetDirectory,
            attachment.Length,
            requiredMediaBytes,
            _options.MaximumBytes,
            availableBytes,
            status,
            requiresConfirmation,
            alreadyAvailable);
    }

    private static bool HasSpace(
        long availableBytes,
        long requiredMediaBytes)
    {
        long requiredWithReserve =
            requiredMediaBytes
            > long.MaxValue
                - FeedMediaDeliveryOptions.MinimumFreeSpaceReserveBytes
                ? long.MaxValue
                : requiredMediaBytes
                    + FeedMediaDeliveryOptions
                        .MinimumFreeSpaceReserveBytes;
        return availableBytes >= requiredWithReserve;
    }
}
