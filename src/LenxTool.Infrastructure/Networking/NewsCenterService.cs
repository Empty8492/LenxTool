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
    private static readonly Uri NewsNowApiUri = new("https://newsnow.busiyi.world/api/s");
    private static readonly SemaphoreSlim SourceFetchSlots = new(4, 4);
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36";

    public async Task<NewsCenterSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        Task<FetchResult<NewsArticle>> briefTask = TryFetchAsync(
            "AI 早报",
            FetchDailyBriefAsync,
            cancellationToken);
        Task<FetchResult<TrendItem>>[] sourceTasks = TrendSourceCatalog.Default
            .Select(source => TryFetchAsync(
                source.Name,
                token => FetchNewsNowAsync(source, token),
                cancellationToken))
            .ToArray();

        FetchResult<TrendItem>[] sourceResults = await Task.WhenAll(sourceTasks).ConfigureAwait(false);
        FetchResult<NewsArticle> brief = await briefTask.ConfigureAwait(false);
        TrendItem[] trends = sourceResults.SelectMany(result => result.Items).ToArray();

        if (brief.Items.Count > 0)
        {
            await repository.UpsertAsync(brief.Items, cancellationToken).ConfigureAwait(false);
        }

        if (trends.Length > 0)
        {
            await repository.UpsertTrendsAsync(trends, cancellationToken).ConfigureAwait(false);
        }

        NewsCenterSnapshot cached = await LoadCachedAsync(cancellationToken).ConfigureAwait(false);
        string[] warnings = sourceResults
            .Select(result => result.Warning)
            .Prepend(brief.Warning)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        bool allFailed = brief.Items.Count == 0 && trends.Length == 0;
        string? warning = warnings.Length switch
        {
            0 => null,
            <= 3 => string.Join("；", warnings),
            _ => $"{string.Join("；", warnings.Take(3))}；另有 {warnings.Length - 3} 个来源使用缓存"
        };
        return cached with
        {
            IsFromCache = allFailed,
            Warning = warning
        };
    }

    public async Task<NewsCenterSnapshot> LoadCachedAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<NewsArticle> articles = await repository.GetLatestAsync(40, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<TrendItem> trends = await repository.GetLatestTrendsAsync(200, null, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset? cacheTime = articles.Select(item => (DateTimeOffset?)item.FetchedAt)
            .Concat(trends.Select(item => (DateTimeOffset?)item.CapturedAt))
            .Max();
        return new(articles, trends, true, cacheTime, null);
    }

    private async Task<IReadOnlyList<NewsArticle>> FetchDailyBriefAsync(
        CancellationToken cancellationToken)
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
                $"news-{hash[..20]}",
                DateOnly.FromDateTime(published.LocalDateTime),
                "AI 早报",
                title,
                summary,
                content,
                url,
                hash,
                fetchedAt)
            {
                RichContent = richContent
            };
        }).ToArray();
    }

    private async Task<IReadOnlyList<TrendItem>> FetchNewsNowAsync(
        TrendSourceDefinition source,
        CancellationToken cancellationToken)
    {
        await SourceFetchSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var uri = new Uri(
                $"{NewsNowApiUri.AbsoluteUri}?id={Uri.EscapeDataString(source.Id)}&latest");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            using HttpClient client = httpClientFactory.CreateClient("LenxTool.News");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<TrendItem> items = NewsNowTrendParser.Parse(
                json,
                source,
                DateTimeOffset.UtcNow,
                10);
            return items.Count > 0
                ? items
                : throw new InvalidDataException($"{source.Name} 未返回可用热点。");
        }
        finally
        {
            SourceFetchSlots.Release();
        }
    }

    private async Task<XDocument> DownloadXmlAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpClient client = httpClientFactory.CreateClient("LenxTool.News");
        using HttpResponseMessage response = await client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
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
        catch (InvalidDataException exception)
        {
            LogSourceFailed(logger, source, exception.Message);
            return new([], $"{source} 数据未通过校验，已使用缓存");
        }
    }

    private static string ElementValue(XElement parent, string localName, string fallback) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value?.Trim()
        is { Length: > 0 } value ? value : fallback;

    private static DateTimeOffset ParsePublished(string value, DateTimeOffset fallback) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out DateTimeOffset parsed)
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
