using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal sealed partial class ArticleContentExtractor :
    IArticleContentExtractor,
    IDisposable
{
    private const int BufferSize = 80 * 1024;
    private readonly IArticleContentTransport _transport;
    private readonly ArticleContentExtractionOptions _options;
    private readonly HtmlArticleContentParser _parser;
    private readonly FeedNetworkPolicy _networkPolicy;
    private readonly PerHostConcurrencyLimiter _hostLimiter;
    private bool _disposed;

    static ArticleContentExtractor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ArticleContentExtractor(
        IFeedHostResolver resolver,
        IArticleContentTransport transport,
        FeedDiscoveryOptions feedOptions,
        ArticleContentExtractionOptions options,
        HtmlArticleContentParser parser)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(feedOptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parser);
        ValidateOptions(options);

        _transport = transport;
        _options = options;
        _parser = parser;
        _networkPolicy = new(resolver, feedOptions);
        _hostLimiter = new(options.MaximumConcurrentRequestsPerHost);
    }

    public async Task<ArticleContentResult> ExtractAsync(
        string url,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri requestedUri = _networkPolicy.ParseAndValidate(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.TotalTimeout);
        try
        {
            FetchedArticle fetched = await FetchAsync(
                requestedUri,
                timeout.Token).ConfigureAwait(false);
            var warnings = new List<ArticleExtractionWarning>();
            string html = DecodeHtml(
                fetched.Content,
                fetched.Charset,
                warnings);
            return _parser.Parse(
                requestedUri.AbsoluteUri,
                fetched.FinalUri,
                html,
                warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new AppException(AppErrorFactory.FromTimeout("文章全文提取"));
        }
        catch (HttpRequestException)
        {
            throw new AppException(AppErrorFactory.FromNetwork("文章全文提取"));
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or DecoderFallbackException
                or NotSupportedException
                or ArgumentException)
        {
            throw InvalidResponse(exception.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _hostLimiter.Dispose();
    }

    private async Task<FetchedArticle> FetchAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        Uri current = _networkPolicy.ParseAndValidate(initialUri.AbsoluteUri);
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            current.AbsoluteUri
        };
        int redirects = 0;
        while (true)
        {
            using PerHostConcurrencyLimiter.Lease hostLease =
                await _hostLimiter.AcquireAsync(
                    current.IdnHost,
                    cancellationToken).ConfigureAwait(false);
            IReadOnlyList<IPAddress> addresses = await _networkPolicy
                .ResolveAllowedAsync(current, cancellationToken)
                .ConfigureAwait(false);
            using ArticleContentHttpResponse ownedResponse = await _transport
                .SendAsync(current, addresses, cancellationToken)
                .ConfigureAwait(false);
            HttpResponseMessage response = ownedResponse.Message;
            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= _options.MaximumRedirects
                    || response.Headers.Location is null)
                {
                    throw new InvalidDataException(
                        "文章重定向次数过多或缺少目标地址。");
                }

                Uri redirected;
                try
                {
                    redirected = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                }
                catch (UriFormatException exception)
                {
                    throw new InvalidDataException(
                        "文章重定向地址无效。",
                        exception);
                }

                current = _networkPolicy.ParseAndValidate(
                    redirected.AbsoluteUri);
                if (!visited.Add(current.AbsoluteUri))
                {
                    throw new InvalidDataException("文章重定向形成循环。");
                }
                redirects++;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AppException(AppErrorFactory.FromHttp(
                    response.StatusCode,
                    "文章全文提取"));
            }
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!IsHtmlMediaType(mediaType))
            {
                throw new InvalidDataException(
                    "文章响应 MIME 类型必须是 HTML。");
            }
            byte[] content = await ReadContentAsync(
                response.Content,
                cancellationToken).ConfigureAwait(false);
            return new(
                current,
                response.Content.Headers.ContentType?.CharSet,
                content);
        }
    }

    private async Task<byte[]> ReadContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaximumDownloadBytes)
        {
            throw new InvalidDataException("文章下载响应超过大小限制。");
        }

        await using Stream network = await content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] downloaded = await ReadBoundedAsync(
            network,
            _options.MaximumDownloadBytes,
            cancellationToken).ConfigureAwait(false);
        string[] encodings = content.Headers.ContentEncoding.ToArray();
        if (encodings.Length == 0
            || (encodings.Length == 1
                && encodings[0].Equals(
                    "identity",
                    StringComparison.OrdinalIgnoreCase)))
        {
            if (downloaded.Length > _options.MaximumDecodedBytes)
            {
                throw new InvalidDataException(
                    "文章响应超过解码后大小限制。");
            }
            return downloaded;
        }
        if (encodings.Length != 1)
        {
            throw new InvalidDataException(
                "文章响应使用了不支持的多重压缩。");
        }

        using var input = new MemoryStream(downloaded, writable: false);
        await using Stream decompressor = encodings[0].ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            "deflate" => new DeflateStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            _ => throw new InvalidDataException(
                "文章响应使用了不支持的压缩格式。")
        };
        return await ReadBoundedAsync(
            decompressor,
            _options.MaximumDecodedBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    "文章响应超过大小限制。");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string DecodeHtml(
        byte[] content,
        string? headerCharset,
        List<ArticleExtractionWarning> warnings)
    {
        Encoding? encoding = DetectBom(content, out int bomLength);
        if (encoding is null)
        {
            string? charset = NormalizeCharset(headerCharset);
            charset ??= ReadMetaCharset(content);
            if (charset is not null)
            {
                try
                {
                    encoding = StrictEncoding(charset);
                }
                catch (ArgumentException)
                {
                    warnings.Add(new(
                        ArticleExtractionWarningCode.EncodingFallback,
                        "页面声明了不受支持的字符编码，已尝试 UTF-8。"));
                }
            }
        }

        if (encoding is not null)
        {
            return TrimBom(encoding.GetString(content, bomLength, content.Length - bomLength));
        }

        try
        {
            return TrimBom(StrictEncoding("utf-8").GetString(content));
        }
        catch (DecoderFallbackException)
        {
            warnings.Add(new(
                ArticleExtractionWarningCode.EncodingFallback,
                "页面未提供有效字符编码，已按 Windows-1252 兼容解码。"));
            return TrimBom(StrictEncoding("windows-1252").GetString(content));
        }
    }

    private static Encoding? DetectBom(byte[] content, out int bomLength)
    {
        ReadOnlySpan<byte> bytes = content;
        if (bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF)
        {
            bomLength = 3;
            return StrictEncoding("utf-8");
        }
        if (bytes.Length >= 2
            && bytes[0] == 0xFF
            && bytes[1] == 0xFE)
        {
            bomLength = 2;
            return new UnicodeEncoding(
                bigEndian: false,
                byteOrderMark: false,
                throwOnInvalidBytes: true);
        }
        if (bytes.Length >= 2
            && bytes[0] == 0xFE
            && bytes[1] == 0xFF)
        {
            bomLength = 2;
            return new UnicodeEncoding(
                bigEndian: true,
                byteOrderMark: false,
                throwOnInvalidBytes: true);
        }
        bomLength = 0;
        return null;
    }

    private static string? ReadMetaCharset(byte[] content)
    {
        int length = Math.Min(content.Length, 8 * 1024);
        string prefix = Encoding.Latin1.GetString(content, 0, length);
        Match match = CharsetPattern().Match(prefix);
        return match.Success ? NormalizeCharset(match.Groups[1].Value) : null;
    }

    private static string? NormalizeCharset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string normalized = value.Trim().Trim('"', '\'');
        return normalized.Length is > 0 and <= 64
            && normalized.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':')
            ? normalized
            : null;
    }

    private static Encoding StrictEncoding(string name) =>
        Encoding.GetEncoding(
            name,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

    private static string TrimBom(string value) =>
        value.Length > 0 && value[0] == '\uFEFF'
            ? value[1..]
            : value;

    private static bool IsHtmlMediaType(string? mediaType) =>
        mediaType?.Trim().ToLowerInvariant() is
            "text/html"
            or "application/xhtml+xml";

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static AppException InvalidResponse(string detail) => new(new(
        AppErrorCode.InvalidRequest,
        "文章页面无效",
        string.IsNullOrWhiteSpace(detail)
            ? "页面无法在安全限制内解析为文章正文。"
            : detail,
        "请确认地址返回标准 HTML，且页面没有超过大小、压缩或节点上限。",
        Provider: "文章全文提取"));

    private static void ValidateOptions(ArticleContentExtractionOptions options)
    {
        if (options.TotalTimeout <= TimeSpan.Zero
            || options.MaximumRedirects < 0
            || options.MaximumDownloadBytes <= 0
            || options.MaximumDecodedBytes <= 0
            || options.MaximumConcurrentRequestsPerHost <= 0
            || options.MaximumNestingDepth <= 0
            || options.MaximumDocumentNodes <= 0
            || options.MaximumBlocks <= 0
            || options.MaximumTotalTextCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    [GeneratedRegex(
        """<meta\b[^>]{0,1024}\bcharset\s*=\s*["']?\s*([A-Za-z0-9._:-]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CharsetPattern();

    private sealed record FetchedArticle(
        Uri FinalUri,
        string? Charset,
        byte[] Content);
}

internal sealed class PerHostConcurrencyLimiter : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, HostGate> _gates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maximumConcurrency;
    private bool _disposed;

    public PerHostConcurrencyLimiter(int maximumConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrency);
        _maximumConcurrency = maximumConcurrency;
    }

    public async ValueTask<Lease> AcquireAsync(
        string host,
        CancellationToken cancellationToken)
    {
        HostGate gate;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_gates.TryGetValue(host, out gate!))
            {
                gate = new(new(_maximumConcurrency));
                _gates.Add(host, gate);
            }
            gate.ReferenceCount++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(this, host, gate);
        }
        catch
        {
            ReleaseReference(host, gate);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private void Release(string host, HostGate gate)
    {
        gate.Semaphore.Release();
        ReleaseReference(host, gate);
    }

    private void ReleaseReference(string host, HostGate gate)
    {
        bool dispose = false;
        lock (_sync)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount == 0
                && _gates.TryGetValue(host, out HostGate? current)
                && ReferenceEquals(current, gate))
            {
                _gates.Remove(host);
                dispose = true;
            }
        }
        if (dispose)
        {
            gate.Semaphore.Dispose();
        }
    }

    internal sealed class HostGate(SemaphoreSlim semaphore)
    {
        public SemaphoreSlim Semaphore { get; } = semaphore;
        public int ReferenceCount { get; set; }
    }

    public sealed class Lease : IDisposable
    {
        private PerHostConcurrencyLimiter? _owner;
        private readonly string _host;
        private readonly HostGate _gate;

        internal Lease(
            PerHostConcurrencyLimiter owner,
            string host,
            HostGate gate)
        {
            _owner = owner;
            _host = host;
            _gate = gate;
        }

        public void Dispose()
        {
            PerHostConcurrencyLimiter? owner = Interlocked.Exchange(
                ref _owner,
                null);
            owner?.Release(_host, _gate);
        }
    }
}
