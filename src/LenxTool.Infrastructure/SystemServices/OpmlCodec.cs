using System.Text;
using System.Xml;
using System.Xml.Linq;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.SystemServices;

public sealed class OpmlCodec : IOpmlCodec
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const long MaximumXmlCharacters = 4 * 1024 * 1024;
    private const int MaximumFeeds = 5000;
    private const int MaximumOutlines = 10_000;
    private const int MaximumDepth = 16;
    private const int MaximumAttributeCodePoints = 4096;
    private const int MaximumTitleCodePoints = 160;

    public async Task<OpmlDocument> ParseAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("OPML source must be readable.", nameof(source));

        try
        {
            await using MemoryStream buffer = await ReadBoundedAsync(source, cancellationToken).ConfigureAwait(false);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlCharacters,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CloseInput = false
            };
            using XmlReader reader = XmlReader.Create(buffer, settings);
            XDocument xml = XDocument.Load(reader, LoadOptions.None);
            XElement root = xml.Root is { } candidate && candidate.Name.LocalName == "opml"
                ? candidate
                : throw InvalidOpml("OPML_ROOT_MISSING");
            XElement body = root.Elements().FirstOrDefault(element => element.Name.LocalName == "body")
                ?? throw InvalidOpml("OPML_BODY_MISSING");
            string title = ReadDocumentTitle(root);
            var feeds = new List<OpmlFeed>();
            int outlineCount = 0;
            foreach (XElement outline in body.Elements().Where(element => element.Name.LocalName == "outline"))
                ReadOutline(outline, [], 1, feeds, ref outlineCount);
            return new(title, feeds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw InvalidOpml(exception.GetType().Name, exception);
        }
    }

    public async Task WriteAsync(
        Stream destination,
        OpmlDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(document);
        if (!destination.CanWrite) throw new ArgumentException("OPML destination must be writable.", nameof(destination));
        if (document.Feeds.Count > MaximumFeeds) throw InvalidOpml("OPML_FEED_LIMIT");

        try
        {
            XDocument xml = BuildDocument(document);
            await using var buffer = new MemoryStream();
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = true,
                NewLineChars = "\n",
                CloseOutput = false
            };
            using (XmlWriter writer = XmlWriter.Create(buffer, settings))
            {
                cancellationToken.ThrowIfCancellationRequested();
                xml.Save(writer);
                writer.Flush();
            }
            if (buffer.Length > MaximumBytes) throw InvalidOpml("OPML_BYTE_LIMIT");
            buffer.Position = 0;
            await buffer.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw InvalidOpml(exception.GetType().Name, exception);
        }
    }

    private static async Task<MemoryStream> ReadBoundedAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumBytes)
            {
                await output.DisposeAsync().ConfigureAwait(false);
                throw InvalidOpml("OPML_BYTE_LIMIT");
            }
            await output.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        output.Position = 0;
        return output;
    }

    private static string ReadDocumentTitle(XElement root)
    {
        string? title = root.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "head")?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "title")?
            .Value;
        if (string.IsNullOrWhiteSpace(title)) return "LenxTool 共享订阅";
        return RequireBoundedText(title, "OPML_TITLE_INVALID", MaximumTitleCodePoints);
    }

    private static void ReadOutline(
        XElement outline,
        IReadOnlyList<string> groupPath,
        int depth,
        ICollection<OpmlFeed> feeds,
        ref int outlineCount)
    {
        if (depth > MaximumDepth) throw InvalidOpml("OPML_DEPTH_LIMIT");
        if (++outlineCount > MaximumOutlines) throw InvalidOpml("OPML_OUTLINE_LIMIT");
        string label = ReadAttribute(outline, "text")
            ?? ReadAttribute(outline, "title")
            ?? string.Empty;
        string? xmlUrl = ReadAttribute(outline, "xmlUrl");
        IReadOnlyList<string> childPath = groupPath;
        if (xmlUrl is not null)
        {
            if (feeds.Count >= MaximumFeeds) throw InvalidOpml("OPML_FEED_LIMIT");
            string title = string.IsNullOrWhiteSpace(label)
                ? xmlUrl
                : RequireBoundedText(label, "OPML_FEED_TITLE_INVALID", MaximumAttributeCodePoints);
            string? htmlUrl = ReadAttribute(outline, "htmlUrl");
            feeds.Add(new(title, xmlUrl, htmlUrl, groupPath.ToArray()));
        }
        else if (!string.IsNullOrWhiteSpace(label))
        {
            string group = RequireBoundedText(label, "OPML_GROUP_INVALID", MaximumAttributeCodePoints);
            childPath = [.. groupPath, group];
        }

        foreach (XElement child in outline.Elements().Where(element => element.Name.LocalName == "outline"))
            ReadOutline(child, childPath, depth + 1, feeds, ref outlineCount);
    }

    private static string? ReadAttribute(XElement element, string localName)
    {
        string? value = element.Attributes()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Name.LocalName,
                localName,
                StringComparison.OrdinalIgnoreCase))?
            .Value;
        if (value is null) return null;
        string trimmed = value.Trim();
        if (trimmed.EnumerateRunes().Count() > MaximumAttributeCodePoints
            || trimmed.Any(char.IsControl))
        {
            throw InvalidOpml($"OPML_{localName.ToUpperInvariant()}_INVALID");
        }
        return trimmed;
    }

    private static XDocument BuildDocument(OpmlDocument document)
    {
        string title = RequireBoundedText(document.Title, "OPML_TITLE_INVALID", MaximumTitleCodePoints);
        var rootGroup = new ExportGroup(string.Empty);
        foreach (OpmlFeed feed in document.Feeds)
        {
            string feedTitle = RequireBoundedText(feed.Title, "OPML_FEED_TITLE_INVALID", MaximumAttributeCodePoints);
            string xmlUrl = RequireBoundedText(feed.XmlUrl, "OPML_XMLURL_INVALID", MaximumAttributeCodePoints);
            string? htmlUrl = feed.HtmlUrl is null
                ? null
                : RequireBoundedText(feed.HtmlUrl, "OPML_HTMLURL_INVALID", MaximumAttributeCodePoints);
            ExportGroup group = rootGroup;
            if (feed.GroupPath.Count > MaximumDepth) throw InvalidOpml("OPML_DEPTH_LIMIT");
            foreach (string part in feed.GroupPath)
            {
                string groupName = RequireBoundedText(part, "OPML_GROUP_INVALID", MaximumAttributeCodePoints);
                group = group.GetOrAdd(groupName);
            }
            group.Feeds.Add(new(feedTitle, xmlUrl, htmlUrl, []));
        }

        var body = new XElement("body");
        AppendGroupContents(rootGroup, body);
        return new(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "opml",
                new XAttribute("version", "2.0"),
                new XElement("head", new XElement("title", title)),
                body));
    }

    private static void AppendGroupContents(ExportGroup group, XElement parent)
    {
        foreach (ExportGroup child in group.Children)
        {
            var outline = new XElement("outline", new XAttribute("text", child.Name), new XAttribute("title", child.Name));
            AppendGroupContents(child, outline);
            parent.Add(outline);
        }
        foreach (OpmlFeed feed in group.Feeds)
        {
            var outline = new XElement(
                "outline",
                new XAttribute("text", feed.Title),
                new XAttribute("title", feed.Title),
                new XAttribute("type", "rss"),
                new XAttribute("xmlUrl", feed.XmlUrl));
            if (feed.HtmlUrl is not null) outline.Add(new XAttribute("htmlUrl", feed.HtmlUrl));
            parent.Add(outline);
        }
    }

    private static string RequireBoundedText(string value, string reason, int maximumCodePoints)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0
            || trimmed.EnumerateRunes().Count() > maximumCodePoints
            || trimmed.Any(char.IsControl))
        {
            throw InvalidOpml(reason);
        }
        return trimmed;
    }

    private static AppException InvalidOpml(string reason, Exception? innerException = null) => new(new(
        AppErrorCode.InvalidRequest,
        "OPML 文件无效",
        reason == "OPML_BYTE_LIMIT"
            ? "OPML 文件超过 2 MiB 安全上限。"
            : "无法安全读取或生成该 OPML 文件。",
        "请选择格式正确、大小受限的 OPML 2.0 文件后重试。",
        $"OPML validation reason: {reason}"), innerException);

    private sealed class ExportGroup(string name)
    {
        private readonly Dictionary<string, ExportGroup> _childrenByName = new(StringComparer.Ordinal);

        public string Name { get; } = name;
        public List<ExportGroup> Children { get; } = [];
        public List<OpmlFeed> Feeds { get; } = [];

        public ExportGroup GetOrAdd(string childName)
        {
            if (_childrenByName.TryGetValue(childName, out ExportGroup? child)) return child;
            child = new(childName);
            _childrenByName.Add(childName, child);
            Children.Add(child);
            return child;
        }
    }
}
