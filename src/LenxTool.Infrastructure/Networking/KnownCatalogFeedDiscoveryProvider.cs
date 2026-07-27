using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal interface IKnownCatalogDiscoveryClient
{
    Task<HttpResponseMessage> GetAsync(
        string pathAndQuery,
        CancellationToken cancellationToken);
}

internal sealed class WorkerKnownCatalogDiscoveryClient(
    WorkerAccountSessionService accountSession)
    : IKnownCatalogDiscoveryClient
{
    public Task<HttpResponseMessage> GetAsync(
        string pathAndQuery,
        CancellationToken cancellationToken) =>
        accountSession.GetAuthorizedAsync(pathAndQuery, cancellationToken);
}

internal sealed class KnownCatalogFeedDiscoveryProvider(
    IKnownCatalogDiscoveryClient client,
    FeedDiscoveryProviderPolicy policy) : IFeedDiscoveryProvider
{
    public const string ProviderSourceId = "worker:known-catalog";

    private const int MaximumResponseBytes = 512 * 1024;
    private const long MaximumJsonSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IKnownCatalogDiscoveryClient _client =
        client ?? throw new ArgumentNullException(nameof(client));

    public string SourceId => ProviderSourceId;

    public FeedDiscoverySourceKind SourceKind =>
        FeedDiscoverySourceKind.KnownCatalog;

    public FeedDiscoveryProviderPolicy Policy { get; } =
        policy ?? throw new ArgumentNullException(nameof(policy));

    public bool Supports(FeedDiscoveryQueryKind queryKind) =>
        queryKind is FeedDiscoveryQueryKind.Url
            or FeedDiscoveryQueryKind.Keyword;

    public async Task<FeedDiscoveryProviderResult> DiscoverAsync(
        FeedDiscoveryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsValid
            || !Supports(query.Kind)
            || query.NormalizedValue is null)
        {
            throw new ArgumentException(
                "Known catalog discovery requires a valid URL or keyword query.",
                nameof(query));
        }

        string path = BuildPath(query.NormalizedValue);
        using HttpResponseMessage response = await _client
            .GetAsync(path, cancellationToken)
            .ConfigureAwait(false);
        await WorkerAccountSessionService
            .EnsureSuccessAsync(response, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse();
        }

        byte[] payload = await ReadBoundedAsync(
            response.Content,
            cancellationToken).ConfigureAwait(false);
        try
        {
            DiscoveryPageDto dto = JsonSerializer.Deserialize<DiscoveryPageDto>(
                payload,
                JsonOptions) ?? throw InvalidResponse();
            return MapPage(dto, query.NormalizedValue);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            JsonException or
            ArgumentException or
            InvalidOperationException or
            FormatException)
        {
            throw new AppException(InvalidResponse().Error, exception);
        }
    }

    private string BuildPath(string query)
    {
        string path =
            $"/v1/feeds/discoveries?query={Uri.EscapeDataString(query)}" +
            $"&pageSize={Policy.MaximumCandidates}&scope=ACTIVE";
        if (Encoding.UTF8.GetByteCount(path) > 2048)
            throw InvalidResponse();
        return path;
    }

    private FeedDiscoveryProviderResult MapPage(
        DiscoveryPageDto dto,
        string expectedQuery)
    {
        if (dto.CatalogVersion is < 0 or > MaximumJsonSafeInteger
            || !string.Equals(dto.Query, expectedQuery, StringComparison.Ordinal)
            || dto.Scope != "ACTIVE"
            || dto.Items is null
            || dto.Pagination is null
            || dto.Items.Count > Policy.MaximumCandidates
            || dto.Pagination.PageSize != Policy.MaximumCandidates
            || dto.Pagination.TotalItems < dto.Items.Count
            || !IsValidCursor(dto.Pagination.NextCursor))
        {
            throw InvalidResponse();
        }

        FeedDiscoveryCandidate[] candidates = dto.Items
            .Select(MapItem)
            .ToArray();
        bool isTruncated = dto.Pagination.NextCursor is not null
            || dto.Pagination.TotalItems > candidates.Length;
        return new(candidates, isTruncated);
    }

    private static FeedDiscoveryCandidate MapItem(DiscoveryItemDto? item)
    {
        if (item is null) throw InvalidResponse();
        ValidateHttpsUrl(item.NormalizedFeedUrl);
        if (item.SiteUrl is not null) ValidateHttpsUrl(item.SiteUrl);
        ValidateText(item.Title, 160);
        if (item.LastUpdatedAt is null
            || item.LastUpdatedAt.Value.Offset != TimeSpan.Zero
            || item.Evidence is null
            || item.Evidence.Count == 0
            || item.Warnings is null
            || item.Catalog is null)
        {
            throw InvalidResponse();
        }

        FeedDocumentKind? documentKind = item.DocumentKind switch
        {
            null => null,
            "RSS20" => FeedDocumentKind.Rss20,
            "ATOM" => FeedDocumentKind.Atom,
            _ => throw InvalidResponse()
        };
        FeedDiscoveryHealth health = item.Health switch
        {
            "UNKNOWN" => FeedDiscoveryHealth.Unknown,
            "HEALTHY" => FeedDiscoveryHealth.Healthy,
            "DEGRADED" => FeedDiscoveryHealth.Degraded,
            "UNAVAILABLE" => FeedDiscoveryHealth.Unavailable,
            _ => throw InvalidResponse()
        };
        FeedDiscoveryEvidence[] evidence = item.Evidence
            .Select(MapEvidence)
            .ToArray();
        FeedDiscoveryWarning[] warnings = item.Warnings
            .Select(MapWarning)
            .ToArray();
        ValidateCatalog(item.Catalog);
        return new(
            item.NormalizedFeedUrl!,
            item.Title,
            item.SiteUrl,
            documentKind,
            item.LastUpdatedAt,
            health,
            evidence,
            warnings);
    }

    private static FeedDiscoveryEvidence MapEvidence(DiscoveryEvidenceDto? dto)
    {
        if (dto is null
            || dto.SourceId != ProviderSourceId
            || dto.SourceKind != "KNOWN_CATALOG")
        {
            throw InvalidResponse();
        }
        FeedDiscoveryMatchKind matchKind = dto.MatchKind switch
        {
            "EXACT_FEED_URL" => FeedDiscoveryMatchKind.ExactFeedUrl,
            "EXACT_SITE_URL" => FeedDiscoveryMatchKind.ExactSiteUrl,
            "EXACT_TITLE" => FeedDiscoveryMatchKind.ExactTitle,
            "KEYWORD" => FeedDiscoveryMatchKind.Keyword,
            _ => throw InvalidResponse()
        };
        FeedDiscoveryConfidence confidence = dto.Confidence switch
        {
            "EXACT" => FeedDiscoveryConfidence.Exact,
            "HIGH" => FeedDiscoveryConfidence.High,
            "MEDIUM" => FeedDiscoveryConfidence.Medium,
            "LOW" => FeedDiscoveryConfidence.Low,
            _ => throw InvalidResponse()
        };
        return new(
            ProviderSourceId,
            FeedDiscoverySourceKind.KnownCatalog,
            matchKind,
            confidence);
    }

    private static FeedDiscoveryWarning MapWarning(DiscoveryWarningDto? dto)
    {
        if (dto is null
            || (dto.SourceId is not null && dto.SourceId != ProviderSourceId))
            throw InvalidResponse();
        FeedDiscoveryWarningCode code = dto.Code switch
        {
            "STALE" => FeedDiscoveryWarningCode.Stale,
            "INSECURE_TRANSPORT" => FeedDiscoveryWarningCode.InsecureTransport,
            "UNVERIFIED" => FeedDiscoveryWarningCode.Unverified,
            "PROVIDER_PARTIAL_FAILURE" =>
                FeedDiscoveryWarningCode.ProviderPartialFailure,
            "RATE_LIMITED" => FeedDiscoveryWarningCode.RateLimited,
            _ => throw InvalidResponse()
        };
        return new(code, dto.SourceId);
    }

    private static void ValidateCatalog(DiscoveryCatalogDto dto)
    {
        if (!Guid.TryParseExact(dto.FeedId, "D", out _)
            || (dto.CategoryId is not null
                && !Guid.TryParseExact(dto.CategoryId, "D", out _))
            || (dto.CategoryId is null) != (dto.CategoryName is null)
            || (dto.CategoryName is not null
                && !IsValidText(dto.CategoryName, 80))
            || dto.ViewKind is not (
                "ARTICLE" or
                "PICTURE" or
                "AUDIO" or
                "VIDEO" or
                "NOTIFICATION")
            || !dto.IsEnabled)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateText(string? value, int maximumCodePoints)
    {
        if (!IsValidText(value, maximumCodePoints)) throw InvalidResponse();
    }

    private static bool IsValidText(string? value, int maximumCodePoints) =>
        !string.IsNullOrWhiteSpace(value)
        && value == value.Trim()
        && value.EnumerateRunes().Count() <= maximumCodePoints
        && !value.Any(char.IsControl);

    private static void ValidateHttpsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > FeedDiscoveryQueryClassifier.MaximumInputCodePoints
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.IdnHost)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw InvalidResponse();
        }
    }

    private static bool IsValidCursor(string? value) =>
        value is null
        || (value.Length is > 0 and <= 1024
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_'));

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw InvalidResponse();
        await using Stream input = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes)
                throw InvalidResponse();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static AppException InvalidResponse() => new(new(
        AppErrorCode.ProviderUnavailable,
        "已知目录响应无效",
        "云端已知目录返回了无法安全使用的发现结果。",
        "已忽略该来源；请稍后重试或检查 Worker 版本。",
        Provider: "LenxTool Worker",
        IsRetryable: true));

    private sealed class DiscoveryPageDto
    {
        public long CatalogVersion { get; init; }
        public string? Query { get; init; }
        public string? Scope { get; init; }
        public List<DiscoveryItemDto?>? Items { get; init; }
        public DiscoveryPaginationDto? Pagination { get; init; }
    }

    private sealed class DiscoveryItemDto
    {
        public string? NormalizedFeedUrl { get; init; }
        public string? Title { get; init; }
        public string? SiteUrl { get; init; }
        public string? DocumentKind { get; init; }
        public DateTimeOffset? LastUpdatedAt { get; init; }
        public string? Health { get; init; }
        public List<DiscoveryEvidenceDto?>? Evidence { get; init; }
        public List<DiscoveryWarningDto?>? Warnings { get; init; }
        public DiscoveryCatalogDto? Catalog { get; init; }
    }

    private sealed class DiscoveryEvidenceDto
    {
        public string? SourceId { get; init; }
        public string? SourceKind { get; init; }
        public string? MatchKind { get; init; }
        public string? Confidence { get; init; }
    }

    private sealed class DiscoveryWarningDto
    {
        public string? Code { get; init; }
        public string? SourceId { get; init; }
    }

    private sealed class DiscoveryCatalogDto
    {
        public string? FeedId { get; init; }
        public string? CategoryId { get; init; }
        public string? CategoryName { get; init; }
        public string? ViewKind { get; init; }
        public bool IsEnabled { get; init; }
    }

    private sealed class DiscoveryPaginationDto
    {
        public int PageSize { get; init; }
        public int TotalItems { get; init; }
        public string? NextCursor { get; init; }
    }
}
