using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal sealed class FeedDocumentParser : IFeedParser
{
    private static readonly XNamespace AtomNamespace = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace ContentNamespace = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace MediaNamespace = "http://search.yahoo.com/mrss/";
    private const int MaximumEnclosuresPerEntry = 32;
    private readonly FeedParserOptions _options;

    public FeedDocumentParser() : this(FeedParserOptions.Default)
    {
    }

    public FeedDocumentParser(FeedParserOptions options)
    {
        if (options.MaximumDocumentBytes is < 1024 or > 20 * 1024 * 1024
            || options.MaximumEntries is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        _options = options;
    }

    public ParsedFeedDocument Parse(
        string feedId,
        string feedUrl,
        ReadOnlyMemory<byte> content,
        DateTimeOffset fetchedAt)
    {
        if (!Guid.TryParseExact(feedId, "D", out _)
            || content.IsEmpty
            || content.Length > _options.MaximumDocumentBytes
            || !Uri.TryCreate(feedUrl, UriKind.Absolute, out Uri? baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            throw InvalidDocument("Feed 标识、地址或文档大小无效。");
        }

        XDocument document;
        try
        {
            using var input = new MemoryStream(content.ToArray(), writable: false);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = _options.MaximumDocumentBytes,
                CloseInput = true
            };
            using XmlReader reader = XmlReader.Create(input, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw InvalidDocument("Feed XML 格式无效或包含被禁止的 DTD/实体。");
        }

        XElement root = document.Root ?? throw InvalidDocument("Feed XML 缺少根元素。");
        return root.Name.LocalName switch
        {
            "rss" when root.Name.NamespaceName.Length == 0 && (string?)root.Attribute("version") == "2.0" =>
                ParseRss(root, feedId, baseUri, fetchedAt),
            "feed" when root.Name.Namespace == AtomNamespace =>
                ParseAtom(root, feedId, baseUri, fetchedAt),
            _ => throw InvalidDocument("仅支持 RSS 2.0 和 Atom 文档。")
        };
    }

    private ParsedFeedDocument ParseRss(
        XElement root,
        string feedId,
        Uri baseUri,
        DateTimeOffset fetchedAt)
    {
        XElement channel = root.Elements().FirstOrDefault(element => element.Name.LocalName == "channel")
            ?? throw InvalidDocument("RSS 2.0 文档缺少 channel。");
        string title = CleanTitle(Value(channel, "title"), "未命名 Feed");
        string? siteUrl = FeedUrlNormalizer.Normalize(Value(channel, "link"), baseUri);
        XElement[] items = channel.Elements().Where(element => element.Name.LocalName == "item").ToArray();
        EnsureEntryLimit(items.Length);
        string? feedAuthor = CleanOptional(Value(channel, "author"), 256);
        return new(
            title,
            siteUrl,
            FeedDocumentKind.Rss20,
            ParseEntries(items.Select(item => ParseRssEntry(item, feedId, baseUri, fetchedAt, feedAuthor))));
    }

    private FeedEntry ParseRssEntry(
        XElement item,
        string feedId,
        Uri baseUri,
        DateTimeOffset fetchedAt,
        string? feedAuthor)
    {
        string title = CleanTitle(Value(item, "title"), "未命名条目");
        string? author = CleanOptional(
            item.Element(DublinCoreNamespace + "creator")?.Value ?? Value(item, "author"),
            256) ?? feedAuthor;
        DateTimeOffset? published = ParseDate(Value(item, "pubDate") ?? item.Element(DublinCoreNamespace + "date")?.Value);
        string summary = FeedTextSanitizer.Clean(Value(item, "description"), 32 * 1024);
        string? fullContent = item.Element(ContentNamespace + "encoded")?.Value;
        string content = FeedTextSanitizer.Clean(
            fullContent ?? Value(item, "description"),
            _options.MaximumDocumentBytes);
        string? normalizedUrl = FeedUrlNormalizer.Normalize(Value(item, "link"), baseUri);
        IReadOnlyList<string> categories = ReadCategories(
            item.Elements().Where(element => element.Name.LocalName == "category")
                .Select(element => element.Value));
        IReadOnlyList<FeedEnclosure> enclosures = ReadRssEnclosures(item, baseUri);
        string? preferredId = ReadIdentifier(Value(item, "guid"));
        return CreateEntry(
            feedId,
            preferredId,
            normalizedUrl,
            title,
            author,
            published,
            updated: null,
            summary,
            content,
            categories,
            enclosures,
            fetchedAt,
            hasFullContent: !string.IsNullOrWhiteSpace(fullContent) && content.Length > 0);
    }

    private ParsedFeedDocument ParseAtom(
        XElement root,
        string feedId,
        Uri baseUri,
        DateTimeOffset fetchedAt)
    {
        string title = CleanTitle(root.Element(AtomNamespace + "title")?.Value, "未命名 Feed");
        string? siteUrl = ReadAtomAlternateUrl(root, baseUri);
        string? feedAuthor = ReadAtomAuthor(root);
        XElement[] entries = root.Elements(AtomNamespace + "entry").ToArray();
        EnsureEntryLimit(entries.Length);
        return new(
            title,
            siteUrl,
            FeedDocumentKind.Atom,
            ParseEntries(entries.Select(entry => ParseAtomEntry(entry, feedId, baseUri, fetchedAt, feedAuthor))));
    }

    private FeedEntry ParseAtomEntry(
        XElement entry,
        string feedId,
        Uri baseUri,
        DateTimeOffset fetchedAt,
        string? feedAuthor)
    {
        string title = CleanTitle(entry.Element(AtomNamespace + "title")?.Value, "未命名条目");
        string? normalizedUrl = ReadAtomAlternateUrl(entry, baseUri);
        string? author = ReadAtomAuthor(entry) ?? feedAuthor;
        DateTimeOffset? published = ParseDate(entry.Element(AtomNamespace + "published")?.Value);
        DateTimeOffset? updated = ParseDate(entry.Element(AtomNamespace + "updated")?.Value);
        string summary = FeedTextSanitizer.Clean(entry.Element(AtomNamespace + "summary")?.Value, 32 * 1024);
        string? fullContent = entry.Element(AtomNamespace + "content")?.Value;
        string content = FeedTextSanitizer.Clean(
            fullContent ?? entry.Element(AtomNamespace + "summary")?.Value,
            _options.MaximumDocumentBytes);
        IReadOnlyList<string> categories = ReadCategories(
            entry.Elements(AtomNamespace + "category")
                .Select(category => (string?)category.Attribute("label") ?? (string?)category.Attribute("term")));
        IReadOnlyList<FeedEnclosure> enclosures = ReadAtomEnclosures(entry, baseUri);
        string? preferredId = ReadIdentifier(entry.Element(AtomNamespace + "id")?.Value);
        return CreateEntry(
            feedId,
            preferredId,
            normalizedUrl,
            title,
            author,
            published,
            updated,
            summary,
            content,
            categories,
            enclosures,
            fetchedAt,
            hasFullContent: !string.IsNullOrWhiteSpace(fullContent) && content.Length > 0);
    }

    private static List<FeedEntry> ParseEntries(IEnumerable<FeedEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<FeedEntry>();
        foreach (FeedEntry entry in entries)
        {
            if (seen.Add(entry.ExternalId)) parsed.Add(entry);
        }
        return parsed;
    }

    private static FeedEntry CreateEntry(
        string feedId,
        string? preferredId,
        string? normalizedUrl,
        string title,
        string? author,
        DateTimeOffset? published,
        DateTimeOffset? updated,
        string summary,
        string content,
        IReadOnlyList<string> categories,
        IReadOnlyList<FeedEnclosure> enclosures,
        DateTimeOffset fetchedAt,
        bool hasFullContent)
    {
        string fallbackHash = CreateHash(
            feedId,
            title,
            author,
            published?.ToString("O", CultureInfo.InvariantCulture),
            updated?.ToString("O", CultureInfo.InvariantCulture),
            summary,
            content);
        string externalId = preferredId ?? normalizedUrl ?? $"urn:lenxtool:fingerprint:{fallbackHash}";
        string id = CreateHash(feedId, externalId);
        string enclosureFingerprint = string.Join(
            '\n',
            enclosures.Select(enclosure =>
                $"{enclosure.Url}|{enclosure.MediaType}|{enclosure.Length}|{enclosure.Title}"));
        string contentHash = CreateHash(
            title,
            author,
            published?.ToString("O", CultureInfo.InvariantCulture),
            updated?.ToString("O", CultureInfo.InvariantCulture),
            summary,
            content,
            string.Join('\n', categories),
            enclosureFingerprint);
        return new(
            id,
            feedId,
            externalId,
            normalizedUrl,
            title,
            author,
            published,
            updated,
            summary,
            content,
            categories,
            enclosures,
            contentHash,
            fetchedAt,
            hasFullContent);
    }

    private static List<FeedEnclosure> ReadRssEnclosures(
        XElement item,
        Uri baseUri)
    {
        IEnumerable<EnclosureValue> standard = item.Elements()
            .Where(element => element.Name.LocalName == "enclosure")
            .Select(element => new EnclosureValue(
                (string?)element.Attribute("url"),
                (string?)element.Attribute("type"),
                (string?)element.Attribute("length"),
                (string?)element.Attribute("title")));
        return ReadEnclosures(
            standard.Concat(ReadMediaRssValues(item)),
            baseUri);
    }

    private static List<FeedEnclosure> ReadAtomEnclosures(
        XElement entry,
        Uri baseUri)
    {
        IEnumerable<EnclosureValue> standard = entry
            .Elements(AtomNamespace + "link")
            .Where(link => string.Equals((string?)link.Attribute("rel"), "enclosure", StringComparison.OrdinalIgnoreCase))
            .Select(link => new EnclosureValue(
                (string?)link.Attribute("href"),
                (string?)link.Attribute("type"),
                (string?)link.Attribute("length"),
                (string?)link.Attribute("title")));
        return ReadEnclosures(
            standard.Concat(ReadMediaRssValues(entry)),
            baseUri);
    }

    private static IEnumerable<EnclosureValue> ReadMediaRssValues(
        XElement entry) =>
        entry.Descendants(MediaNamespace + "content")
            .Select(content => new EnclosureValue(
                (string?)content.Attribute("url"),
                (string?)content.Attribute("type"),
                (string?)content.Attribute("fileSize"),
                (string?)content.Attribute("title")
                    ?? content.Element(MediaNamespace + "title")?.Value));

    private static List<FeedEnclosure> ReadEnclosures(
        IEnumerable<EnclosureValue> values,
        Uri baseUri)
    {
        var enclosures = new List<FeedEnclosure>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (EnclosureValue enclosure in values)
        {
            string? url = FeedUrlNormalizer.Normalize(enclosure.Url, baseUri);
            if (url is null || !seen.Add(url)) continue;
            long? length = long.TryParse(
                enclosure.Length,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parsedLength) && parsedLength >= 0
                ? parsedLength
                : null;
            enclosures.Add(new(
                url,
                CleanOptional(enclosure.MediaType, 128),
                length,
                CleanOptional(enclosure.Title, 256)));
            if (enclosures.Count == MaximumEnclosuresPerEntry)
            {
                break;
            }
        }
        return enclosures;
    }

    private static List<string> ReadCategories(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categories = new List<string>();
        foreach (string? value in values)
        {
            string? category = CleanOptional(value, 256);
            if (category is not null && seen.Add(category)) categories.Add(category);
            if (categories.Count == 64) break;
        }
        return categories;
    }

    private static string? ReadAtomAlternateUrl(XElement parent, Uri baseUri)
    {
        XElement? link = parent.Elements(AtomNamespace + "link").FirstOrDefault(element =>
        {
            string? relation = (string?)element.Attribute("rel");
            return element.Attribute("href") is not null
                && (relation is null || relation.Equals("alternate", StringComparison.OrdinalIgnoreCase));
        });
        return FeedUrlNormalizer.Normalize((string?)link?.Attribute("href"), baseUri);
    }

    private static string? ReadAtomAuthor(XElement parent) => CleanOptional(
        parent.Elements(AtomNamespace + "author").FirstOrDefault()?.Element(AtomNamespace + "name")?.Value,
        256);

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static string? Value(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value;

    private static string CleanTitle(string? value, string fallback)
    {
        string cleaned = FeedTextSanitizer.CleanLiteral(value, 512);
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static string? CleanOptional(string? value, int maximumLength)
    {
        string cleaned = FeedTextSanitizer.Clean(value, maximumLength);
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? ReadIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim();
        if (normalized.Any(char.IsControl)) return null;
        return normalized.Length <= 2048
            ? normalized
            : $"urn:lenxtool:id-hash:{CreateHash(normalized)}";
    }

    private static string CreateHash(params string?[] values)
    {
        string canonical = string.Concat(values.Select(value =>
        {
            string item = value ?? string.Empty;
            return $"{item.Length.ToString(CultureInfo.InvariantCulture)}:{item}";
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private void EnsureEntryLimit(int count)
    {
        if (count > _options.MaximumEntries)
            throw InvalidDocument("Feed 条目数量超过安全上限。");
    }

    private static AppException InvalidDocument(string detail) => new(new(
        AppErrorCode.InvalidRequest,
        "Feed 文档无效",
        detail,
        "请确认地址返回标准 RSS 2.0 或 Atom，并移除 DTD、外部实体或超限内容。",
        Provider: "Feed 解析"));

    private sealed record EnclosureValue(
        string? Url,
        string? MediaType,
        string? Length,
        string? Title);
}
