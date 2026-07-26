namespace LenxTool.Core.Models;

public static class StorageRetentionPolicy
{
    public const int DefaultDays = 180;
}

public sealed record LocalStorageUsage(
    long DatabaseBytes,
    long ImageCacheBytes,
    int ImageFileCount,
    long ModelBytes,
    int ModelFileCount)
{
    public long TotalBytes => checked(
        DatabaseBytes + ImageCacheBytes + ModelBytes);
}

public sealed record StorageCleanupPreview(
    DateTimeOffset Cutoff,
    int ExpiredFeedEntryCount,
    int ReclaimableImageFileCount,
    long ReclaimableImageBytes);

public sealed record StorageCleanupResult(
    DateTimeOffset Cutoff,
    int DeletedFeedEntryCount,
    int RemovedImageFileCount,
    long ReclaimedImageBytes,
    bool DatabaseOptimized,
    LocalStorageUsage Usage);

public sealed record EntryAssetPruneResult(
    int RemovedFileCount,
    long RemovedBytes);
