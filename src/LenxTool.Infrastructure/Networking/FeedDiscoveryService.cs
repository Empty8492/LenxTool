using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal sealed class FeedDiscoveryService : IFeedDiscoveryService
{
    private readonly IFeedDiscoveryTransport _transport;
    private readonly FeedDiscoveryOptions _options;
    private readonly FeedNetworkPolicy _networkPolicy;

    public FeedDiscoveryService(
        IFeedHostResolver resolver,
        IFeedDiscoveryTransport transport,
        FeedDiscoveryOptions options)
    {
        _transport = transport;
        _options = ValidateOptions(options);
        _networkPolicy = new(resolver, options);
    }

    public async Task<FeedDiscoveryResult> DiscoverAsync(
        string url,
        CancellationToken cancellationToken)
    {
        Uri requestedUri = _networkPolicy.ParseAndValidate(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TotalTimeout);
        try
        {
            FetchedDocument initial = await FetchAsync(requestedUri, timeout.Token).ConfigureAwait(false);
            if (IsXmlMediaType(initial.MediaType))
            {
                ParsedFeed parsed = ParseFeed(initial.Content);
                return new(
                    requestedUri.AbsoluteUri,
                    [new(initial.FinalUri.AbsoluteUri, parsed.Title, parsed.Kind)]);
            }
            if (!IsHtmlMediaType(initial.MediaType)) throw InvalidResponse();

            string html = DecodeHtml(initial.Content, initial.Charset);
            IReadOnlyList<HtmlFeedLink> links = HtmlFeedLinkExtractor.Extract(
                html,
                initial.FinalUri,
                _options.MaximumCandidates);
            var feeds = new List<DiscoveredFeed>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (HtmlFeedLink link in links)
            {
                try
                {
                    FetchedDocument candidate = await FetchAsync(link.Uri, timeout.Token).ConfigureAwait(false);
                    if (!IsXmlMediaType(candidate.MediaType)) continue;
                    ParsedFeed parsed = ParseFeed(candidate.Content);
                    if (!seen.Add(candidate.FinalUri.AbsoluteUri)) continue;
                    feeds.Add(new(
                        candidate.FinalUri.AbsoluteUri,
                        NormalizeTitle(link.Title) ?? parsed.Title,
                        parsed.Kind));
                }
                catch (AppException)
                {
                    // An invalid candidate is isolated; no unsafe endpoint is contacted.
                }
                catch (HttpRequestException)
                {
                }
                catch (InvalidDataException)
                {
                }
                catch (XmlException)
                {
                }
                catch (DecoderFallbackException)
                {
                }
            }
            if (feeds.Count == 0) throw InvalidResponse("页面没有可安全验证的 RSS/Atom 链接。");
            return new(requestedUri.AbsoluteUri, feeds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new AppException(AppErrorFactory.FromTimeout("Feed 发现"));
        }
        catch (HttpRequestException)
        {
            throw new AppException(AppErrorFactory.FromNetwork("Feed 发现"));
        }
        catch (AppException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw InvalidResponse();
        }
        catch (XmlException)
        {
            throw InvalidResponse();
        }
        catch (DecoderFallbackException)
        {
            throw InvalidResponse();
        }
    }

    private async Task<FetchedDocument> FetchAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        Uri current = _networkPolicy.ParseAndValidate(initialUri.AbsoluteUri);
        var visited = new HashSet<string>(StringComparer.Ordinal) { current.AbsoluteUri };
        int redirects = 0;
        while (true)
        {
            IReadOnlyList<IPAddress> addresses = await _networkPolicy
                .ResolveAllowedAsync(current, cancellationToken)
                .ConfigureAwait(false);
            using FeedDiscoveryHttpResponse ownedResponse = await _transport
                .SendAsync(current, addresses, cancellationToken)
                .ConfigureAwait(false);
            HttpResponseMessage response = ownedResponse.Message;
            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= _options.MaximumRedirects || response.Headers.Location is null)
                    throw InvalidResponse("Feed 重定向次数过多或缺少目标地址。");
                Uri redirected;
                try
                {
                    redirected = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                }
                catch (UriFormatException)
                {
                    throw InvalidResponse();
                }
                redirected = _networkPolicy.ParseAndValidate(redirected.AbsoluteUri);
                if (!visited.Add(redirected.AbsoluteUri))
                    throw InvalidResponse("Feed 重定向形成循环。");
                current = redirected;
                redirects++;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AppException(AppErrorFactory.FromHttp(
                    response.StatusCode,
                    "Feed 发现"));
            }
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType)) throw InvalidResponse("Feed 响应缺少 MIME 类型。");
            if (!IsXmlMediaType(mediaType) && !IsHtmlMediaType(mediaType))
                throw InvalidResponse("Feed 响应 MIME 类型不受支持。");
            byte[] content = await ReadContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return new(
                current,
                mediaType,
                response.Content.Headers.ContentType?.CharSet,
                content);
        }
    }

    private async Task<byte[]> ReadContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaximumCompressedBytes)
            throw InvalidResponse("Feed 压缩响应超过大小限制。");

        await using Stream network = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] compressed = await ReadBoundedAsync(
            network,
            _options.MaximumCompressedBytes,
            cancellationToken).ConfigureAwait(false);
        string[] encodings = content.Headers.ContentEncoding.ToArray();
        if (encodings.Length == 0
            || (encodings.Length == 1 && encodings[0].Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            if (compressed.Length > _options.MaximumDecompressedBytes)
                throw InvalidResponse("Feed 响应超过解压后大小限制。");
            return compressed;
        }
        if (encodings.Length != 1) throw InvalidResponse("Feed 使用了不支持的多重压缩。");

        using var input = new MemoryStream(compressed, writable: false);
        await using Stream decompressor = encodings[0].ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            "deflate" => new DeflateStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            _ => throw InvalidResponse("Feed 使用了不支持的内容压缩。")
        };
        return await ReadBoundedAsync(
            decompressor,
            _options.MaximumDecompressedBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw InvalidResponse("Feed 响应超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private ParsedFeed ParseFeed(byte[] content)
    {
        using var input = new MemoryStream(content, writable: false);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = _options.MaximumDecompressedBytes,
            CloseInput = true
        };
        using XmlReader reader = XmlReader.Create(input, settings);
        reader.MoveToContent();
        FeedDocumentKind kind;
        if (reader.LocalName == "rss"
            && reader.NamespaceURI.Length == 0
            && reader.GetAttribute("version") == "2.0")
        {
            kind = FeedDocumentKind.Rss20;
        }
        else if (reader.LocalName == "feed" && reader.NamespaceURI == "http://www.w3.org/2005/Atom")
        {
            kind = FeedDocumentKind.Atom;
        }
        else
        {
            throw InvalidResponse("XML 根元素不是 RSS 2.0 或 Atom。");
        }

        string? title = null;
        int rootDepth = reader.Depth;
        bool inRssChannel = false;
        bool sawRssChannel = false;
        while (reader.Read())
        {
            if (kind == FeedDocumentKind.Rss20
                && reader.NodeType == XmlNodeType.Element
                && reader.Depth == rootDepth + 1
                && reader.LocalName == "channel")
            {
                inRssChannel = true;
                sawRssChannel = true;
                continue;
            }
            bool isTitle = reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "title"
                && ((kind == FeedDocumentKind.Atom && reader.Depth == rootDepth + 1)
                    || (kind == FeedDocumentKind.Rss20 && inRssChannel && reader.Depth == rootDepth + 2));
            if (!isTitle || title is not null) continue;
            title = NormalizeTitle(reader.ReadString());
        }
        if (kind == FeedDocumentKind.Rss20 && !sawRssChannel)
            throw InvalidResponse("RSS 2.0 文档缺少 channel 元素。");
        return new(kind, title);
    }

    private static string DecodeHtml(byte[] content, string? charset)
    {
        Encoding encoding = DetectBomEncoding(content) ?? new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        if (!string.IsNullOrWhiteSpace(charset))
        {
            string normalized = charset.Trim().Trim('"', '\'');
            try
            {
                encoding = Encoding.GetEncoding(
                    normalized,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                throw InvalidResponse("发现页面声明了不支持的字符编码。");
            }
        }
        return encoding.GetString(content);
    }

    private static Encoding? DetectBomEncoding(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            return new UTF8Encoding(false, true);
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
            return new UnicodeEncoding(false, true, true);
        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
            return new UnicodeEncoding(true, true, true);
        return null;
    }

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string normalized = string.Concat(title.Trim().Select(character =>
            char.IsControl(character) ? ' ' : character));
        if (normalized.Length > 200) normalized = normalized[..200];
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool IsXmlMediaType(string mediaType) =>
        mediaType.Equals("application/rss+xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/atom+xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsHtmlMediaType(string mediaType) =>
        mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static FeedDiscoveryOptions ValidateOptions(FeedDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.AllowedHttpHosts);
        ArgumentNullException.ThrowIfNull(options.TrustedPrivateHosts);
        if (options.TotalTimeout <= TimeSpan.Zero
            || options.ConnectTimeout <= TimeSpan.Zero
            || options.ConnectTimeout > options.TotalTimeout
            || options.MaximumRedirects is < 0 or > 10
            || options.MaximumCandidates is < 1 or > 100
            || options.MaximumCompressedBytes is < 1024 or > 10 * 1024 * 1024
            || options.MaximumDecompressedBytes < options.MaximumCompressedBytes
            || options.MaximumDecompressedBytes > 20 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return options;
    }

    private static AppException InvalidResponse(string? detail = null) => new(new(
        AppErrorCode.ProviderUnavailable,
        "Feed 无法安全验证",
        detail ?? "远程地址没有返回可安全识别的 RSS、Atom 或发现页面。",
        "请检查地址和服务状态后重试。",
        Provider: "Feed 发现",
        IsRetryable: true));

    private sealed record FetchedDocument(
        Uri FinalUri,
        string MediaType,
        string? Charset,
        byte[] Content);

    private sealed record ParsedFeed(FeedDocumentKind Kind, string? Title);
}
