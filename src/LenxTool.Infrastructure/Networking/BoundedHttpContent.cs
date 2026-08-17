namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// Reads an HTTP response body without trusting Content-Length and stops as soon
/// as the configured byte budget is exceeded.
/// </summary>
internal static class BoundedHttpContent
{
    private const int BufferSize = 16 * 1024;

    public static Task<byte[]> ReadAsByteArrayAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        ReadCoreAsync(content, maximumBytes, cancellationToken);

    public static async Task<byte[]> ReadAsByteArrayAsync(
        HttpContent content,
        int maximumBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            timeout,
            TimeSpan.Zero);
        using var deadline = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return await ReadCoreAsync(
                content,
                maximumBytes,
                deadline.Token)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadCoreAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfEqual(maximumBytes, int.MaxValue);
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("HTTP response exceeds its byte budget.");
        }

        await using Stream input = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream(
            Math.Min(maximumBytes + 1, BufferSize));
        byte[] buffer = new byte[
            Math.Min(maximumBytes + 1, BufferSize)];
        int total = 0;

        while (true)
        {
            int requested = Math.Min(buffer.Length, maximumBytes + 1 - total);
            int read = await input
                .ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException(
                    "HTTP response exceeds its byte budget.");
            }
            output.Write(buffer, 0, read);
        }
    }
}
