using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 以固定字段顺序生成 front matter，并把已清洗 HTML 的安全子集转成 Markdown；
/// 远程图片只有在调用方提供本地资源映射时才会进入正文。
/// </summary>
internal static partial class MarkdownDocumentRenderer
{
    internal const int MaximumHtmlDepth = 128;
    internal const int MaximumHtmlNodeCount = 16_384;
    private static readonly UTF8Encoding Utf8WithoutBom =
        new(encoderShouldEmitUTF8Identifier: false);

    public static IReadOnlyList<MarkdownImageCandidate>
        FindImageCandidates(
            string sanitizedContent,
            int maximumOutputBytes)
    {
        HtmlDocument document = ParseAndValidateDocument(
            sanitizedContent,
            maximumOutputBytes);
        return document.DocumentNode
            .Descendants("img")
            .Select(node => new MarkdownImageCandidate(
                node.GetAttributeValue("src", string.Empty),
                NormalizeInlineText(
                    node.GetAttributeValue("alt", string.Empty))))
            .Where(candidate => IsSafeHttpUrl(candidate.SourceUrl))
            .DistinctBy(
                candidate => candidate.SourceUrl,
                StringComparer.Ordinal)
            .ToArray();
    }

    public static string Render(
        FeedEntry entry,
        EntryViewKind viewKind,
        MarkdownExportContentMode contentMode,
        IReadOnlyDictionary<string, string> localImages,
        MarkdownRenderOptions? renderOptions,
        int maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localImages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumOutputBytes);
        var output = new BoundedUtf8StringBuilder(
            maximumOutputBytes);
        AppendFrontMatter(
            output,
            entry,
            viewKind,
            renderOptions?.Tags);
        output.Append('\n');

        if (contentMode == MarkdownExportContentMode.LinkOnly)
        {
            Uri? source = SafeHttpUri(entry.NormalizedUrl);
            if (source is not null)
            {
                output.AppendLine(
                    RenderSafeLink("阅读原文", source));
            }
            return NormalizeNewlines(output.ToString());
        }

