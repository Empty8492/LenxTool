using System.Net;
using System.Text.RegularExpressions;

namespace LenxTool.App.Controls;

public enum RichArticleBlockKind
{
    Heading,
    Subheading,
    Body,
    Bullet,
    Image
}

public sealed record RichArticleInline(string Text, string? Url = null);

public sealed record RichArticleBlock(
    RichArticleBlockKind Kind,
    IReadOnlyList<RichArticleInline> Inlines,
    string? ImageUrl = null)
{
    public string Text => string.Concat(Inlines.Select(inline => inline.Text));
}

public sealed record RichArticleDocument(string? HeroImageUrl, IReadOnlyList<RichArticleBlock> Blocks);

public static partial class RichArticleFormatter
{
    public static RichArticleDocument Parse(string? htmlOrText, string? baseUrl = null)
    {
        string input = htmlOrText ?? string.Empty;
        string normalized = UnsafeElementPattern().Replace(input, string.Empty);
        var images = new List<RichArticleImage>();
        normalized = ImageTagPattern().Replace(
            normalized,
            match => CreateHtmlImageMarker(match.Value, baseUrl, images));
        normalized = MarkdownImagePattern().Replace(
            normalized,
            match => CreateImageMarker(
                match.Groups["url"].Value,
                match.Groups["alt"].Value,
                baseUrl,
                images));
        normalized = AnchorPattern().Replace(normalized, match =>
        {
            string text = ToPlainText(match.Groups["text"].Value);
            string? url = ResolveUrl(WebUtility.HtmlDecode(match.Groups["url"].Value), baseUrl);
            return url is null ? text : $"[{text}]({url})";
        });
        normalized = HeadingOnePattern().Replace(normalized, match => $"\n# {ToPlainText(match.Groups["text"].Value)}\n");
        normalized = HeadingTwoPattern().Replace(normalized, match => $"\n## {ToPlainText(match.Groups["text"].Value)}\n");
        normalized = HeadingThreePattern().Replace(normalized, match => $"\n### {ToPlainText(match.Groups["text"].Value)}\n");
        normalized = ListItemPattern().Replace(normalized, match => $"\n• {ToPlainTextPreservingLinks(match.Groups["text"].Value)}\n");
        normalized = BreakPattern().Replace(normalized, "\n");
        normalized = RemainingTagPattern().Replace(normalized, string.Empty);
        normalized = WebUtility.HtmlDecode(normalized).Replace("\r", string.Empty, StringComparison.Ordinal);
        normalized = SpeechAnnotationPattern().Replace(normalized, "${display}");

        var blocks = new List<RichArticleBlock>();
        foreach (string rawLine in normalized.Split('\n'))
        {
            string line = InlineWhitespacePattern().Replace(rawLine, " ").Trim();
            if (line.Length == 0) continue;

            Match imageMarker = ImageMarkerPattern().Match(line);
            if (imageMarker.Success
                && int.TryParse(imageMarker.Groups["index"].Value, out int imageIndex)
                && imageIndex >= 0
                && imageIndex < images.Count)
            {
                RichArticleImage image = images[imageIndex];
                blocks.Add(new(
                    RichArticleBlockKind.Image,
                    [new(string.IsNullOrWhiteSpace(image.AltText) ? "资讯配图" : image.AltText)],
                    image.Url));
                continue;
            }

            RichArticleBlockKind kind = RichArticleBlockKind.Body;
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                kind = RichArticleBlockKind.Subheading;
                line = line[4..];
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal))
            {
                kind = RichArticleBlockKind.Heading;
                line = line[(line[1] == '#' ? 3 : 2)..];
            }
            else if (line.StartsWith("• ", StringComparison.Ordinal))
            {
                kind = RichArticleBlockKind.Bullet;
                line = line[2..];
            }

            if (kind == RichArticleBlockKind.Body
                && VideoEditionLinePattern().IsMatch(line))
            {
                continue;
            }

