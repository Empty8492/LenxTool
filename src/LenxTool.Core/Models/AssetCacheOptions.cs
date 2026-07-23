namespace LenxTool.Core.Models;

public sealed record AssetCacheOptions(
    long MaximumBytes,
    long MaximumAssetBytes)
{
    public static AssetCacheOptions Default { get; } = new(
        MaximumBytes: 512L * 1024 * 1024,
        MaximumAssetBytes: 12L * 1024 * 1024);
}
