using System.Globalization;
using System.Text;
using HtmlAgilityPack;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal sealed class HtmlArticleContentParser
{
    internal const string ExtractionVersion = "article-content-v1";
    private static readonly HashSet<string> TextBlockNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "h1", "h2", "h3", "h4", "h5", "h6", "p", "li", "blockquote"
        };
    private static readonly string[] PositiveTokens =
        ["article", "body", "content", "entry", "main", "post", "story", "text"];
    private static readonly string[] NegativeTokens =
        ["advert", "comment", "footer", "header", "menu", "nav", "promo", "related", "share", "sidebar"];
    private readonly ArticleContentExtractionOptions _options;

    public HtmlArticleContentParser(ArticleContentExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _options = options;
    }

    public ArticleContentResult Parse(
        string requestedUrl,
        Uri finalUri,
        string html,
        IReadOnlyList<ArticleExtractionWarning> initialWarnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedUrl);
        ArgumentNullException.ThrowIfNull(finalUri);
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(initialWarnings);

        var document = new HtmlDocument
        {
            OptionAutoCloseOnEnd = true,
            OptionCheckSyntax = false,
            OptionFixNestedTags = true,
            OptionMaxNestedChildNodes = _options.MaximumNestingDepth
        };
        try
        {
            document.LoadHtml(html);
        }
        catch (Exception exception) when (
            exception is not (OutOfMemoryException or StackOverflowException))
        {
            throw new InvalidDataException(
                "文章 HTML 嵌套或结构超过安全上限。",
                exception);
        }
        int nodeCount = document.DocumentNode
            .Descendants()
            .Take(_options.MaximumDocumentNodes + 1)
            .Count();
        if (nodeCount > _options.MaximumDocumentNodes)
        {
            throw new InvalidDataException("文章 HTML 节点数量超过安全上限。");
        }

        RemoveUnsafeNodes(document.DocumentNode);
        HtmlNode root = SelectContentRoot(document);
        var warnings = initialWarnings.ToList();
        List<ArticleContentBlock> blocks = ExtractBlocks(
            root,
            finalUri,
            warnings);
        if (blocks.Count == 0)
        {
            warnings.Add(new(
                ArticleExtractionWarningCode.NoReadableContent,
                "页面没有可识别的正文块。"));
        }

        return new(
            requestedUrl,
            finalUri.AbsoluteUri,
            ReadTitle(document, root),
            ReadMetadata(document, "author", "article:author", "byline"),
            ReadPublishedAt(document, warnings),
            blocks,
            warnings,
            ExtractionVersion);
    }

    private List<ArticleContentBlock> ExtractBlocks(
        HtmlNode root,
        Uri finalUri,
        List<ArticleExtractionWarning> warnings)
    {
        var blocks = new List<ArticleContentBlock>();
        int totalTextCharacters = 0;
        bool blockLimitWarningAdded = false;
        bool textLimitWarningAdded = false;

        foreach (HtmlNode node in root.Descendants())
        {
            string name = node.Name;
            bool isImage = name.Equals("img", StringComparison.OrdinalIgnoreCase);
            if (!isImage && !TextBlockNames.Contains(name))
            {
                continue;
            }
            if (!isImage && HasTextBlockAncestor(node, root))
            {
                continue;
            }

            ArticleContentBlock? block = isImage
                ? CreateImageBlock(node, finalUri)
                : CreateTextBlock(node, finalUri);
            if (block is null)
            {
                continue;
            }
            if (blocks.Count >= _options.MaximumBlocks)
            {
                if (!blockLimitWarningAdded)
                {
                    warnings.Add(new(
                        ArticleExtractionWarningCode.BlockLimitReached,
                        "正文块数量达到安全上限，后续内容已省略。"));
                    blockLimitWarningAdded = true;
                }
                break;
            }

            int remaining = _options.MaximumTotalTextCharacters - totalTextCharacters;
            if (remaining <= 0)
            {
                if (!textLimitWarningAdded)
                {
                    warnings.Add(new(
                        ArticleExtractionWarningCode.TextLimitReached,
                        "正文文本达到安全上限，后续内容已省略。"));
                    textLimitWarningAdded = true;
                }
                break;
            }
            if (block.Text.Length > remaining)
            {
                block = block with { Text = block.Text[..remaining].TrimEnd() };
                if (!textLimitWarningAdded)
                {
                    warnings.Add(new(
                        ArticleExtractionWarningCode.TextLimitReached,
                        "正文文本达到安全上限，最后一个正文块已截断。"));
                    textLimitWarningAdded = true;
                }
            }
            if (block.Text.Length == 0 && block.Kind != ArticleContentBlockKind.Image)
            {
                continue;
            }

            blocks.Add(block);
            totalTextCharacters += block.Text.Length;
            if (textLimitWarningAdded)
            {
                break;
            }
        }

        return blocks;
    }

    private static ArticleContentBlock? CreateTextBlock(
        HtmlNode node,
        Uri finalUri)
    {
        string text = NormalizeText(node.InnerText);
        if (text.Length == 0)
        {
            return null;
        }
        ArticleContentBlockKind kind;
        int? headingLevel = null;
        if (node.Name.Length == 2
            && node.Name[0] == 'h'
            && node.Name[1] is >= '1' and <= '6')
        {
            kind = ArticleContentBlockKind.Heading;
            headingLevel = node.Name[1] - '0';
        }
        else
        {
            kind = node.Name.ToLowerInvariant() switch
            {
                "li" => ArticleContentBlockKind.ListItem,
                "blockquote" => ArticleContentBlockKind.Quote,
                _ => ArticleContentBlockKind.Paragraph
            };
        }

        return new(
            kind,
            text,
            ResourceUrl: null,
            headingLevel,
            ReadLinks(node, finalUri));
    }

    private static ArticleContentBlock? CreateImageBlock(
        HtmlNode node,
        Uri finalUri)
    {
        string? source = FirstNonEmptyAttribute(
            node,
            "src",
            "data-src",
            "data-original");
        string? resourceUrl = NormalizeResourceUrl(source, finalUri);
        if (resourceUrl is null)
        {
            return null;
        }

        return new(
            ArticleContentBlockKind.Image,
            NormalizeText(node.GetAttributeValue("alt", string.Empty)),
            resourceUrl,
            HeadingLevel: null,
            Links: []);
    }

    private static List<ArticleContentLink> ReadLinks(
        HtmlNode node,
        Uri finalUri)
    {
        HtmlNodeCollection? anchors = node.SelectNodes(".//a[@href]");
        if (anchors is null)
        {
            return [];
        }

        var links = new List<ArticleContentLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (HtmlNode anchor in anchors)
        {
            string? normalized = NormalizeResourceUrl(
                anchor.GetAttributeValue("href", string.Empty),
                finalUri);
            if (normalized is null || !seen.Add(normalized))
            {
                continue;
            }
            links.Add(new(normalized, NormalizeText(anchor.InnerText)));
        }
        return links;
    }

    private static string? FirstNonEmptyAttribute(
        HtmlNode node,
        params string[] names)
    {
        foreach (string name in names)
        {
            string value = node.GetAttributeValue(name, string.Empty);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static string? NormalizeResourceUrl(string? value, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || value.Any(char.IsControl)
            || !Uri.TryCreate(baseUri, HtmlEntity.DeEntitize(value.Trim()), out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsoluteUri.Length > 2048)
        {
            return null;
        }
        return uri.AbsoluteUri;
    }

    private static bool HasTextBlockAncestor(HtmlNode node, HtmlNode root)
    {
        for (HtmlNode? ancestor = node.ParentNode;
             ancestor is not null && ancestor != root;
             ancestor = ancestor.ParentNode)
        {
            if (TextBlockNames.Contains(ancestor.Name))
            {
                return true;
            }
        }
        return false;
    }

    private static HtmlNode SelectContentRoot(HtmlDocument document)
    {
        HtmlNode root = document.DocumentNode;
        HtmlNodeCollection? semanticArticles = root.SelectNodes("//article");
        HtmlNode? selected = SelectBestCandidate(semanticArticles);
        if (selected is not null)
        {
            return selected;
        }

        selected = SelectBestCandidate(root.SelectNodes("//main"));
        if (selected is not null)
        {
            return selected;
        }

        IEnumerable<HtmlNode> contentCandidates = root
            .Descendants()
            .Where(node => node.Name is "div" or "section")
            .Where(IsPositiveCandidate)
            .Take(2_000);
        selected = SelectBestCandidate(contentCandidates);
        return selected
            ?? document.DocumentNode.SelectSingleNode("//body")
            ?? document.DocumentNode;
    }

    private static HtmlNode? SelectBestCandidate(
        IEnumerable<HtmlNode>? candidates) =>
        candidates?
            .Where(candidate => !IsNegativeCandidate(candidate))
            .Select(candidate => (Node: candidate, Score: Score(candidate)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Node)
            .FirstOrDefault();

    private static int Score(HtmlNode candidate)
    {
        string text = NormalizeText(candidate.InnerText);
        int paragraphText = candidate.Descendants("p")
            .Take(1_000)
            .Sum(paragraph => Math.Min(NormalizeText(paragraph.InnerText).Length, 20_000));
        int linkText = candidate.Descendants("a")
            .Take(2_000)
            .Sum(anchor => Math.Min(NormalizeText(anchor.InnerText).Length, 2_000));
        int paragraphCount = candidate.Descendants("p").Take(1_000).Count();
        return Math.Min(text.Length, 500_000)
            + Math.Min(paragraphText, 500_000) * 2
            + paragraphCount * 80
            - Math.Min(linkText, 250_000) * 2;
    }

    private static bool IsPositiveCandidate(HtmlNode node)
    {
        string signature = CandidateSignature(node);
        return PositiveTokens.Any(token =>
            signature.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNegativeCandidate(HtmlNode node)
    {
        string signature = CandidateSignature(node);
        return NegativeTokens.Any(token =>
            signature.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string CandidateSignature(HtmlNode node) =>
        $"{node.GetAttributeValue("id", string.Empty)} " +
        node.GetAttributeValue("class", string.Empty);

    private static void RemoveUnsafeNodes(HtmlNode root)
    {
        HtmlNodeCollection? unsafeNodes = root.SelectNodes(
            "//script|//style|//noscript|//template|//form|//iframe|//object|" +
            "//embed|//svg|//canvas|//nav|//footer|//aside");
        if (unsafeNodes is not null)
        {
            foreach (HtmlNode node in unsafeNodes.ToArray())
            {
                node.Remove();
            }
        }

        foreach (HtmlNode comment in root
                     .Descendants()
                     .Where(node => node.NodeType == HtmlNodeType.Comment)
                     .ToArray())
        {
            comment.Remove();
        }
    }

    private static string? ReadTitle(HtmlDocument document, HtmlNode root)
    {
        string? title = ReadMetadata(document, "og:title", "twitter:title", "title");
        if (title is not null)
        {
            return Limit(title, 512);
        }

        HtmlNode? heading = root.SelectSingleNode(".//h1");
        title = NormalizeOptional(heading?.InnerText);
        if (title is not null)
        {
            return Limit(title, 512);
        }

        return Limit(NormalizeOptional(
            document.DocumentNode.SelectSingleNode("//title")?.InnerText), 512);
    }

    private static string? ReadMetadata(
        HtmlDocument document,
        params string[] names)
    {
        HtmlNodeCollection? metadata = document.DocumentNode.SelectNodes("//meta[@content]");
        if (metadata is null)
        {
            return null;
        }
        foreach (string acceptedName in names)
        {
            foreach (HtmlNode meta in metadata)
            {
                string name = meta.GetAttributeValue("property", string.Empty);
                if (name.Length == 0)
                {
                    name = meta.GetAttributeValue("name", string.Empty);
                }
                if (!name.Trim().Equals(
                        acceptedName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string? content = NormalizeOptional(
                    meta.GetAttributeValue("content", string.Empty));
                if (content is not null)
                {
                    return Limit(content, 512);
                }
            }
        }
        return null;
    }

    private static DateTimeOffset? ReadPublishedAt(
        HtmlDocument document,
        List<ArticleExtractionWarning> warnings)
    {
        string? value = ReadMetadata(
            document,
            "article:published_time",
            "datepublished",
            "date",
            "pubdate");
        HtmlNode? time = document.DocumentNode
            .SelectSingleNode("//time[@datetime]");
        value ??= time is null
            ? null
            : NormalizeOptional(
                time.GetAttributeValue("datetime", string.Empty));
        if (value is null)
        {
            return null;
        }
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset publishedAt))
        {
            return publishedAt.ToUniversalTime();
        }

        warnings.Add(new(
            ArticleExtractionWarningCode.InvalidMetadata,
            "页面发布时间格式无效，已忽略该字段。"));
        return null;
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        string decoded = HtmlEntity.DeEntitize(value);
        var output = new StringBuilder(decoded.Length);
        bool pendingSpace = false;
        foreach (char character in decoded)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = output.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                output.Append(' ');
                pendingSpace = false;
            }
            output.Append(character);
        }
        return output.ToString().Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        string normalized = NormalizeText(value);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? Limit(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength
            ? value
            : value[..maximumLength].TrimEnd();

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
}