            blocks.Add(new(kind, ParseInlines(line)));
        }

        string? heroImage = blocks.FirstOrDefault(block => block.Kind == RichArticleBlockKind.Image)?.ImageUrl;
        return new(heroImage, blocks);
    }

    private static string CreateHtmlImageMarker(
        string imageTag,
        string? baseUrl,
        List<RichArticleImage> images)
    {
        Dictionary<string, string> attributes = ImageAttributePattern()
            .Matches(imageTag)
            .GroupBy(match => match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key.ToLowerInvariant(),
                group =>
                {
                    Match match = group.Last();
                    return WebUtility.HtmlDecode(
                        match.Groups["double"].Success
                            ? match.Groups["double"].Value
                            : match.Groups["single"].Value);
                },
                StringComparer.OrdinalIgnoreCase);

        string? source = null;
        foreach (string attribute in new[] { "data-src", "data-original", "data-lazy-src", "src", "srcset" })
        {
            if (!attributes.TryGetValue(attribute, out string? candidate)) continue;
            string firstCandidate = attribute == "srcset"
                ? candidate.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
                    .FirstOrDefault() ?? string.Empty
                : candidate;
            if (ResolveUrl(firstCandidate, baseUrl) is not null)
            {
                source = firstCandidate;
                break;
            }
        }

        attributes.TryGetValue("alt", out string? altText);
        return CreateImageMarker(source, altText, baseUrl, images);
    }

    private static string CreateImageMarker(
        string? source,
        string? altText,
        string? baseUrl,
        List<RichArticleImage> images)
    {
        string? url = string.IsNullOrWhiteSpace(source)
            ? null
            : ResolveUrl(WebUtility.HtmlDecode(source), baseUrl);
        if (url is null) return string.Empty;

        int index = images.Count;
        images.Add(new(url, WebUtility.HtmlDecode(altText ?? string.Empty).Trim()));
        return $"\n\uE000LENX_IMAGE_{index}\uE001\n";
    }

    private static List<RichArticleInline> ParseInlines(string text)
    {
        var result = new List<RichArticleInline>();
        int position = 0;
        foreach (Match match in LinkPattern().Matches(text))
        {
            if (match.Index > position) result.Add(new(text[position..match.Index]));
            string label = match.Groups["label"].Success ? match.Groups["label"].Value : match.Groups["raw"].Value;
            string url = match.Groups["url"].Success ? match.Groups["url"].Value : match.Groups["raw"].Value;
            result.Add(new(label, url));
            position = match.Index + match.Length;
        }

        if (position < text.Length) result.Add(new(text[position..]));
        if (result.Count == 0) result.Add(new(text));
        return result;
    }

    private static string ToPlainText(string html) =>
        InlineWhitespacePattern().Replace(
            WebUtility.HtmlDecode(RemainingTagPattern().Replace(html, string.Empty)), " ").Trim();

    private static string ToPlainTextPreservingLinks(string html) =>
        InlineWhitespacePattern().Replace(RemainingTagPattern().Replace(html, string.Empty), " ").Trim();

    private static string? ResolveUrl(string value, string? baseUrl)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute) && absolute.Scheme is "http" or "https")
        {
            return absolute.AbsoluteUri;
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri)
            && Uri.TryCreate(baseUri, value, out Uri? relative)
            && relative.Scheme is "http" or "https")
        {
            return relative.AbsoluteUri;
        }

        return null;
    }

    [GeneratedRegex("<(?:script|style|iframe|object|embed)\\b[^>]*>.*?</(?:script|style|iframe|object|embed)>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeElementPattern();

    [GeneratedRegex("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTagPattern();

    [GeneratedRegex("(?:^|\\s)(?<name>data-src|data-original|data-lazy-src|srcset|src|alt)\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageAttributePattern();

    [GeneratedRegex("!\\[(?<alt>[^\\]]*)\\]\\((?<url>[^\\s)]+)(?:\\s+[\"'][^\"']*[\"'])?\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownImagePattern();

    [GeneratedRegex("^\\uE000LENX_IMAGE_(?<index>[0-9]+)\\uE001$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageMarkerPattern();

    [GeneratedRegex("<a\\b[^>]*href\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AnchorPattern();

    [GeneratedRegex("<h1\\b[^>]*>(?<text>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingOnePattern();

    [GeneratedRegex("<h2\\b[^>]*>(?<text>.*?)</h2>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingTwoPattern();

    [GeneratedRegex("<h3\\b[^>]*>(?<text>.*?)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingThreePattern();

    [GeneratedRegex("<li\\b[^>]*>(?<text>.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ListItemPattern();

    [GeneratedRegex("<(?:br|/p|/div|/section|/article|/ul|/ol)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BreakPattern();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex RemainingTagPattern();

    [GeneratedRegex("[ \\t\\f\\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex InlineWhitespacePattern();

    [GeneratedRegex("\\[(?<label>[^\\]]+)\\]\\((?<url>https?://[^)]+)\\)|(?<raw>https?://[^\\s<>()]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    [GeneratedRegex("\\{(?<display>[^{}|]+)\\|(?:\"[^\"]*\"|[^{}]+)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex SpeechAnnotationPattern();

    [GeneratedRegex("^视频版\\s*[:：]", RegexOptions.CultureInvariant)]
    private static partial Regex VideoEditionLinePattern();

    private sealed record RichArticleImage(string Url, string AltText);
}
