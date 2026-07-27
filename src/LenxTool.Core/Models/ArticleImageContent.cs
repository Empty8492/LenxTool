namespace LenxTool.Core.Models;

public sealed record ArticleImageContent(
    byte[] Bytes,
    string MimeType,
    bool FromCache);

public sealed class ArticleImageStreamContent(
    Stream stream,
    string mimeType,
    bool fromCache) : IAsyncDisposable, IDisposable
{
    public Stream Stream { get; } = stream ?? throw new ArgumentNullException(nameof(stream));
    public string MimeType { get; } = string.IsNullOrWhiteSpace(mimeType)
        ? throw new ArgumentException("MIME 类型不能为空。", nameof(mimeType))
        : mimeType;
    public bool FromCache { get; } = fromCache;

    public void Dispose() => Stream.Dispose();

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public sealed class ArticleImageDownloadBudget
{
    private readonly object _gate = new();
    private readonly HashSet<string> _resources = new(StringComparer.Ordinal);
    private long _networkBytes;

    public ArticleImageDownloadBudget(
        int maximumResources,
        long maximumNetworkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResources);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNetworkBytes);
        MaximumResources = maximumResources;
        MaximumNetworkBytes = maximumNetworkBytes;
    }

    public int MaximumResources { get; }
    public long MaximumNetworkBytes { get; }

    public long RemainingNetworkBytes
    {
        get
        {
            lock (_gate)
            {
                return MaximumNetworkBytes - _networkBytes;
            }
        }
    }

    public bool TryReserveResource(string sourceUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);
        lock (_gate)
        {
            if (_resources.Contains(sourceUrl))
            {
                return true;
            }
            if (_resources.Count >= MaximumResources)
            {
                return false;
            }
            _resources.Add(sourceUrl);
            return true;
        }
    }

    public bool TryConsumeNetworkBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_gate)
        {
            if (count > MaximumNetworkBytes - _networkBytes)
            {
                return false;
            }
            _networkBytes += count;
            return true;
        }
    }
}
