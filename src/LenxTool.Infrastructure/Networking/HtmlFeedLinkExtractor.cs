using System.Net;

namespace LenxTool.Infrastructure.Networking;

internal static class HtmlFeedLinkExtractor
{
    public static IReadOnlyList<HtmlFeedLink> Extract(
        string html,
        Uri baseUri,
        int maximumCandidates)
    {
        var links = new List<HtmlFeedLink>();
        int index = 0;
        while (index < html.Length && links.Count < maximumCandidates)
        {
            int opening = html.IndexOf('<', index);
            if (opening < 0) break;
            int nameStart = opening + 1;
            while (nameStart < html.Length && char.IsWhiteSpace(html[nameStart])) nameStart++;
            if (!StartsWithTagName(html, nameStart, "link"))
            {
                index = opening + 1;
                continue;
            }

            int end = FindTagEnd(html, nameStart + 4);
            if (end < 0) break;
            Dictionary<string, string> attributes = ParseAttributes(html.AsSpan(nameStart + 4, end - nameStart - 4));
            if (IsFeedAlternate(attributes)
                && attributes.TryGetValue("href", out string? encodedHref))
            {
                string href = WebUtility.HtmlDecode(encodedHref).Trim();
                if (href.Length is > 0 and <= 2048
                    && Uri.TryCreate(baseUri, href, out Uri? feedUri))
                {
                    string? title = attributes.TryGetValue("title", out string? encodedTitle)
                        ? WebUtility.HtmlDecode(encodedTitle).Trim()
                        : null;
                    links.Add(new(feedUri, string.IsNullOrWhiteSpace(title) ? null : title));
                }
            }
            index = end + 1;
        }
        return links;
    }

    private static bool IsFeedAlternate(Dictionary<string, string> attributes)
    {
        if (!attributes.TryGetValue("rel", out string? rel)
            || !rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Contains("alternate", StringComparer.OrdinalIgnoreCase)
            || !attributes.TryGetValue("type", out string? type))
        {
            return false;
        }

        string mediaType = type.Split(';', 2)[0].Trim();
        return mediaType.Equals("application/rss+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/atom+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseAttributes(ReadOnlySpan<char> content)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        while (index < content.Length)
        {
            while (index < content.Length && (char.IsWhiteSpace(content[index]) || content[index] == '/')) index++;
            int nameStart = index;
            while (index < content.Length && IsAttributeNameCharacter(content[index])) index++;
            if (index == nameStart)
            {
                index++;
                continue;
            }
            string name = content[nameStart..index].ToString();
            while (index < content.Length && char.IsWhiteSpace(content[index])) index++;
            string value = string.Empty;
            if (index < content.Length && content[index] == '=')
            {
                index++;
                while (index < content.Length && char.IsWhiteSpace(content[index])) index++;
                if (index < content.Length && content[index] is '\'' or '"')
                {
                    char quote = content[index++];
                    int valueStart = index;
                    while (index < content.Length && content[index] != quote) index++;
                    value = content[valueStart..index].ToString();
                    if (index < content.Length) index++;
                }
                else
                {
                    int valueStart = index;
                    while (index < content.Length
                           && !char.IsWhiteSpace(content[index])
                           && content[index] != '>')
                    {
                        index++;
                    }
                    value = content[valueStart..index].ToString();
                }
            }
            attributes.TryAdd(name, value);
        }
        return attributes;
    }

    private static int FindTagEnd(string html, int index)
    {
        char quote = '\0';
        for (int current = index; current < html.Length; current++)
        {
            char value = html[current];
            if (quote != '\0')
            {
                if (value == quote) quote = '\0';
                continue;
            }
            if (value is '\'' or '"') quote = value;
            else if (value == '>') return current;
        }
        return -1;
    }

    private static bool StartsWithTagName(string html, int index, string name)
    {
        if (index + name.Length > html.Length
            || !html.AsSpan(index, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        int end = index + name.Length;
        return end == html.Length || char.IsWhiteSpace(html[end]) || html[end] is '/' or '>';
    }

    private static bool IsAttributeNameCharacter(char value) =>
        !char.IsWhiteSpace(value) && value is not ('=' or '/' or '>' or '<');
}

internal sealed record HtmlFeedLink(Uri Uri, string? Title);
