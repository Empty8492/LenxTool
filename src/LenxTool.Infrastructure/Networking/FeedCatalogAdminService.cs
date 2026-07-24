using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed class FeedCatalogAdminService(
    WorkerAccountSessionService accountSession) : IFeedCatalogAdminService, IFeedCatalogBatchService
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

    public async Task<FeedCatalogBatchResult> ApplyAsync(
        IReadOnlyList<FeedCatalogBatchOperation> operations,
        long expectedCatalogVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ValidateCatalogVersion(expectedCatalogVersion);
        if (operations.Count is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(operations), "A catalog batch must contain 1 to 100 operations.");

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var createdCategoryIds = new HashSet<string>(StringComparer.Ordinal);
        var payloads = new List<Dictionary<string, object?>>(operations.Count);
        foreach (FeedCatalogBatchOperation operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ValidateOperationId(operation.OperationId);
            if (!operationIds.Add(operation.OperationId))
                throw new ArgumentException("Catalog batch operation identifiers must be unique.", nameof(operations));
            payloads.Add(ToBatchPayload(operation, createdCategoryIds));
            if (operation.Type == FeedCatalogBatchOperationType.CreateCategory)
                createdCategoryIds.Add(operation.OperationId);
        }

        using HttpResponseMessage response = await accountSession.SendCatalogMutationAsync(
            HttpMethod.Post,
            "/v1/admin/feed-catalog-batches",
            expectedCatalogVersion,
            new { Operations = payloads },
            cancellationToken).ConfigureAwait(false);
        await WorkerAccountSessionService.EnsureSuccessAsync(response, cancellationToken)
            .ConfigureAwait(false);
        CatalogBatchDto result = await WorkerAccountSessionService
            .ReadJsonAsync<CatalogBatchDto>(response, cancellationToken)
            .ConfigureAwait(false);
        if (result.CatalogVersion != expectedCatalogVersion + 1
            || result.CatalogVersion > MaximumCatalogVersion
            || result.Results is null
            || result.Results.Count != operations.Count)
        {
            throw InvalidResponse();
        }

        var mapped = new List<FeedCatalogBatchOperationResult>(result.Results.Count);
        for (int index = 0; index < result.Results.Count; index++)
        {
            CatalogBatchOperationDto item = result.Results[index];
            FeedCatalogBatchOperation source = operations[index];
            string expectedType = IsCategoryOperation(source.Type) ? "FEED_CATEGORY" : "FEED";
            if (!string.Equals(item.OperationId, source.OperationId, StringComparison.Ordinal)
                || !string.Equals(item.ResourceType, expectedType, StringComparison.Ordinal)
                || !Guid.TryParseExact(item.ResourceId, "D", out Guid resourceId))
            {
                throw InvalidResponse();
            }
            mapped.Add(new(item.OperationId, item.ResourceType, resourceId.ToString("D")));
        }
        return new(result.CatalogVersion, mapped);
    }

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
        FullTextPolicy = ToWireValue(input.FullTextPolicy),
        input.RefreshIntervalMinutes,
        input.SortOrder,
        input.IsEnabled
    };

    private static Dictionary<string, object?> ToBatchPayload(
        FeedCatalogBatchOperation operation,
        ISet<string> createdCategoryIds)
    {
        var payload = new Dictionary<string, object?>
        {
            ["operationId"] = operation.OperationId,
            ["type"] = ToWireType(operation.Type)
        };
        switch (operation.Type)
        {
            case FeedCatalogBatchOperationType.CreateCategory:
                RequireAbsentResourceIds(operation);
                payload["input"] = ToCategoryBatchInput(RequireCategoryInput(operation));
                break;
            case FeedCatalogBatchOperationType.PatchCategory:
                RequireNoFeedFields(operation);
                payload["categoryId"] = ValidateId(operation.CategoryId ?? string.Empty, nameof(operation.CategoryId));
                payload["input"] = ToCategoryBatchInput(RequireCategoryInput(operation));
                break;
            case FeedCatalogBatchOperationType.DeleteCategory:
                RequireNoFeedFields(operation);
                if (operation.CategoryInput is not null) throw InvalidOperationShape();
                payload["categoryId"] = ValidateId(operation.CategoryId ?? string.Empty, nameof(operation.CategoryId));
                break;
            case FeedCatalogBatchOperationType.CreateFeed:
                RequireAbsentResourceIds(operation);
                if (operation.CategoryInput is not null) throw InvalidOperationShape();
                payload["input"] = ToFeedBatchInput(operation, createdCategoryIds);
                break;
            case FeedCatalogBatchOperationType.PatchFeed:
                RequireNoCategoryFields(operation);
                payload["feedId"] = ValidateId(operation.FeedId ?? string.Empty, nameof(operation.FeedId));
                payload["input"] = ToFeedBatchInput(operation, createdCategoryIds);
                break;
            case FeedCatalogBatchOperationType.DeleteFeed:
                RequireNoCategoryFields(operation);
                if (operation.FeedInput is not null || operation.CategoryOperationId is not null)
                    throw InvalidOperationShape();
                payload["feedId"] = ValidateId(operation.FeedId ?? string.Empty, nameof(operation.FeedId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), "Catalog batch operation type is invalid.");
        }
        return payload;
    }

    private static Dictionary<string, object?> ToCategoryBatchInput(FeedCategoryInput input)
    {
        ValidateCategory(input);
        return new()
        {
            ["name"] = input.Name,
            ["sortOrder"] = input.SortOrder,
            ["isEnabled"] = input.IsEnabled
        };
    }

    private static Dictionary<string, object?> ToFeedBatchInput(
        FeedCatalogBatchOperation operation,
        ISet<string> createdCategoryIds)
    {
        FeedCatalogItemInput input = operation.FeedInput ?? throw InvalidOperationShape();
        ValidateFeed(input);
        if (operation.CategoryOperationId is not null && input.CategoryId is not null)
            throw InvalidOperationShape();
        var payload = new Dictionary<string, object?>
        {
            ["originalUrl"] = input.OriginalUrl,
            ["displayName"] = input.DisplayName,
            ["siteUrl"] = input.SiteUrl,
            ["viewKind"] = input.ViewKind.ToString().ToUpperInvariant(),
            ["fullTextPolicy"] = ToWireValue(input.FullTextPolicy),
            ["refreshIntervalMinutes"] = input.RefreshIntervalMinutes,
            ["sortOrder"] = input.SortOrder,
            ["isEnabled"] = input.IsEnabled
        };
        if (operation.CategoryOperationId is null)
        {
            payload["categoryId"] = input.CategoryId;
        }
        else
        {
            ValidateOperationId(operation.CategoryOperationId);
            if (!createdCategoryIds.Contains(operation.CategoryOperationId))
                throw new ArgumentException("A category reference must target an earlier create operation.", nameof(operation));
            payload["categoryRef"] = new Dictionary<string, object?>
            {
                ["operationId"] = operation.CategoryOperationId
            };
        }
        return payload;
    }

    private static FeedCategoryInput RequireCategoryInput(FeedCatalogBatchOperation operation)
    {
        if (operation.CategoryInput is null || operation.FeedInput is not null || operation.CategoryOperationId is not null)
            throw InvalidOperationShape();
        return operation.CategoryInput;
    }

    private static void RequireAbsentResourceIds(FeedCatalogBatchOperation operation)
    {
        if (operation.CategoryId is not null || operation.FeedId is not null) throw InvalidOperationShape();
    }

    private static void RequireNoFeedFields(FeedCatalogBatchOperation operation)
    {
        if (operation.FeedId is not null || operation.FeedInput is not null || operation.CategoryOperationId is not null)
            throw InvalidOperationShape();
    }

    private static void RequireNoCategoryFields(FeedCatalogBatchOperation operation)
    {
        if (operation.CategoryId is not null || operation.CategoryInput is not null) throw InvalidOperationShape();
    }

    private static ArgumentException InvalidOperationShape() => new(
        "Catalog batch operation fields do not match its type.",
        "operations");

    private static void ValidateOperationId(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 64
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '.' and not '_' and not ':' and not '-'))
        {
            throw new ArgumentException("Catalog batch operation identifiers are invalid.", nameof(value));
        }
    }

    private static bool IsCategoryOperation(FeedCatalogBatchOperationType type) => type is
        FeedCatalogBatchOperationType.CreateCategory
        or FeedCatalogBatchOperationType.PatchCategory
        or FeedCatalogBatchOperationType.DeleteCategory;

    private static string ToWireType(FeedCatalogBatchOperationType type) => type switch
    {
        FeedCatalogBatchOperationType.CreateCategory => "CREATE_CATEGORY",
        FeedCatalogBatchOperationType.PatchCategory => "PATCH_CATEGORY",
        FeedCatalogBatchOperationType.DeleteCategory => "DELETE_CATEGORY",
        FeedCatalogBatchOperationType.CreateFeed => "CREATE_FEED",
        FeedCatalogBatchOperationType.PatchFeed => "PATCH_FEED",
        FeedCatalogBatchOperationType.DeleteFeed => "DELETE_FEED",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
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
        if (!Enum.IsDefined(input.FullTextPolicy))
            throw new ArgumentOutOfRangeException(nameof(input), "FullTextPolicy is invalid.");
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

    private static string ToWireValue(FeedFullTextPolicy policy) => policy switch
    {
        FeedFullTextPolicy.None => "NONE",
        FeedFullTextPolicy.OnOpen => "ON_OPEN",
        FeedFullTextPolicy.Background => "BACKGROUND",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

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

    private sealed class CatalogBatchDto
    {
        public long CatalogVersion { get; init; }
        public List<CatalogBatchOperationDto>? Results { get; init; }
    }

    private sealed class CatalogBatchOperationDto
    {
        public string OperationId { get; init; } = string.Empty;
        public string ResourceType { get; init; } = string.Empty;
        public string ResourceId { get; init; } = string.Empty;
    }
}
