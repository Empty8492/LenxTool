using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace LenxTool.App.Controls;

public sealed class ArticleImageDownloader
{
    private readonly HttpClient _httpClient;
    private readonly int _maximumBytes;

    public ArticleImageDownloader(HttpClient httpClient, int maximumBytes = 12 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _httpClient = httpClient;
        _maximumBytes = maximumBytes;
    }

    public async Task<byte[]> DownloadAsync(
        string imageUrl,
        string? referrer,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException("图片地址必须使用 HTTP 或 HTTPS。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LenxTool", "0.1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        if (Uri.TryCreate(referrer, UriKind.Absolute, out Uri? referrerUri)
            && referrerUri.Scheme is "http" or "https")
        {
            request.Headers.Referrer = referrerUri;
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("远程地址返回的不是图片。");
        }

        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength > _maximumBytes)
        {
            throw new InvalidDataException("图片超过允许的大小。");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > _maximumBytes)
            {
                throw new InvalidDataException("图片超过允许的大小。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return destination.ToArray();
    }
}
