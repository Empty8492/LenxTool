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
    public static IReadOnlyList<MarkdownImageCandidate>
        FindImageCandidates(string sanitizedContent)
    {
        var document = new HtmlDocument();
        document.LoadHtml(sanitizedContent ?? string.Empty);
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
        IReadOnlyDictionary<string, string> localImages)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localImages);
        var output = new StringBuilder();
        AppendFrontMatter(output, entry, viewKind);
        output.Append('\n');

        if (contentMode == MarkdownExportContentMode.LinkOnly)
        {
            Uri? source = SafeHttpUri(entry.NormalizedUrl);
            if (source is not null)
            {
                output.Append("[阅读原文](");
                output.Append(source.AbsoluteUri);
                output.AppendLine(")");
            }
            return NormalizeNewlines(output.ToString());
        }

        var document = new HtmlDocument();
        document.LoadHtml(entry.SanitizedContent ?? string.Empty);
        string body = RenderChildren(
            document.DocumentNode,
            contentMode,
            localImages);
        output.Append(NormalizeMarkdownBody(body));
        output.Append('\n');
        return NormalizeNewlines(output.ToString());
    }

    private static void AppendFrontMatter(
        StringBuilder output,
        FeedEntry entry,
        EntryViewKind viewKind)
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
                output.AppendLine(QuoteYaml(category));
            }
        }
        output.AppendLine("---");
    }

    private static string RenderChildren(
        HtmlNode parent,
        MarkdownExportContentMode contentMode,
        IReadOnlyDictionary<string, string> localImages) =>
        string.Concat(
            parent.ChildNodes.Select(node =>
                RenderNode(node, contentMode, localImages)));

    private static string RenderNode(
        HtmlNode node,
        MarkdownExportContentMode contentMode,
        IReadOnlyDictionary<string, string> localImages)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            return WebUtility.HtmlDecode(node.InnerText);
        }
        if (node.NodeType != HtmlNodeType.Element)
        {
            return string.Empty;
        }

        string children = RenderChildren(
            node,
            contentMode,
            localImages);
        return node.Name.ToLowerInvariant() switch
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
            "blockquote" => RenderBlockquote(children),
            "ul" or "ol" => $"{children.TrimEnd()}\n\n",
            "li" => $"- {children.Trim()}\n",
            "code" when node.ParentNode?.Name != "pre" =>
                $"`{children.Trim().Replace("`", "\\`", StringComparison.Ordinal)}`",
            "pre" => $"```\n{node.InnerText.Trim()}\n```\n\n",
            "a" => RenderLink(node, children),
            "img" => RenderImage(node, contentMode, localImages),
            "script" or "style" => string.Empty,
            _ => children
        };
    }

    private static string RenderLink(
        HtmlNode node,
        string children)
    {
        string href = node.GetAttributeValue("href", string.Empty);
        Uri? safeUri = SafeHttpUri(href);
        string label = children.Trim();
        if (safeUri is null)
        {
            return label;
        }
        return $"[{EscapeMarkdownLabel(label)}]({safeUri.AbsoluteUri})";
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

    private static string RenderBlockquote(string content) =>
        string.Join(
            '\n',
            NormalizeNewlines(content)
                .Trim()
                .Split('\n')
                .Select(line => $"> {line}"))
        + "\n\n";

    private static string NormalizeMarkdownBody(string value)
    {
        string normalized = NormalizeNewlines(value);
        normalized = TrailingWhitespaceRegex().Replace(
            normalized,
            string.Empty);
        normalized = ExcessBlankLinesRegex().Replace(
            normalized,
            "\n\n");
        return normalized.Trim();
    }

    private static string NormalizeInlineText(string value) =>
        InlineWhitespaceRegex().Replace(
            NormalizeNewlines(
                WebUtility.HtmlDecode(value ?? string.Empty)),
            " ").Trim();

    private static void AppendYaml(
        StringBuilder output,
        string name,
        string? value)
    {
        output.Append(name);
        output.Append(": ");
        output.AppendLine(value is null ? "null" : QuoteYaml(value));
    }

    private static string QuoteYaml(string value)
    {
        var escaped = new StringBuilder();
        foreach (char character in NormalizeInlineText(value))
        {
            if (character == '\\' || character == '"')
            {
                escaped.Append('\\');
            }
            escaped.Append(character);
        }
        return $"\"{escaped}\"";
    }

    private static string EscapeMarkdownLabel(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);

    private static bool IsSafeHttpUrl(string? value) =>
        SafeHttpUri(value) is not null;

    private static Uri? SafeHttpUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    [GeneratedRegex(@"[ \t]+\n", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingWhitespaceRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessBlankLinesRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex InlineWhitespaceRegex();
}

internal sealed record MarkdownImageCandidate(
    string SourceUrl,
    string AltText);
