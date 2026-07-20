using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Core.Tools;
using Microsoft.Extensions.Logging;

namespace LenxTool.Infrastructure.Networking;

public sealed partial class NewsCenterService(
    IHttpClientFactory httpClientFactory,
    INewsRepository repository,
    ILogger<NewsCenterService> logger) : INewsCenterService
{
    private static readonly Uri DailyBriefUri = new("https://daily.juya.uk/rss.xml");
    private static readonly Uri HackerNewsUri = new("https://news.ycombinator.com/rss");

    public async Task<NewsCenterSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        Task<FetchResult<NewsArticle>> briefTask = TryFetchAsync(
            "AI 早报",
            FetchDailyBriefAsync,
            cancellationToken);
        Task<FetchResult<TrendItem>> hackerNewsTask = TryFetchAsync(
            "Hacker News",
            FetchHackerNewsAsync,
            cancellationToken);
        Task<FetchResult<TrendItem>> githubTask = TryFetchAsync(
            "GitHub",
            FetchGithubAsync,
            cancellationToken);

        await Task.WhenAll(briefTask, hackerNewsTask, githubTask).ConfigureAwait(false);
        FetchResult<NewsArticle> brief = await briefTask.ConfigureAwait(false);
        FetchResult<TrendItem> hackerNews = await hackerNewsTask.ConfigureAwait(false);
        FetchResult<TrendItem> github = await githubTask.ConfigureAwait(false);

        TrendItem[] trends = hackerNews.Items.Concat(github.Items).ToArray();
        if (brief.Items.Count > 0) await repository.UpsertAsync(brief.Items, cancellationToken).ConfigureAwait(false);
        if (trends.Length > 0) await repository.UpsertTrendsAsync(trends, cancellationToken).ConfigureAwait(false);

        NewsCenterSnapshot cached = await LoadCachedAsync(cancellationToken).ConfigureAwait(false);
        string?[] possibleWarnings = [brief.Warning, hackerNews.Warning, github.Warning];
        string[] warnings = possibleWarnings
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        bool allFailed = brief.Items.Count == 0 && trends.Length == 0;
        return cached with
        {
            IsFromCache = allFailed,
            Warning = warnings.Length == 0 ? null : string.Join("；", warnings)
        };
    }

    public async Task<NewsCenterSnapshot> LoadCachedAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<NewsArticle> articles = await repository.GetLatestAsync(40, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<TrendItem> trends = await repository.GetLatestTrendsAsync(60, null, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset? cacheTime = articles.Select(item => (DateTimeOffset?)item.FetchedAt)
            .Concat(trends.Select(item => (DateTimeOffset?)item.CapturedAt))
            .Max();
        return new(articles, trends, true, cacheTime, null);
    }

    private async Task<IReadOnlyList<NewsArticle>> FetchDailyBriefAsync(CancellationToken cancellationToken)
    {
        XDocument document = await DownloadXmlAsync(DailyBriefUri, cancellationToken).ConfigureAwait(false);
        DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;
        return document.Descendants("item").Take(30).Select((item, index) =>
        {
            string title = ElementValue(item, "title", $"每日早报 {index + 1}");
            string url = ElementValue(item, "link", DailyBriefUri.AbsoluteUri);
            string summary = StripMarkup(ElementValue(item, "description", string.Empty));
            string richContent = item.Elements().FirstOrDefault(
                element => element.Name.LocalName == "encoded")?.Value ?? string.Empty;
            string content = StripMarkup(string.IsNullOrWhiteSpace(richContent) ? summary : richContent);
            DateTimeOffset published = ParsePublished(ElementValue(item, "pubDate", string.Empty), fetchedAt);
            string hash = ContentFingerprint.Create(NormalizeUrl(url), title);
            return new NewsArticle(
                $"news-{hash[..20]}", DateOnly.FromDateTime(published.LocalDateTime), "AI 早报",
                title, summary, content, url, hash, fetchedAt)
            {
                RichContent = richContent
            };
        }).ToArray();
    }

    private async Task<IReadOnlyList<TrendItem>> FetchHackerNewsAsync(CancellationToken cancellationToken)
    {
        XDocument document = await DownloadXmlAsync(HackerNewsUri, cancellationToken).ConfigureAwait(false);
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        return document.Descendants("item").Take(20).Select((item, index) =>
        {
            string title = ElementValue(item, "title", "Untitled");
            string url = ElementValue(item, "link", HackerNewsUri.AbsoluteUri);
            string hash = ContentFingerprint.Create("Hacker News", NormalizeUrl(url), title);
            return new TrendItem(
                $"trend-{hash[..20]}", "Hacker News", index + 1, title, $"#{index + 1}",
                url, hash, capturedAt);
        }).ToArray();
    }

    private async Task<IReadOnlyList<TrendItem>> FetchGithubAsync(CancellationToken cancellationToken)
    {
        string date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var uri = new Uri($"https://api.github.com/search/repositories?q=created:%3E{date}&sort=stars&order=desc&per_page=20");
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("LenxTool/0.1");
        using HttpClient client = httpClientFactory.CreateClient("LenxTool.News");
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        var results = new List<TrendItem>();
        int rank = 1;
        foreach (JsonElement item in document.RootElement.GetProperty("items").EnumerateArray().Take(20))
        {
            string title = item.GetProperty("full_name").GetString() ?? "unknown/repository";
            string url = item.GetProperty("html_url").GetString() ?? "https://github.com";
            int stars = item.GetProperty("stargazers_count").GetInt32();
            string hash = ContentFingerprint.Create("GitHub", NormalizeUrl(url), title);
            results.Add(new(
                $"trend-{hash[..20]}", "GitHub", rank++, title,
                $"{stars.ToString("N0", CultureInfo.InvariantCulture)} stars", url, hash, capturedAt));
        }

        return results;
    }

    private async Task<XDocument> DownloadXmlAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpClient client = httpClientFactory.CreateClient("LenxTool.News");
        using HttpResponseMessage response = await client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FetchResult<T>> TryFetchAsync<T>(
        string source,
        Func<CancellationToken, Task<IReadOnlyList<T>>> fetch,
        CancellationToken cancellationToken)
    {
        try
        {
            return new(await fetch(cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogSourceFailed(logger, source, "请求超时");
            return new([], $"{source} 请求超时，已使用缓存");
        }
        catch (HttpRequestException exception)
        {
            LogSourceFailed(logger, source, exception.Message);
            return new([], $"{source} 暂时不可用，已使用缓存");
        }
        catch (JsonException exception)
        {
            LogSourceFailed(logger, source, exception.Message);
            return new([], $"{source} 返回了无法识别的数据");
        }
        catch (System.Xml.XmlException exception)
        {
            LogSourceFailed(logger, source, exception.Message);
            return new([], $"{source} 返回了无法识别的数据");
        }
    }

    private static string ElementValue(XElement parent, string localName, string fallback) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value?.Trim()
        is { Length: > 0 } value ? value : fallback;

    private static DateTimeOffset ParsePublished(string value, DateTimeOffset fallback) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset parsed)
            ? parsed
            : fallback;

    private static string NormalizeUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped).TrimEnd('/')
            : value.Trim();

    private static string StripMarkup(string value)
    {
        string withoutTags = MarkupPattern().Replace(value, " ");
        return WhitespacePattern().Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    [LoggerMessage(2101, LogLevel.Warning, "News source {Source} failed: {Reason}")]
    private static partial void LogSourceFailed(ILogger logger, string source, string reason);

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupPattern();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    private sealed record FetchResult<T>(IReadOnlyList<T> Items, string? Warning);
}
