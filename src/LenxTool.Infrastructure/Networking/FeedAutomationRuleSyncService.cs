using System.Globalization;
using System.Net;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed class FeedAutomationRuleSyncService(
    WorkerAccountSessionService accountSession,
    IFeedAutomationRuleRepository repository,
    TimeProvider timeProvider)
    : IFeedAutomationRuleSyncService, IDisposable
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _synchronizationGate = new(1, 1);
    private bool _disposed;

    public async Task<FeedAutomationRuleSyncResult> SyncAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!accountSession.Current.IsAuthenticated)
        {
            return new(
                FeedAutomationRuleSyncOutcome.SkippedNotAuthenticated,
                0,
                null);
        }

        await _synchronizationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            FeedAutomationRuleSnapshot local =
                await repository.GetAsync(cancellationToken)
                    .ConfigureAwait(false);
            string path = string.Create(
                CultureInfo.InvariantCulture,
                $"/v1/automation-rules?scope=ACTIVE&afterVersion={local.RuleSetVersion}");
            using HttpResponseMessage response =
                await accountSession.GetAuthorizedAsync(
                        path,
                        cancellationToken)
                    .ConfigureAwait(false);
            DateTimeOffset synchronizedAt = timeProvider.GetUtcNow();

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                await RecordUnchangedAsync(
                        local,
                        synchronizedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new(
                    FeedAutomationRuleSyncOutcome.Unchanged,
                    local.RuleSetVersion,
                    synchronizedAt);
            }

            await WorkerAccountSessionService.EnsureSuccessAsync(
                    response,
                    cancellationToken)
                .ConfigureAwait(false);
            FeedAutomationRuleSnapshot snapshot = await ReadSnapshotAsync(
                    response,
                    local.RuleSetVersion,
                    synchronizedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            await repository.ReplaceAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            return new(
                FeedAutomationRuleSyncOutcome.Updated,
                snapshot.RuleSetVersion,
                synchronizedAt);
        }
        finally
        {
            _synchronizationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _synchronizationGate.Dispose();
    }

    private async Task RecordUnchangedAsync(
        FeedAutomationRuleSnapshot local,
        DateTimeOffset synchronizedAt,
        CancellationToken cancellationToken)
    {
        if (local.RuleSetVersion == 0
            && local.LastSyncedAt is null)
        {
            await repository.ReplaceAsync(
                    new(
                        0,
                        GeneratedAt: null,
                        LastSyncedAt: synchronizedAt,
                        Rules: Array.Empty<FeedAutomationRule>()),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        bool updated = await repository.MarkSynchronizedAsync(
                local.RuleSetVersion,
                synchronizedAt,
                cancellationToken)
            .ConfigureAwait(false);
        if (!updated)
        {
            throw new InvalidOperationException(
                "The local automation rule version changed during synchronization.");
        }
    }

    private static async Task<FeedAutomationRuleSnapshot> ReadSnapshotAsync(
        HttpResponseMessage response,
        long localVersion,
        DateTimeOffset synchronizedAt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw CreateInvalidSnapshotException();
        }

        byte[] payload = await ReadBoundedAsync(
                response.Content,
                cancellationToken)
            .ConfigureAwait(false);
        RuleSnapshotDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<RuleSnapshotDto>(
                    payload,
                    JsonOptions)
                ?? throw CreateInvalidSnapshotException();
        }
        catch (JsonException exception)
        {
            throw new AppException(
                CreateInvalidSnapshotException().Error,
                exception);
        }

        try
        {
            return MapSnapshot(dto, localVersion, synchronizedAt);
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
            throw new AppException(
                CreateInvalidSnapshotException().Error,
                exception);
        }
    }

    private static FeedAutomationRuleSnapshot MapSnapshot(
        RuleSnapshotDto dto,
        long localVersion,
        DateTimeOffset synchronizedAt)
    {
        if (!string.Equals(dto.Scope, "ACTIVE", StringComparison.Ordinal)
            || dto.RuleSetVersion <= localVersion
            || dto.RuleSetVersion > MaximumSafeInteger
            || dto.GeneratedAt is null
            || dto.GeneratedAt.Value.Offset != TimeSpan.Zero
            || dto.Rules is null
            || dto.Rules.Count
                > FeedAutomationRuleInterpreter.MaximumRuleCount)
        {
            throw CreateInvalidSnapshotException();
        }

        FeedAutomationRule[] normalized = dto.Rules
            .Select(MapRule)
            .Select(FeedAutomationRuleValidator.ValidateAndNormalize)
            .ToArray();
        if (normalized.Any(rule => !rule.IsEnabled))
        {
            throw CreateInvalidSnapshotException();
        }
        _ = FeedAutomationRuleInterpreter.Compile(normalized);
        return new(
            dto.RuleSetVersion,
            dto.GeneratedAt,
            synchronizedAt,
            normalized);
    }

    private static FeedAutomationRule MapRule(RuleDto? dto)
    {
        if (dto is null)
        {
            throw CreateInvalidSnapshotException();
        }
        return new(
            dto.Id
                ?? throw CreateInvalidSnapshotException(),
            dto.Version,
            dto.Name
                ?? throw CreateInvalidSnapshotException(),
            dto.Priority,
            dto.ConflictOrder,
            dto.IsEnabled,
            dto.MatchMode switch
            {
                "ALL" => FeedAutomationMatchMode.All,
                "ANY" => FeedAutomationMatchMode.Any,
                _ => throw CreateInvalidSnapshotException()
            },
            dto.Conditions?
                .Select(MapCondition)
                .ToArray()
                ?? throw CreateInvalidSnapshotException(),
            dto.Actions?
                .Select(MapAction)
                .ToArray()
                ?? throw CreateInvalidSnapshotException());
    }

    private static FeedAutomationCondition MapCondition(
        ConditionDto? dto)
    {
        if (dto is null)
        {
            throw CreateInvalidSnapshotException();
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
                _ => throw CreateInvalidSnapshotException()
            },
            dto.Operator switch
            {
                "EQUALS" => FeedAutomationOperator.Equals,
                "CONTAINS" => FeedAutomationOperator.Contains,
                "REGEX" => FeedAutomationOperator.Regex,
                "BEFORE" => FeedAutomationOperator.Before,
                "AFTER" => FeedAutomationOperator.After,
                "EXISTS" => FeedAutomationOperator.Exists,
                _ => throw CreateInvalidSnapshotException()
            },
            dto.Value);
    }

    private static FeedAutomationAction MapAction(ActionDto? dto)
    {
        if (dto is null)
        {
            throw CreateInvalidSnapshotException();
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
                _ => throw CreateInvalidSnapshotException()
            },
            dto.Order,
            dto.Value);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw CreateInvalidSnapshotException();
        }

        await using Stream input =
            await content.ReadAsStreamAsync(cancellationToken)
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
                throw CreateInvalidSnapshotException();
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static AppException CreateInvalidSnapshotException() =>
        new(new(
            AppErrorCode.ProviderUnavailable,
            "自动化规则响应无效",
            "云服务返回了无法安全应用的自动化规则快照。",
            "已保留上次成功规则；请稍后重试或联系管理员检查 Worker 版本。",
            Provider: "LenxTool Worker",
            IsRetryable: true));

    private sealed class RuleSnapshotDto
    {
        public long RuleSetVersion { get; init; }
        public string? Scope { get; init; }
        public DateTimeOffset? GeneratedAt { get; init; }
        public List<RuleDto?>? Rules { get; init; }
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
