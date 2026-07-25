using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.Controls;

public enum RichArticleBlockKind
{
    Heading,
    Subheading,
    Body,
    Quote,
    Bullet,
    Translation,
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

public sealed record RichArticleTranslationSource(
    RichArticleDocument Document,
    IReadOnlyList<FeedAiTranslationBlock> Blocks);

public static partial class RichArticleFormatter
{
    private const int MaximumEnclosuresPerArticle = 32;
    private const long MaximumAutomaticEnclosureImageBytes =
        12L * 1024 * 1024;

    public static RichArticleTranslationSource CreateTranslationSource(
        RichArticleDocument document,
        string title)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        string normalizedTitle = NormalizeReaderText(title);
        if (normalizedTitle.Length == 0)
            throw new ArgumentException("翻译标题不能为空。", nameof(title));

        var sourceBlocks = new List<RichArticleBlock>(document.Blocks.Count + 1);
        bool containsTitle = document.Blocks.Any(block =>
            block.Kind == RichArticleBlockKind.Heading
            && string.Equals(block.Text, normalizedTitle, StringComparison.Ordinal));
        if (!containsTitle)
        {
            sourceBlocks.Add(new(
                RichArticleBlockKind.Heading,
                [new(normalizedTitle)]));
        }
        sourceBlocks.AddRange(document.Blocks);

        RichArticleBlock[] boundedSourceBlocks = sourceBlocks
            .SelectMany(SplitForTranslation)
            .ToArray();
        var translationBlocks = new List<FeedAiTranslationBlock>(boundedSourceBlocks.Length);
        for (int index = 0; index < boundedSourceBlocks.Length; index++)
        {
            RichArticleBlock block = boundedSourceBlocks[index];
            if (block.Kind == RichArticleBlockKind.Image
                || string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }

            ArticleContentLink[] links = block.Inlines
                .Select(inline => (
                    Inline: inline,
                    Url: inline.Url is null ? null : ResolveUrl(inline.Url, null)))
                .Where(item => item.Url is not null && !string.IsNullOrWhiteSpace(item.Inline.Text))
                .Select(item => new ArticleContentLink(item.Url!, item.Inline.Text))
                .ToArray();
            bool isTitle = block.Kind == RichArticleBlockKind.Heading
                && string.Equals(block.Text, normalizedTitle, StringComparison.Ordinal);
            FeedAiTranslationBlockKind kind =
                isTitle
                    ? FeedAiTranslationBlockKind.Title
                    : block.Kind switch
                    {
                        RichArticleBlockKind.Heading or RichArticleBlockKind.Subheading =>
                            FeedAiTranslationBlockKind.Heading,
                        RichArticleBlockKind.Bullet => FeedAiTranslationBlockKind.ListItem,
                        RichArticleBlockKind.Quote => FeedAiTranslationBlockKind.Quote,
                        _ => FeedAiTranslationBlockKind.Paragraph
                    };
            translationBlocks.Add(new(
                index,
                kind,
                block.Text,
                null,
                block.Kind switch
                {
                    RichArticleBlockKind.Heading => 1,
                    RichArticleBlockKind.Subheading => 2,
                    _ => null
                },
                Array.AsReadOnly(links)));
        }

        return new(
            new(document.HeroImageUrl, Array.AsReadOnly(boundedSourceBlocks)),
            Array.AsReadOnly(translationBlocks.ToArray()));
    }

    public static RichArticleDocument ApplyTranslation(
        RichArticleTranslationSource source,
        FeedAiTranslationResult result,
        bool bilingual)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        Dictionary<int, FeedAiTranslatedBlock> translatedBySequence = result.Blocks
            .ToDictionary(block => block.Sequence);
        var blocks = new List<RichArticleBlock>(
            bilingual ? source.Document.Blocks.Count * 2 : source.Document.Blocks.Count);
        for (int index = 0; index < source.Document.Blocks.Count; index++)
        {
            RichArticleBlock original = source.Document.Blocks[index];
            if (original.Kind == RichArticleBlockKind.Image)
            {
                blocks.Add(original);
                continue;
            }
            if (!translatedBySequence.TryGetValue(index, out FeedAiTranslatedBlock? translated)
                || !string.Equals(
                    translated.OriginalText,
                    original.Text,
                    StringComparison.Ordinal))
            {
                blocks.Add(original);
                continue;
            }

            if (bilingual) blocks.Add(original);
            blocks.Add(new(
                bilingual ? RichArticleBlockKind.Translation : original.Kind,
                CreateTranslatedInlines(translated.TranslatedText, original.Inlines)));
        }

