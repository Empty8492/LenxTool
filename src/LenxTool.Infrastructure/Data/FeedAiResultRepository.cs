using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedAiResultRepository(SqliteDatabase database) : IFeedAiResultRepository
{
    public async Task UpsertAsync(
        FeedAiResult result,
        CancellationToken cancellationToken)
    {
        ValidateResult(result);

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ai_reports(
                id, entity_type, entity_id, report_type, title, content, model,
                request_count, token_usage, created_at, content_hash, target_language,
                prompt_version, prompt_tokens, completion_tokens, duration_ms,
                error_code, updated_at)
            VALUES(
                $id, 'feed_entry', $entryId, $reportType, $title, $content, $model,
                $requestCount, $totalTokens, $createdAt, $contentHash, $targetLanguage,
                $promptVersion, $promptTokens, $completionTokens, $durationMs,
                $errorCode, $updatedAt)
            ON CONFLICT(
                entity_id, content_hash, report_type, target_language, model, prompt_version)
            WHERE entity_type='feed_entry'
              AND entity_id IS NOT NULL
              AND content_hash IS NOT NULL
              AND target_language IS NOT NULL
              AND prompt_version IS NOT NULL
            DO UPDATE SET
                title=excluded.title,
                content=excluded.content,
                request_count=excluded.request_count,
                token_usage=excluded.token_usage,
                prompt_tokens=excluded.prompt_tokens,
                completion_tokens=excluded.completion_tokens,
                duration_ms=excluded.duration_ms,
                error_code=excluded.error_code,
                updated_at=excluded.updated_at
            RETURNING id;
            """;
        AddResultParameters(command, result);
        string storedId = (string)(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false))!;

        command.Parameters.Clear();
        command.CommandText = """
            DELETE FROM content_fts
            WHERE entity_type='report' AND entity_id=$id;
            """;
        command.Parameters.AddWithValue("$id", storedId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO content_fts(entity_type, entity_id, title, content)
            VALUES('report', $id, $title, $content);
            """;
        command.Parameters.AddWithValue("$id", storedId);
        command.Parameters.AddWithValue("$title", result.Title);
        command.Parameters.AddWithValue("$content", result.Content);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FeedAiResult?> GetCurrentAsync(
        FeedAiCacheKey key,
        CancellationToken cancellationToken)
    {
        ValidateKey(key);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectColumns}
            WHERE entity_type='feed_entry'
              AND entity_id=$entryId
              AND content_hash=$contentHash
              AND report_type=$reportType
              AND target_language=$targetLanguage
              AND model=$model
              AND prompt_version=$promptVersion
            LIMIT 1;
            """;
        AddKeyParameters(command, key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadResult(reader)
            : null;
    }

    public async Task<IReadOnlyList<FeedAiResult>> GetHistoryAsync(
        string entryId,
        FeedAiTaskType taskType,
        string targetLanguage,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateText(entryId, nameof(entryId), 256);
        ValidateTaskType(taskType);
        ValidateText(targetLanguage, nameof(targetLanguage), 32);
        if (limit is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectColumns}
            WHERE entity_type='feed_entry'
              AND entity_id=$entryId
              AND report_type=$reportType
              AND target_language=$targetLanguage
            ORDER BY julianday(created_at) DESC, id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$entryId", entryId);
        command.Parameters.AddWithValue("$reportType", ToStoredTaskType(taskType));
        command.Parameters.AddWithValue("$targetLanguage", targetLanguage);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<FeedAiResult>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadResult(reader));
        }
        return results;
    }

    private const string SelectColumns = """
        SELECT id, entity_id, content_hash, report_type, target_language, model,
               prompt_version, title, content, request_count, prompt_tokens,
               completion_tokens, token_usage, duration_ms, error_code,
               created_at, updated_at
        FROM ai_reports
        """;

    private static void AddResultParameters(SqliteCommand command, FeedAiResult result)
    {
        FeedAiCacheKey key = result.CacheKey;
        command.Parameters.AddWithValue("$id", result.Id);
        command.Parameters.AddWithValue("$entryId", key.EntryId);
        command.Parameters.AddWithValue("$contentHash", key.ContentHash);
        command.Parameters.AddWithValue("$reportType", ToStoredTaskType(key.TaskType));
        command.Parameters.AddWithValue("$targetLanguage", key.TargetLanguage);
        command.Parameters.AddWithValue("$model", key.Model);
        command.Parameters.AddWithValue("$promptVersion", key.PromptVersion);
        command.Parameters.AddWithValue("$title", result.Title);
        command.Parameters.AddWithValue("$content", result.Content);
        command.Parameters.AddWithValue("$requestCount", result.RequestCount);
        command.Parameters.AddWithValue("$promptTokens", result.PromptTokens);
        command.Parameters.AddWithValue("$completionTokens", result.CompletionTokens);
        command.Parameters.AddWithValue("$totalTokens", result.TotalTokens);
        command.Parameters.AddWithValue("$durationMs", result.DurationMilliseconds);
        command.Parameters.AddWithValue("$errorCode", (object?)result.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$createdAt",
            result.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$updatedAt",
            result.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void AddKeyParameters(SqliteCommand command, FeedAiCacheKey key)
    {
        command.Parameters.AddWithValue("$entryId", key.EntryId);
        command.Parameters.AddWithValue("$contentHash", key.ContentHash);
        command.Parameters.AddWithValue("$reportType", ToStoredTaskType(key.TaskType));
        command.Parameters.AddWithValue("$targetLanguage", key.TargetLanguage);
        command.Parameters.AddWithValue("$model", key.Model);
        command.Parameters.AddWithValue("$promptVersion", key.PromptVersion);
    }

    private static FeedAiResult ReadResult(SqliteDataReader reader)
    {
        FeedAiTaskType taskType = FromStoredTaskType(reader.GetString(3));
        var key = new FeedAiCacheKey(
            reader.GetString(1),
            reader.GetString(2),
            taskType,
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6));
        return new(
            reader.GetString(0),
            key,
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt64(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture));
    }

    private static void ValidateResult(FeedAiResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateText(result.Id, nameof(result.Id), 128);
        ValidateKey(result.CacheKey);
        ValidateText(result.Title, nameof(result.Title), 500, allowEmpty: true);
        ArgumentNullException.ThrowIfNull(result.Content);
        if (result.Content.Length > 2_000_000)
            throw new ArgumentOutOfRangeException(nameof(result));
        if (result.RequestCount < 0
            || result.PromptTokens < 0
            || result.CompletionTokens < 0
            || result.TotalTokens < 0
            || result.TotalTokens < result.PromptTokens + result.CompletionTokens
            || result.DurationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(result));
        }
        if (result.ErrorCode is not null)
            ValidateText(result.ErrorCode, nameof(result.ErrorCode), 128);
        if (result.UpdatedAt < result.CreatedAt)
            throw new ArgumentOutOfRangeException(nameof(result));
    }

    private static void ValidateKey(FeedAiCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateText(key.EntryId, nameof(key.EntryId), 256);
        if (key.ContentHash.Length != 64 || key.ContentHash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("内容哈希必须是 64 位十六进制 SHA-256。", nameof(key));
        ValidateTaskType(key.TaskType);
        ValidateText(key.TargetLanguage, nameof(key.TargetLanguage), 32);
        ValidateText(key.Model, nameof(key.Model), 128);
        ValidateText(key.PromptVersion, nameof(key.PromptVersion), 128);
    }

    private static void ValidateTaskType(FeedAiTaskType taskType)
    {
        if (taskType is not FeedAiTaskType.Summary and not FeedAiTaskType.Translation)
            throw new ArgumentOutOfRangeException(nameof(taskType));
    }

    private static void ValidateText(
        string value,
        string parameterName,
        int maximumLength,
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value))
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static string ToStoredTaskType(FeedAiTaskType taskType) =>
        taskType switch
        {
            FeedAiTaskType.Summary => "entry_summary",
            FeedAiTaskType.Translation => "entry_translation",
            _ => throw new ArgumentOutOfRangeException(nameof(taskType))
        };

    private static FeedAiTaskType FromStoredTaskType(string taskType) =>
        taskType switch
        {
            "entry_summary" => FeedAiTaskType.Summary,
            "entry_translation" => FeedAiTaskType.Translation,
            _ => throw new InvalidDataException($"未知的 Feed AI 任务类型：{taskType}")
        };
}
