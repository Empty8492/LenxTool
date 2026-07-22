using System.Text;
using LenxTool.Core.Models;

namespace LenxTool.Core.Feeds;

public static class OpmlCatalogPlanner
{
    private const int MaximumTitleCodePoints = 160;
    private const int MaximumCategoryCodePoints = 80;
    private const int MaximumUrlCharacters = 2048;
    private const string GroupSeparator = " / ";

    public static IReadOnlyList<OpmlCatalogPreviewItem> CreatePreview(
        OpmlDocument document,
        FeedCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);

        Dictionary<string, FeedCategory> categories = catalog.Categories
            .GroupBy(category => category.NormalizedName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var existingFeeds = new Dictionary<string, FeedCatalogItem>(StringComparer.Ordinal);
        foreach (FeedCatalogItem feed in catalog.Feeds)
        {
            string key = NormalizeHttpsUrl(feed.NormalizedUrl, out string normalized)
                ? normalized
                : feed.NormalizedUrl;
            existingFeeds.TryAdd(key, feed);
        }

        var firstImportedByUrl = new Dictionary<string, ImportSignature>(StringComparer.Ordinal);
        var result = new List<OpmlCatalogPreviewItem>(document.Feeds.Count);
        for (int index = 0; index < document.Feeds.Count; index++)
        {
            OpmlFeed feed = document.Feeds[index];
            result.Add(Classify(index, feed, categories, existingFeeds, firstImportedByUrl));
        }
        return result;
    }

    private static OpmlCatalogPreviewItem Classify(
        int index,
        OpmlFeed feed,
        Dictionary<string, FeedCategory> categories,
        Dictionary<string, FeedCatalogItem> existingFeeds,
        IDictionary<string, ImportSignature> firstImportedByUrl)
    {
        string title = feed.Title.Trim();
        if (!IsBoundedText(title, MaximumTitleCodePoints))
            return Invalid(index, feed, "标题必须为 1～160 个字符，且不能包含控制字符。");
        if (!NormalizeHttpsUrl(feed.XmlUrl, out string feedUrl))
            return Invalid(index, feed, "Feed 地址必须是无账号、片段或自定义端口的 HTTPS URL。");

        string? siteUrl = null;
        if (!string.IsNullOrWhiteSpace(feed.HtmlUrl))
        {
            if (!NormalizeHttpsUrl(feed.HtmlUrl, out siteUrl))
                return Invalid(index, feed, "站点地址必须是无账号、片段或自定义端口的 HTTPS URL。");
        }

        string? categoryName = feed.GroupPath.Count == 0
            ? null
            : string.Join(GroupSeparator, feed.GroupPath.Select(part => part.Trim()));
        if (categoryName is not null && !IsBoundedText(categoryName, MaximumCategoryCodePoints))
            return Invalid(index, feed, "嵌套分组展开后的分类名称必须为 1～80 个字符。");

        string? categoryId = null;
        string categoryKey = string.Empty;
        if (categoryName is not null)
        {
            string normalizedCategory = NormalizeCategoryName(categoryName);
            categoryKey = normalizedCategory;
            if (categories.TryGetValue(normalizedCategory, out FeedCategory? existingCategory))
            {
                categoryId = existingCategory.Id;
                categoryName = existingCategory.Name;
                categoryKey = existingCategory.Id;
            }
        }

        var signature = new ImportSignature(title, categoryKey);
        if (existingFeeds.TryGetValue(feedUrl, out FeedCatalogItem? existing))
        {
            bool same = string.Equals(existing.DisplayName, title, StringComparison.Ordinal)
                && string.Equals(existing.CategoryId, categoryId, StringComparison.Ordinal);
            return new(
                index,
                title,
                feedUrl,
                siteUrl,
                categoryName,
                categoryId,
                same ? OpmlCatalogItemStatus.Duplicate : OpmlCatalogItemStatus.Conflict,
                same ? "共享目录中已存在相同订阅。" : "相同 Feed 地址已存在，但标题或分类不同。",
                false);
        }
        if (firstImportedByUrl.TryGetValue(feedUrl, out ImportSignature? first))
        {
            bool same = first == signature;
            return new(
                index,
                title,
                feedUrl,
                siteUrl,
                categoryName,
                categoryId,
                same ? OpmlCatalogItemStatus.Duplicate : OpmlCatalogItemStatus.Conflict,
                same ? "该订阅在 OPML 文件中重复出现。" : "OPML 中相同 Feed 地址具有不同标题或分类。",
                false);
        }

        firstImportedByUrl.Add(feedUrl, signature);
        return new(
            index,
            title,
            feedUrl,
            siteUrl,
            categoryName,
            categoryId,
            OpmlCatalogItemStatus.New,
            categoryId is null && categoryName is not null ? "将新建分类并导入。" : "可导入。",
            true);
    }

    private static OpmlCatalogPreviewItem Invalid(int index, OpmlFeed feed, string message) => new(
        index,
        feed.Title.Trim(),
        feed.XmlUrl.Trim(),
        string.IsNullOrWhiteSpace(feed.HtmlUrl) ? null : feed.HtmlUrl.Trim(),
        feed.GroupPath.Count == 0 ? null : string.Join(GroupSeparator, feed.GroupPath),
        null,
        OpmlCatalogItemStatus.Invalid,
        message,
        false);

    private static bool IsBoundedText(string value, int maximumCodePoints) =>
        value.Length > 0
        && value.EnumerateRunes().Count() <= maximumCodePoints
        && !value.Any(char.IsControl);

    private static string NormalizeCategoryName(string value) =>
        value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    private static bool NormalizeHttpsUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumUrlCharacters
            || value != value.Trim()
            || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.IsDefaultPort)
        {
            return false;
        }
        normalized = uri.AbsoluteUri;
        return normalized.Length <= MaximumUrlCharacters;
    }

    private sealed record ImportSignature(string Title, string CategoryKey);
}
