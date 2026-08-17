using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed class FeedAutomationRuleAdminService(
    WorkerAccountSessionService accountSession)
    : IFeedAutomationRuleAdminService
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<FeedAutomationRuleSnapshot> GetAllAsync(
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response =
            await accountSession.GetAuthorizedAsync(
                    "/v1/automation-rules?scope=ALL",
                    cancellationToken)
                .ConfigureAwait(false);
        await WorkerAccountSessionService.EnsureSuccessAsync(
                response,
                cancellationToken)
            .ConfigureAwait(false);
        RuleSnapshotDto dto = await ReadBoundedJsonAsync<RuleSnapshotDto>(
                response,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!string.Equals(dto.Scope, "ALL", StringComparison.Ordinal)
                || dto.RuleSetVersion is < 0 or > MaximumSafeInteger
                || dto.GeneratedAt is null
                || dto.GeneratedAt.Value.Offset != TimeSpan.Zero
                || dto.Rules is null
                || dto.Rules.Count > FeedAutomationRuleInterpreter.MaximumRuleCount)
            {
                throw InvalidResponse();
            }
            FeedAutomationRule[] rules = dto.Rules
                .Select(MapRule)
                .ToArray();
            _ = FeedAutomationRuleInterpreter.Compile(rules);
            return new(
                dto.RuleSetVersion,
                dto.GeneratedAt,
                LastSyncedAt: null,
                rules);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidDataException
                or InvalidOperationException)
        {
            throw new AppException(InvalidResponse().Error, exception);
        }
    }

    public Task<FeedAutomationRuleMutationResult> CreateAsync(
        FeedAutomationRuleDefinition definition,
        long expectedRuleSetVersion,
        CancellationToken cancellationToken) =>
        MutateAsync(
            HttpMethod.Post,
            "/v1/admin/automation-rules",
            expectedRuleSetVersion,
            definition,
            expectedRuleId: null,
            cancellationToken);

    public Task<FeedAutomationRuleMutationResult> UpdateAsync(
        string ruleId,
        FeedAutomationRuleDefinition definition,
        long expectedRuleSetVersion,
        CancellationToken cancellationToken)
    {
        string id = ValidateId(ruleId);
        return MutateAsync(
            HttpMethod.Patch,
            $"/v1/admin/automation-rules/{id}",
            expectedRuleSetVersion,
            definition,
            id,
            cancellationToken);
    }

    private async Task<FeedAutomationRuleMutationResult> MutateAsync(
        HttpMethod method,
        string path,
        long expectedRuleSetVersion,
        FeedAutomationRuleDefinition definition,
        string? expectedRuleId,
        CancellationToken cancellationToken)
    {
        ValidateVersion(expectedRuleSetVersion);
        FeedAutomationRuleDefinition normalized =
            FeedAutomationRuleValidator.ValidateAndNormalizeDefinition(definition);
        using HttpResponseMessage response =
            await accountSession.SendAutomationMutationAsync(
                    method,
                    path,
                    expectedRuleSetVersion,
                    ToPayload(normalized),
                    cancellationToken)
                .ConfigureAwait(false);
        await WorkerAccountSessionService.EnsureSuccessAsync(
                response,
                cancellationToken)
            .ConfigureAwait(false);
        RuleMutationDto dto = await ReadBoundedJsonAsync<RuleMutationDto>(
                response,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            FeedAutomationRule rule = MapRule(dto.Rule);
            if (dto.RuleSetVersion != expectedRuleSetVersion + 1
                || dto.RuleSetVersion > MaximumSafeInteger
                || (expectedRuleId is not null
                    && !string.Equals(
                        expectedRuleId,
                        rule.Id,
                        StringComparison.Ordinal)))
            {
                throw InvalidResponse();
            }
            return new(dto.RuleSetVersion, rule);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidDataException
                or InvalidOperationException)
        {
            throw new AppException(InvalidResponse().Error, exception);
        }
    }

    private static object ToPayload(
        FeedAutomationRuleDefinition definition) => new
        {
            definition.Name,
            definition.Priority,
            definition.ConflictOrder,
            definition.IsEnabled,
            MatchMode = definition.MatchMode switch
            {
                FeedAutomationMatchMode.All => "ALL",
                FeedAutomationMatchMode.Any => "ANY",
                _ => throw new ArgumentOutOfRangeException(nameof(definition))
            },
            Conditions = definition.Conditions.Select(condition => new
            {
                Field = ToWireValue(condition.Field),
                Operator = ToWireValue(condition.Operator),
                condition.Value
            }),
            Actions = definition.Actions.Select(action => new
            {
                Type = ToWireValue(action.Type),
                action.Order,
                action.Value
            })
        };

    private static FeedAutomationRule MapRule(RuleDto? dto)
    {
        if (dto is null)
        {
            throw InvalidResponse();
        }
        return FeedAutomationRuleValidator.ValidateAndNormalize(new(
            dto.Id ?? throw InvalidResponse(),
            dto.Version,
            dto.Name ?? throw InvalidResponse(),
            dto.Priority,
            dto.ConflictOrder,
            dto.IsEnabled,
            dto.MatchMode switch
            {
                "ALL" => FeedAutomationMatchMode.All,
                "ANY" => FeedAutomationMatchMode.Any,
                _ => throw InvalidResponse()
            },
            dto.Conditions?
                .Select(MapCondition)
                .ToArray()
                ?? throw InvalidResponse(),
            dto.Actions?
                .Select(MapAction)
                .ToArray()
                ?? throw InvalidResponse()));
    }

    private static FeedAutomationCondition MapCondition(
        ConditionDto? dto)
    {
        if (dto is null)
        {
            throw InvalidResponse();
        }
        return new(
            dto.Field switch
            {
                "FEED" => FeedAutomationField.Feed,
                "CATEGORY" => FeedAutomationField.Category,
                "TITLE" => FeedAutomationField.Title,
                "AUTHOR" => FeedAutomationField.Author,
                "CONTENT" => FeedAutomationField.Content,
                "LANGUAGE" => FeedAutomationField.Language,
                "PUBLISHED_AT" => FeedAutomationField.PublishedAt,
                "HAS_AUDIO" => FeedAutomationField.HasAudio,
                "HAS_VIDEO" => FeedAutomationField.HasVideo,
                _ => throw InvalidResponse()
            },
            dto.Operator switch
            {
                "EQUALS" => FeedAutomationOperator.Equals,
                "CONTAINS" => FeedAutomationOperator.Contains,
                "REGEX" => FeedAutomationOperator.Regex,
                "BEFORE" => FeedAutomationOperator.Before,
                "AFTER" => FeedAutomationOperator.After,
                "EXISTS" => FeedAutomationOperator.Exists,
                _ => throw InvalidResponse()
            },
            dto.Value);
    }

    private static FeedAutomationAction MapAction(ActionDto? dto)
    {
        if (dto is null)
        {
            throw InvalidResponse();
        }
        return new(
            dto.Type switch
            {
                "ADD_TAG" => FeedAutomationActionType.AddTag,
                "HIDE" => FeedAutomationActionType.Hide,
                "MARK_READ" => FeedAutomationActionType.MarkRead,
                "GENERATE_SUMMARY" =>
                    FeedAutomationActionType.GenerateSummary,
                "TRANSLATE" => FeedAutomationActionType.Translate,
                "SEND_TO_MEDIA" => FeedAutomationActionType.SendToMedia,
                "NOTIFY" => FeedAutomationActionType.Notify,
                _ => throw InvalidResponse()
            },
            dto.Order,
            dto.Value);
    }

    private static string ToWireValue(FeedAutomationField value) => value switch
    {
        FeedAutomationField.Feed => "FEED",
        FeedAutomationField.Category => "CATEGORY",
        FeedAutomationField.Title => "TITLE",
        FeedAutomationField.Author => "AUTHOR",
        FeedAutomationField.Content => "CONTENT",
        FeedAutomationField.Language => "LANGUAGE",
        FeedAutomationField.PublishedAt => "PUBLISHED_AT",
        FeedAutomationField.HasAudio => "HAS_AUDIO",
        FeedAutomationField.HasVideo => "HAS_VIDEO",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string ToWireValue(
        FeedAutomationOperator value) => value switch
        {
            FeedAutomationOperator.Equals => "EQUALS",
            FeedAutomationOperator.Contains => "CONTAINS",
            FeedAutomationOperator.Regex => "REGEX",
            FeedAutomationOperator.Before => "BEFORE",
            FeedAutomationOperator.After => "AFTER",
            FeedAutomationOperator.Exists => "EXISTS",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static string ToWireValue(
        FeedAutomationActionType value) => value switch
        {
            FeedAutomationActionType.AddTag => "ADD_TAG",
            FeedAutomationActionType.Hide => "HIDE",
            FeedAutomationActionType.MarkRead => "MARK_READ",
            FeedAutomationActionType.GenerateSummary => "GENERATE_SUMMARY",
            FeedAutomationActionType.Translate => "TRANSLATE",
            FeedAutomationActionType.SendToMedia => "SEND_TO_MEDIA",
            FeedAutomationActionType.Notify => "NOTIFY",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static async Task<T> ReadBoundedJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw InvalidResponse();
        }
        await using Stream input =
            await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > MaximumResponseBytes)
            {
                throw InvalidResponse();
            }
            output.Write(buffer, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(output.GetBuffer().AsSpan(0, total), JsonOptions)
                ?? throw InvalidResponse();
        }
        catch (JsonException exception)
        {
            throw new AppException(InvalidResponse().Error, exception);
        }
    }

    private static string ValidateId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed))
        {
            throw new ArgumentException(
                "Rule ID must be a canonical UUID.",
                nameof(value));
        }
        return parsed.ToString("D");
    }

    private static void ValidateVersion(long value)
    {
        if (value is < 0 or > MaximumSafeInteger)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static AppException InvalidResponse() => new(new(
        AppErrorCode.ProviderUnavailable,
        "自动化规则响应无效",
        "云服务没有返回预期的规则版本。",
        "当前更改状态未知；请刷新规则后再决定是否重试。",
        Provider: "LenxTool Worker",
        IsRetryable: true));

    private sealed class RuleSnapshotDto
    {
        public long RuleSetVersion { get; init; }
        public string? Scope { get; init; }
        public DateTimeOffset? GeneratedAt { get; init; }
        public List<RuleDto?>? Rules { get; init; }
    }

    private sealed class RuleMutationDto
    {
        public long RuleSetVersion { get; init; }
        public RuleDto? Rule { get; init; }
    }

    private sealed class RuleDto
    {
        public string? Id { get; init; }
        public int Version { get; init; }
        public string? Name { get; init; }
        public int Priority { get; init; }
        public int ConflictOrder { get; init; }
        public bool IsEnabled { get; init; }
        public string? MatchMode { get; init; }
        public List<ConditionDto?>? Conditions { get; init; }
        public List<ActionDto?>? Actions { get; init; }
    }

    private sealed class ConditionDto
    {
        public string? Field { get; init; }
        public string? Operator { get; init; }
        public string? Value { get; init; }
    }

    private sealed class ActionDto
    {
        public string? Type { get; init; }
        public int Order { get; init; }
        public string? Value { get; init; }
    }
}