        return new(source.Document.HeroImageUrl, Array.AsReadOnly(blocks.ToArray()));
    }

    public static RichArticleDocument FromExtractedContent(ArticleContentResult article)
    {
        ArgumentNullException.ThrowIfNull(article);
        var blocks = new List<RichArticleBlock>(article.Blocks.Count);
        foreach (ArticleContentBlock source in article.Blocks)
        {
            string text = NormalizeReaderText(source.Text);
            if (VideoEditionLinePattern().IsMatch(text)) continue;
            if (source.Kind == ArticleContentBlockKind.Image)
            {
                string? imageUrl = string.IsNullOrWhiteSpace(source.ResourceUrl)
                    ? null
                    : ResolveUrl(source.ResourceUrl, article.FinalUrl);
                if (imageUrl is not null)
                {
                    blocks.Add(new(
                        RichArticleBlockKind.Image,
                        [new(text)],
                        imageUrl));
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    blocks.Add(new(
                        RichArticleBlockKind.Body,
                        [new(text)]));
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(text)) continue;
            RichArticleBlockKind kind = source.Kind switch
            {
                ArticleContentBlockKind.Heading when source.HeadingLevel is > 1 =>
                    RichArticleBlockKind.Subheading,
                ArticleContentBlockKind.Heading => RichArticleBlockKind.Heading,
                ArticleContentBlockKind.ListItem => RichArticleBlockKind.Bullet,
                ArticleContentBlockKind.Quote => RichArticleBlockKind.Quote,
                _ => RichArticleBlockKind.Body
            };
            blocks.Add(new(kind, CreateExtractedInlines(text, source.Links, article.FinalUrl)));
        }

        string? heroImage = blocks
            .FirstOrDefault(block => block.Kind == RichArticleBlockKind.Image)
            ?.ImageUrl;
        return new(heroImage, blocks);
    }

    public static RichArticleDocument WithEnclosures(
        RichArticleDocument document,
        IReadOnlyList<FeedEnclosure> enclosures,
        string? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(enclosures);
        var attachments = new List<RichArticleBlock>();
        foreach (FeedEnclosure enclosure in enclosures.Take(
                     MaximumEnclosuresPerArticle))
        {
            FeedAttachmentClassification classification =
                FeedAttachmentClassifier.Classify(enclosure, baseUrl);
            string title = GetAttachmentTitle(classification);
            string label = CreateAttachmentLabel(
                classification,
                title);
            attachments.Add(new(
                RichArticleBlockKind.Bullet,
                classification.SafeUrl is null
                    ? [new(label)]
                    : [new(label, classification.SafeUrl)]));
            if (CanPreviewAutomatically(classification))
            {
                attachments.Add(new(
                    RichArticleBlockKind.Image,
                    [new(title)],
                    classification.SafeUrl));
            }
        }

        if (attachments.Count == 0) return document;
        var blocks = new List<RichArticleBlock>(
            document.Blocks.Count + attachments.Count + 1);
        blocks.AddRange(document.Blocks);
        blocks.Add(new(
            RichArticleBlockKind.Subheading,
            [new("附件")]));
        blocks.AddRange(attachments);
        return new(document.HeroImageUrl, blocks);
    }

    private static bool CanPreviewAutomatically(
        FeedAttachmentClassification attachment) =>
        attachment is
        {
            Kind: FeedAttachmentKind.Image,
            TypeStatus: FeedAttachmentTypeStatus.Verified,
            Length: > 0 and <= MaximumAutomaticEnclosureImageBytes,
            SafeUrl: not null
        }
        && attachment.SafeUrl.StartsWith(
            "https://",
            StringComparison.Ordinal);

    private static string CreateAttachmentLabel(
        FeedAttachmentClassification attachment,
        string title)
    {
        string kind = attachment.Kind switch
        {
            FeedAttachmentKind.Image => "图片",
            FeedAttachmentKind.Audio => "音频",
            FeedAttachmentKind.Video => "视频",
            _ => "附件"
        };
        string typeWarning = attachment.TypeStatus switch
        {
            FeedAttachmentTypeStatus.Verified => string.Empty,
            FeedAttachmentTypeStatus.Unverified => " · 类型未验证",
            FeedAttachmentTypeStatus.Conflicting =>
                " · 类型与扩展名不一致",
            _ => " · 不支持的附件类型"
        };
        string sourceWarning = attachment.SafeUrl is null
            ? "地址已阻止"
            : "外部来源，打开前请确认";
        return string.Concat(
            kind,
            " · ",
            title,
            " · ",
            FormatAttachmentLength(attachment.Length),
            typeWarning,
            " · ",
            sourceWarning);
    }

    private static string GetAttachmentTitle(
        FeedAttachmentClassification attachment)
    {
        string title = NormalizeReaderText(attachment.Title);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        if (Uri.TryCreate(
                attachment.SafeUrl,
                UriKind.Absolute,
                out Uri? uri))
        {
            string fileName = NormalizeReaderText(
                Path.GetFileName(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return "未命名附件";
    }

    private static string FormatAttachmentLength(long? length)
    {
        if (length is null)
        {
            return "大小未知";
        }

        if (length < 1024)
        {
            return $"{length.Value.ToString(CultureInfo.InvariantCulture)} B";
        }

        double value = length.Value;
        string unit = "KB";
        value /= 1024;
        if (value >= 1024)
        {
            value /= 1024;
            unit = "MB";
        }
        if (value >= 1024)
        {
            value /= 1024;
            unit = "GB";
        }

        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {unit}";
    }

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

    private static List<RichArticleInline> CreateTranslatedInlines(
        string translatedText,
        IReadOnlyList<RichArticleInline> originalInlines)
    {
        var inlines = new List<RichArticleInline> { new(translatedText) };
        RichArticleInline[] links = originalInlines
            .Select(inline => (
                Inline: inline,
                Url: inline.Url is null ? null : ResolveUrl(inline.Url, null)))
            .Where(item => item.Url is not null && !string.IsNullOrWhiteSpace(item.Inline.Text))
            .Select(item => new RichArticleInline(item.Inline.Text, item.Url))
            .ToArray();
        if (links.Length == 0) return inlines;

        inlines.Add(new("\n原文链接："));
        for (int index = 0; index < links.Length; index++)
        {
            if (index > 0) inlines.Add(new(" · "));
            inlines.Add(links[index]);
        }
        return inlines;
    }

    private static IReadOnlyList<RichArticleBlock> SplitForTranslation(
        RichArticleBlock block)
    {
        int maximumCharacters = FeedAiTranslationOptions.Default.MaximumBlockCharacters;
        if (block.Kind == RichArticleBlockKind.Image
            || block.Text.Length <= maximumCharacters)
        {
            return [block];
        }

        var result = new List<RichArticleBlock>();
        var chunk = new List<RichArticleInline>();
        int chunkCharacters = 0;
        foreach (RichArticleInline inline in block.Inlines)
        {
            string? safeUrl = inline.Url is null ? null : ResolveUrl(inline.Url, null);
            int position = 0;
            while (position < inline.Text.Length)
            {
                int available = maximumCharacters - chunkCharacters;
                int take = Math.Min(available, inline.Text.Length - position);
                if (take > 0
                    && position + take < inline.Text.Length
                    && char.IsHighSurrogate(inline.Text[position + take - 1])
                    && char.IsLowSurrogate(inline.Text[position + take]))
                {
                    take--;
                }
                if (take == 0)
                {
                    result.Add(new(block.Kind, Array.AsReadOnly(chunk.ToArray())));
                    chunk.Clear();
                    chunkCharacters = 0;
                    continue;
                }

                chunk.Add(new(inline.Text.Substring(position, take), safeUrl));
                position += take;
                chunkCharacters += take;
                if (chunkCharacters == maximumCharacters)
                {
                    result.Add(new(block.Kind, Array.AsReadOnly(chunk.ToArray())));
                    chunk.Clear();
                    chunkCharacters = 0;
                }
            }
        }
        if (chunk.Count > 0)
        {
            result.Add(new(block.Kind, Array.AsReadOnly(chunk.ToArray())));
        }
        return result;
    }

    private static string ToPlainText(string html) =>
        InlineWhitespacePattern().Replace(
            WebUtility.HtmlDecode(RemainingTagPattern().Replace(html, string.Empty)), " ").Trim();

    private static string ToPlainTextPreservingLinks(string html) =>
        InlineWhitespacePattern().Replace(RemainingTagPattern().Replace(html, string.Empty), " ").Trim();

    private static string? ResolveUrl(string value, string? baseUrl)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute)
            && absolute.Scheme is "http" or "https"
            && string.IsNullOrEmpty(absolute.UserInfo))
        {
            return absolute.AbsoluteUri;
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri)
            && Uri.TryCreate(baseUri, value, out Uri? relative)
            && relative.Scheme is "http" or "https"
            && string.IsNullOrEmpty(relative.UserInfo))
        {
            return relative.AbsoluteUri;
        }

        return null;
    }

    private static List<RichArticleInline> CreateExtractedInlines(
        string text,
        IReadOnlyList<ArticleContentLink> links,
        string baseUrl)
    {
        var inlines = new List<RichArticleInline>();
        int position = 0;
        foreach (ArticleContentLink link in links)
        {
            if (string.IsNullOrEmpty(link.Text)) continue;
            int linkPosition = text.IndexOf(
                link.Text,
                position,
                StringComparison.Ordinal);
            if (linkPosition < 0) continue;
            if (linkPosition > position)
            {
                inlines.Add(new(text[position..linkPosition]));
            }

            inlines.Add(new(
                link.Text,
                ResolveUrl(link.Url, baseUrl)));
            position = linkPosition + link.Text.Length;
        }

        if (position < text.Length)
        {
            inlines.Add(new(text[position..]));
        }
        if (inlines.Count == 0)
        {
            inlines.Add(new(text));
        }
        return inlines;
    }

    private static string NormalizeReaderText(string? text) =>
        SpeechAnnotationPattern()
            .Replace(text ?? string.Empty, "${display}")
            .Trim();

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