        HtmlDocument document = ParseAndValidateDocument(
            entry.SanitizedContent,
            maximumOutputBytes);
        string body = RenderChildren(
            document.DocumentNode,
            contentMode,
            localImages,
            maximumOutputBytes);
        string renderedBody = renderOptions?.TemplateMarkdown is { } template
            ? ApplyTemplate(
                template,
                entry,
                body,
                maximumOutputBytes)
            : NormalizeMarkdownBody(body);
        output.Append(renderedBody);
        if (renderOptions?.IncludeSourceLink == true
            && SafeHttpUri(entry.NormalizedUrl) is { } sourceLink)
        {
            if (renderedBody.Length > 0)
            {
                output.Append("\n\n");
            }
            output.Append(
                RenderSafeLink("阅读原文", sourceLink));
        }
        output.Append('\n');
        return NormalizeNewlines(output.ToString());
    }

    private static void AppendFrontMatter(
        BoundedUtf8StringBuilder output,
        FeedEntry entry,
        EntryViewKind viewKind,
        IReadOnlyList<string>? tags)
    {
        output.AppendLine("---");
        AppendYaml(output, "title", entry.Title);
        AppendYaml(output, "source", SafeHttpUri(entry.NormalizedUrl)?.AbsoluteUri);
        AppendYaml(output, "author", entry.Author);
        AppendYaml(
            output,
            "published_at",
            entry.PublishedAt?.ToString("O", CultureInfo.InvariantCulture));
        AppendYaml(
            output,
            "fetched_at",
            entry.FetchedAt.ToString("O", CultureInfo.InvariantCulture));
        AppendYaml(output, "entry_id", entry.Id);
        AppendYaml(output, "feed_id", entry.FeedId);
        AppendYaml(output, "view_kind", viewKind.ToString());
        AppendYaml(output, "content_hash", entry.ContentHash);
        string[] categories = entry.Categories
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeInlineText)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (categories.Length == 0)
        {
            output.AppendLine("categories: []");
        }
        else
        {
            output.AppendLine("categories:");
            foreach (string category in categories)
            {
                output.Append("  - ");
                AppendQuotedYaml(output, category);
                output.Append('\n');
            }
        }
        if (tags is null)
        {
            // 通用 Markdown 导出保持既有 front matter 兼容性。
        }
        else if (tags.Count == 0)
        {
            output.AppendLine("tags: []");
        }
        else
        {
            output.AppendLine("tags:");
            foreach (string tag in tags)
            {
                output.Append("  - ");
                AppendQuotedYaml(output, tag);
                output.Append('\n');
            }
        }
        output.AppendLine("---");
    }

    private static string ApplyTemplate(
        string template,
        FeedEntry entry,
        string body,
        int maximumOutputBytes)
    {
        string source = SafeHttpUri(entry.NormalizedUrl) is { } sourceUri
            ? RenderSafeDestination(sourceUri)
            : string.Empty;
        string published = entry.PublishedAt?.ToString(
                "O",
                CultureInfo.InvariantCulture)
            ?? string.Empty;
        string title = EscapeMarkdownText(
            NormalizeInlineText(entry.Title),
            maximumOutputBytes);
        string content = NormalizeMarkdownBody(body);
        string author = EscapeMarkdownText(
            NormalizeInlineText(entry.Author ?? string.Empty),
            maximumOutputBytes);
        var output = new BoundedUtf8StringBuilder(
            maximumOutputBytes);
        int offset = 0;
        foreach (Match match
                 in TemplatePlaceholderRegex().Matches(template))
        {
            output.Append(
                template.AsSpan(
                    offset,
                    match.Index - offset));
            output.Append(
                match.Value switch
                {
                    "{{title}}" => title,
                    "{{content}}" => content,
                    "{{source_url}}" => source,
                    "{{author}}" => author,
                    "{{published_at}}" => published,
                    _ => string.Empty
                });
            offset = match.Index + match.Length;
        }
        output.Append(template.AsSpan(offset));
        return NormalizeMarkdownBody(output.ToString());
    }

    private static string RenderChildren(
        HtmlNode parent,
        MarkdownExportContentMode contentMode,
        IReadOnlyDictionary<string, string> localImages,
        int maximumOutputBytes)
    {
        var output = new BoundedUtf8StringBuilder(
            maximumOutputBytes);
        foreach (HtmlNode node in parent.ChildNodes)
        {
            output.Append(
                RenderNode(
                    node,
                    contentMode,
                    localImages,
                    maximumOutputBytes));
        }
        return output.ToString();
    }

    private static string RenderNode(
        HtmlNode node,
        MarkdownExportContentMode contentMode,
        IReadOnlyDictionary<string, string> localImages,
        int maximumOutputBytes)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            return EscapeMarkdownText(
                WebUtility.HtmlDecode(node.InnerText),
                maximumOutputBytes);
        }
        if (node.NodeType != HtmlNodeType.Element)
        {
            return string.Empty;
        }

        string nodeName = node.Name.ToLowerInvariant();
        string specialNode = nodeName switch
        {
            "br" => "\n",
            "code" when node.ParentNode?.Name != "pre" =>
                RenderInlineCode(
                    node.InnerText,
                    maximumOutputBytes),
            "pre" => RenderFencedCode(
                node.InnerText,
                maximumOutputBytes),
            "img" => RenderImage(node, contentMode, localImages),
            "script" or "style" => string.Empty,
            _ => string.Empty
        };
        if (nodeName is "br" or "pre" or "img" or "script" or "style"
            || nodeName == "code"
                && node.ParentNode?.Name != "pre")
        {
            EnsureWithinOutputBudget(
                specialNode,
                maximumOutputBytes);
            return specialNode;
        }

        string children = RenderChildren(
            node,
            contentMode,
            localImages,
            maximumOutputBytes);
        string rendered = nodeName switch
        {
            "p" or "div" => $"{children.Trim()}\n\n",
            "br" => "\n",
            "strong" or "b" => $"**{children.Trim()}**",
            "em" or "i" => $"*{children.Trim()}*",
            "h1" => $"# {children.Trim()}\n\n",
            "h2" => $"## {children.Trim()}\n\n",
            "h3" => $"### {children.Trim()}\n\n",
            "h4" => $"#### {children.Trim()}\n\n",
            "h5" => $"##### {children.Trim()}\n\n",
            "h6" => $"###### {children.Trim()}\n\n",
            "blockquote" => RenderBlockquote(
                children,
                maximumOutputBytes),
            "ul" or "ol" => $"{children.TrimEnd()}\n\n",
            "li" => $"- {children.Trim()}\n",
            "a" => RenderLink(
                node,
                children,
                maximumOutputBytes),
            _ => children
        };
        EnsureWithinOutputBudget(
            rendered,
            maximumOutputBytes);
        return rendered;
    }

    private static string RenderLink(
        HtmlNode node,
        string children,
        int maximumOutputBytes)
    {
        string href = node.GetAttributeValue("href", string.Empty);
        Uri? safeUri = SafeHttpUri(href);
        string label = children.Trim();
        if (safeUri is null)
        {
            return label;
        }
        // 文本节点已逐字符转义；这里只由渲染器拼接已验证的 HTTP(S) 目标。
        string link = RenderSafeLink(label, safeUri);
        EnsureWithinOutputBudget(
            link,
            maximumOutputBytes);
        return link;
    }

    private static string RenderInlineCode(
        string value,
        int maximumOutputBytes)
    {
        string content = NormalizeNewlines(
                WebUtility.HtmlDecode(value ?? string.Empty))
            .Replace('\n', ' ');
        if (content.Length == 0)
        {
            return "<code></code>";
        }
        int fenceLength = Math.Max(
            1,
            MaximumConsecutiveBackticks(content) + 1);
        bool consistsEntirelyOfSpaces =
            content.All(character => character == ' ');
        bool needsPadding = !consistsEntirelyOfSpaces
                            && (content.StartsWith('`')
                                || content.EndsWith('`')
                                || content.StartsWith(' ')
                                || content.EndsWith(' '));
        var output = new BoundedUtf8StringBuilder(
            maximumOutputBytes);
        output.AppendRepeatedAscii('`', fenceLength);
        if (needsPadding)
        {
            output.Append(' ');
        }
        output.Append(content);
        if (needsPadding)
        {
            output.Append(' ');
        }
        output.AppendRepeatedAscii('`', fenceLength);
        return output.ToString();
    }

    private static string RenderFencedCode(
        string value,
        int maximumOutputBytes)
    {
        string content = NormalizeNewlines(
            WebUtility.HtmlDecode(value ?? string.Empty));
        int fenceLength = Math.Max(
            3,
            MaximumConsecutiveBackticks(content) + 1);
        string finalContentNewline =
            content.EndsWith('\n') ? string.Empty : "\n";
        var output = new BoundedUtf8StringBuilder(
            maximumOutputBytes);
        output.AppendRepeatedAscii('`', fenceLength);
        output.Append('\n');
        output.Append(content);
        output.Append(finalContentNewline);
        output.AppendRepeatedAscii('`', fenceLength);
        output.Append("\n\n");
        return output.ToString();
    }

    private static int MaximumConsecutiveBackticks(string value)
    {
        int maximum = 0;
        int current = 0;
        foreach (char character in value)
        {
            if (character == '`')
            {
                maximum = Math.Max(maximum, ++current);
            }
            else
            {
                current = 0;
            }
        }
        return maximum;
    }

    internal static void EnsureContentWithinStructuralLimits(
        string sanitizedContent,
        int maximumOutputBytes) =>
        _ = ParseAndValidateDocument(
            sanitizedContent,
            maximumOutputBytes);

    private static HtmlDocument ParseAndValidateDocument(
        string sanitizedContent,
        int maximumOutputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumOutputBytes);
        string content = sanitizedContent ?? string.Empty;
        EnsureMarkupTokenBudget(content);
        var document = new HtmlDocument
        {
            // HtmlAgilityPack exposes this parser-time guard specifically
            // to prevent unclosed-tag nesting from overflowing the stack.
            OptionMaxNestedChildNodes = MaximumHtmlDepth
        };
        try
        {
            document.LoadHtml(content);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw new MarkdownRenderLimitExceededException(
                exception);
        }
        EnsureDocumentWithinStructuralLimits(
            document.DocumentNode,
            maximumOutputBytes);
        return document;
    }

    private static void EnsureMarkupTokenBudget(string content)
    {
        int markupTokenCount = 0;
        foreach (char character in content)
        {
            if (character == '<'
                && ++markupTokenCount > MaximumHtmlNodeCount)
            {
                throw new MarkdownRenderLimitExceededException();
            }
        }
    }

    private static void EnsureDocumentWithinStructuralLimits(
        HtmlNode root,
        int maximumOutputBytes)
    {
        long maximumEstimatedWorkBytes =
            checked((long)maximumOutputBytes * 3);
        var pending =
            new Stack<(HtmlNode Node, int Depth, int WorkDepth)>();
        if (root.FirstChild is not null)
        {
            pending.Push((root.FirstChild, 1, 1));
        }
        int nodeCount = 0;
        long estimatedWorkBytes = 0;
        while (pending.TryPop(out var current))
        {
            if (++nodeCount > MaximumHtmlNodeCount
                || current.Depth > MaximumHtmlDepth)
            {
                throw new MarkdownRenderLimitExceededException();
            }

            AddEstimatedWork(
                ref estimatedWorkBytes,
                bytes: 16,
                current.Depth,
                maximumEstimatedWorkBytes);
            if (current.Node.NodeType == HtmlNodeType.Text
                && current.WorkDepth > 0)
            {
                string decoded = WebUtility.HtmlDecode(
                    current.Node.InnerText);
                AddEstimatedWork(
                    ref estimatedWorkBytes,
                    Utf8WithoutBom.GetByteCount(decoded),
                    current.WorkDepth,
                    maximumEstimatedWorkBytes);
            }

            if (current.Node.NextSibling is not null)
            {
                pending.Push(
                    (current.Node.NextSibling,
                        current.Depth,
                        current.WorkDepth));
            }
            if (current.Node.FirstChild is not null)
            {
                int childWorkDepth = ChildWorkDepth(
                    current.Node,
                    current.WorkDepth);
                pending.Push(
                    (current.Node.FirstChild,
                        current.Depth + 1,
                        childWorkDepth));
            }
        }
    }

    private static int ChildWorkDepth(
        HtmlNode node,
        int currentWorkDepth)
    {
        if (node.NodeType != HtmlNodeType.Element)
        {
            return currentWorkDepth;
        }
        return node.Name.ToLowerInvariant() switch
        {
            "script" or "style" or "img" or "br" => 0,
            "pre" => 1,
            "code" when node.ParentNode?.Name != "pre" => 1,
            _ => currentWorkDepth > 0
                ? checked(currentWorkDepth + 1)
                : 0
        };
    }

    private static void AddEstimatedWork(
        ref long currentBytes,
        long bytes,
        int traversalCount,
        long maximumBytes)
    {
        if (bytes < 0
            || traversalCount <= 0
            || bytes > (maximumBytes - currentBytes)
                / traversalCount)
        {
            throw new MarkdownRenderLimitExceededException();
        }
        currentBytes += bytes * traversalCount;
    }

    private static string RenderImage(
        HtmlNode node,
        MarkdownExportContentMode contentMode,
        IReadOnlyDictionary<string, string> localImages)
    {
        if (contentMode
            != MarkdownExportContentMode.ContentWithCachedImages)
        {
            return string.Empty;
        }
        string source = node.GetAttributeValue("src", string.Empty);
        if (!localImages.TryGetValue(source, out string? relativePath))
        {
            return string.Empty;
        }
        string alt = NormalizeInlineText(
            node.GetAttributeValue("alt", string.Empty));
        return $"![{EscapeMarkdownLabel(alt)}]({relativePath})\n\n";
    }

    private static string RenderBlockquote(
        string content,
        int maximumOutputBytes)
    {
        string normalized = NormalizeNewlines(content).Trim();
        var output = new BoundedUtf8StringBuilder(
            maximumOutputBytes);
        int lineStart = 0;
        while (lineStart <= normalized.Length)
        {
            int lineEnd = normalized.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = normalized.Length;
            }
            output.Append("> ");
            output.Append(
                normalized.AsSpan(
                    lineStart,
                    lineEnd - lineStart));
            if (lineEnd == normalized.Length)
            {
                break;
            }
            output.Append('\n');
            lineStart = lineEnd + 1;
        }
        output.Append("\n\n");
        return output.ToString();
    }

    private static string NormalizeMarkdownBody(string value)
        => NormalizeNewlines(value).Trim();

    private static string NormalizeInlineText(string value) =>
        InlineWhitespaceRegex().Replace(
            NormalizeNewlines(
                WebUtility.HtmlDecode(value ?? string.Empty)),
            " ").Trim();

    private static void AppendYaml(
        BoundedUtf8StringBuilder output,
        string name,
        string? value)
    {
        output.Append(name);
        output.Append(": ");
        if (value is null)
        {
            output.Append("null");
        }
        else
        {
            AppendQuotedYaml(output, value);
        }
        output.Append('\n');
    }

    private static void AppendQuotedYaml(
        BoundedUtf8StringBuilder output,
        string value)
    {
        string normalized = NormalizeInlineText(value);
        output.Append('"');
        int segmentStart = 0;
        for (int index = 0; index < normalized.Length; index++)
        {
            char character = normalized[index];
            if (character == '\\' || character == '"')
            {
                output.Append(
                    normalized.AsSpan(
                        segmentStart,
                        index - segmentStart));
                output.Append('\\');
                output.Append(character);
                segmentStart = index + 1;
            }
        }
        output.Append(normalized.AsSpan(segmentStart));
        output.Append('"');
    }

    private static string EscapeMarkdownLabel(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);

    private static string EscapeMarkdownText(
        string value,
        int maximumOutputBytes)
    {
        const string markdownControlCharacters =
            "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
        var escaped = new BoundedUtf8StringBuilder(
            maximumOutputBytes);
        int segmentStart = 0;
        int index = 0;
        foreach (char character in value)
        {
            if (markdownControlCharacters.Contains(character))
            {
                escaped.Append(
                    value.AsSpan(
                        segmentStart,
                        index - segmentStart));
                escaped.Append('\\');
                escaped.Append(character);
                segmentStart = index + 1;
            }
            index++;
        }
        escaped.Append(value.AsSpan(segmentStart));
        return escaped.ToString();
    }

    private static void EnsureWithinOutputBudget(
        string value,
        int maximumOutputBytes)
    {
        if (Utf8WithoutBom.GetByteCount(value)
            > maximumOutputBytes)
        {
            throw new MarkdownRenderLimitExceededException();
        }
    }

    private static bool IsSafeHttpUrl(string? value) =>
        SafeHttpUri(value) is not null;

    private static Uri? SafeHttpUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;

    private static string RenderSafeLink(
        string label,
        Uri destination) =>
        $"[{label}]({RenderSafeDestination(destination)})";

    private static string RenderSafeDestination(Uri destination) =>
        $"<{destination.AbsoluteUri}>";

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex InlineWhitespaceRegex();

    [GeneratedRegex(
        @"\{\{(?:title|content|source_url|author|published_at)\}\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex TemplatePlaceholderRegex();

    private sealed class BoundedUtf8StringBuilder
    {
        private readonly int _maximumBytes;
        private readonly StringBuilder _builder = new();
        private int _byteCount;

        public BoundedUtf8StringBuilder(int maximumBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                maximumBytes);
            _maximumBytes = maximumBytes;
        }

        public void Append(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            Append(value.AsSpan());
        }

        public void Append(ReadOnlySpan<char> value)
        {
            if (value.IsEmpty)
            {
                return;
            }
            Reserve(Utf8WithoutBom.GetByteCount(value));
            _builder.Append(value);
        }

        public void Append(char value)
        {
            Span<char> buffer = stackalloc char[1];
            buffer[0] = value;
            Reserve(Utf8WithoutBom.GetByteCount(buffer));
            _builder.Append(value);
        }

        public void AppendLine(string value)
        {
            Append(value);
            Append('\n');
        }

        public void AppendRepeatedAscii(
            char value,
            int count)
        {
            if (value > 0x7f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            Reserve(count);
            _builder.Append(value, count);
        }

        private void Reserve(int additionalBytes)
        {
            if (additionalBytes > _maximumBytes - _byteCount)
            {
                throw new MarkdownRenderLimitExceededException();
            }
            _byteCount += additionalBytes;
        }

        public override string ToString() =>
            _builder.ToString();
    }
}

internal sealed record MarkdownImageCandidate(
    string SourceUrl,
    string AltText);

internal sealed class MarkdownRenderLimitExceededException
    : Exception
{
    public MarkdownRenderLimitExceededException()
    {
    }

    public MarkdownRenderLimitExceededException(
        Exception innerException)
        : base(
            "Markdown rendering exceeded its structural limits.",
            innerException)
    {
    }
}
