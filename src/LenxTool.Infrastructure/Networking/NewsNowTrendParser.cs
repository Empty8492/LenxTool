using System.Text.Json;
using LenxTool.Core.Models;
using LenxTool.Core.Tools;

namespace LenxTool.Infrastructure.Networking;

public static class NewsNowTrendParser
{
    public static IReadOnlyList<TrendItem> Parse(
        string json,
        TrendSourceDefinition source,
        DateTimeOffset capturedAt,
        int maximumItems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string status = root.TryGetProperty("status", out JsonElement statusElement)
            ? statusElement.GetString() ?? string.Empty
            : string.Empty;
        if (status is not ("success" or "cache"))
        {
            throw new InvalidDataException($"{source.Name} 返回了无效状态。");
        }

        if (!root.TryGetProperty("items", out JsonElement itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{source.Name} 缺少热点列表。");
        }

        var results = new List<TrendItem>(maximumItems);
        int sourceRank = 0;
        foreach (JsonElement item in itemsElement.EnumerateArray())
        {
            sourceRank++;
            if (results.Count >= maximumItems) break;
            if (!TryReadTitle(item, out string title)) continue;
            if (!TryReadLink(item, source, out string url)) continue;

            string heat = ReadHeat(item) ?? string.Empty;
            string hash = ContentFingerprint.Create(source.Id, NormalizeUrl(url), title);
            results.Add(new(
                $"trend-{hash[..20]}",
                source.Name,
                sourceRank,
                title,
                heat,
                url,
                hash,
                capturedAt));
        }

        return results;
    }

    private static bool TryReadTitle(JsonElement item, out string title)
    {
        title = string.Empty;
        if (!item.TryGetProperty("title", out JsonElement titleElement)
            || titleElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        title = titleElement.GetString()?.Trim() ?? string.Empty;
        return title.Length > 0;
    }

    private static bool TryReadLink(
        JsonElement item,
        TrendSourceDefinition source,
        out string url)
    {
        url = string.Empty;
        string[] candidates = [ReadString(item, "url"), ReadString(item, "mobileUrl")];
        foreach (string candidate in candidates.Where(value => value.Length > 0))
        {
            ValidateLink(candidate, source);
            if (url.Length == 0) url = candidate;
        }

        return url.Length > 0;
    }

    private static void ValidateLink(string value, TrendSourceDefinition source)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException($"{source.Name} 返回了无效链接。");
        }

        if (source.ExpectedDomain is null) return;
        string expected = source.ExpectedDomain.ToLowerInvariant();
        string hostname = uri.IdnHost.ToLowerInvariant();
        if (uri.Scheme != Uri.UriSchemeHttps
            || (hostname != expected && !hostname.EndsWith($".{expected}", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"{source.Name} 返回了未通过域名校验的链接。");
        }
    }

    private static string? ReadHeat(JsonElement item)
    {
        if (!item.TryGetProperty("extra", out JsonElement extra)
            || extra.ValueKind != JsonValueKind.Object
            || !extra.TryGetProperty("info", out JsonElement info)
            || info.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string value = info.GetString()?.Trim() ?? string.Empty;
        return value.Length == 0 ? null : value;
    }

    private static string ReadString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out JsonElement element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string NormalizeUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped).TrimEnd('/')
            : value.Trim();
}
