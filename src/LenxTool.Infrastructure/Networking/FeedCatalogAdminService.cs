using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed class FeedCatalogAdminService(
    WorkerAccountSessionService accountSession) : IFeedCatalogAdminService
{
    private const long MaximumCatalogVersion = 9_007_199_254_740_991;

    public Task<long> CreateCategoryAsync(
        FeedCategoryInput input,
        long expectedCatalogVersion,
        CancellationToken cancellationToken)
    {
        ValidateCategory(input);
        return SendAsync(
            HttpMethod.Post,
            "/v1/admin/feed-categories",
            expectedCatalogVersion,
            new { input.Name, input.SortOrder, input.IsEnabled },
            cancellationToken);
    }

    public Task<long> UpdateCategoryAsync(
        string categoryId,
        FeedCategoryInput input,
        long expectedCatalogVersion,
        CancellationToken cancellationToken)
    {
        string id = ValidateId(categoryId, nameof(categoryId));
        ValidateCategory(input);
        return SendAsync(
            HttpMethod.Patch,
            $"/v1/admin/feed-categories/{id}",
            expectedCatalogVersion,
            new { input.Name, input.SortOrder, input.IsEnabled },
            cancellationToken);
    }

    public Task<long> DeleteCategoryAsync(
        string categoryId,
        long expectedCatalogVersion,
        CancellationToken cancellationToken) => SendAsync(
            HttpMethod.Delete,
            $"/v1/admin/feed-categories/{ValidateId(categoryId, nameof(categoryId))}",
            expectedCatalogVersion,
            null,
            cancellationToken);

    public Task<long> CreateFeedAsync(
        FeedCatalogItemInput input,
        long expectedCatalogVersion,
        CancellationToken cancellationToken)
    {
        ValidateFeed(input);
        return SendAsync(
            HttpMethod.Post,
            "/v1/admin/feeds",
            expectedCatalogVersion,
            ToPayload(input),
            cancellationToken);
    }

    public Task<long> UpdateFeedAsync(
        string feedId,
        FeedCatalogItemInput input,
        long expectedCatalogVersion,
        CancellationToken cancellationToken)
    {
        string id = ValidateId(feedId, nameof(feedId));
        ValidateFeed(input);
        return SendAsync(
            HttpMethod.Patch,
            $"/v1/admin/feeds/{id}",
            expectedCatalogVersion,
            ToPayload(input),
            cancellationToken);
    }

    public Task<long> DeleteFeedAsync(
        string feedId,
        long expectedCatalogVersion,
        CancellationToken cancellationToken) => SendAsync(
            HttpMethod.Delete,
            $"/v1/admin/feeds/{ValidateId(feedId, nameof(feedId))}",
            expectedCatalogVersion,
            null,
            cancellationToken);

    private async Task<long> SendAsync(
        HttpMethod method,
        string path,
        long expectedCatalogVersion,
        object? payload,
        CancellationToken cancellationToken)
    {
        ValidateCatalogVersion(expectedCatalogVersion);
        using HttpResponseMessage response = await accountSession.SendCatalogMutationAsync(
            method,
            path,
            expectedCatalogVersion,
            payload,
            cancellationToken).ConfigureAwait(false);
        await WorkerAccountSessionService.EnsureSuccessAsync(response, cancellationToken)
            .ConfigureAwait(false);
        CatalogMutationDto result = await WorkerAccountSessionService
            .ReadJsonAsync<CatalogMutationDto>(response, cancellationToken)
            .ConfigureAwait(false);
        if (result.CatalogVersion != expectedCatalogVersion + 1
            || result.CatalogVersion > MaximumCatalogVersion)
        {
            throw InvalidResponse();
        }
        return result.CatalogVersion;
    }

    private static object ToPayload(FeedCatalogItemInput input) => new
    {
        input.OriginalUrl,
        input.DisplayName,
        input.SiteUrl,
        input.CategoryId,
        ViewKind = input.ViewKind.ToString().ToUpperInvariant(),
        input.RefreshIntervalMinutes,
        input.SortOrder,
        input.IsEnabled
    };

    private static void ValidateCategory(FeedCategoryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateText(input.Name, 80, nameof(input.Name));
        ValidateSortOrder(input.SortOrder, nameof(input.SortOrder));
    }

    private static void ValidateFeed(FeedCatalogItemInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateHttpsUrl(input.OriginalUrl, nameof(input.OriginalUrl), required: true);
        ValidateText(input.DisplayName, 160, nameof(input.DisplayName));
        ValidateHttpsUrl(input.SiteUrl, nameof(input.SiteUrl), required: false);
        if (input.CategoryId is not null) ValidateId(input.CategoryId, nameof(input.CategoryId));
        if (!Enum.IsDefined(input.ViewKind))
            throw new ArgumentOutOfRangeException(nameof(input), "ViewKind is invalid.");
        if (input.RefreshIntervalMinutes is < 5 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(input), "RefreshIntervalMinutes is invalid.");
        ValidateSortOrder(input.SortOrder, nameof(input.SortOrder));
    }

    private static string ValidateId(string value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id)) throw new ArgumentException(
            "Catalog resource identifiers must be canonical UUIDs.",
            parameterName);
        return id.ToString("D");
    }

    private static void ValidateCatalogVersion(long version)
    {
        if (version is < 0 or > MaximumCatalogVersion)
            throw new ArgumentOutOfRangeException(nameof(version));
    }

    private static void ValidateSortOrder(int value, string parameterName)
    {
        if (value is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateText(string value, int maximumCodePoints, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        string trimmed = value.Trim();
        if (trimmed.Length == 0
            || !string.Equals(trimmed, value, StringComparison.Ordinal)
            || trimmed.EnumerateRunes().Count() > maximumCodePoints
            || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("Catalog text is invalid.", parameterName);
        }
    }

    private static void ValidateHttpsUrl(string? value, string parameterName, bool required)
    {
        if (!required && value is null) return;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || value != value.Trim()
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.IsDefaultPort)
        {
            throw new ArgumentException("Catalog URLs must be bounded public HTTPS URLs.", parameterName);
        }
    }

    private static AppException InvalidResponse() => new(new(
        AppErrorCode.ProviderUnavailable,
        "目录写入响应无效",
        "云服务没有返回预期的新目录版本。",
        "当前更改状态未知；请刷新目录后再决定是否重试。",
        Provider: "LenxTool Worker",
        IsRetryable: true));

    private sealed class CatalogMutationDto
    {
        public long CatalogVersion { get; init; }
    }
}
