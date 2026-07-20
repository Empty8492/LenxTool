using System.Globalization;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class MediaJobRepository(SqliteDatabase database) : IMediaJobRepository
{
    public async Task UpsertAsync(MediaJob job, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO media_jobs(
              id, kind, input_path, output_path, status, progress, engine, model,
              shared_usage_seconds, ai_request_count, error_json, created_at, updated_at)
            VALUES($id,$kind,$input,$output,$status,$progress,$engine,$model,$usage,$requests,$error,$created,$updated)
            ON CONFLICT(id) DO UPDATE SET
              output_path=excluded.output_path, status=excluded.status, progress=excluded.progress,
              engine=excluded.engine, model=excluded.model,
              shared_usage_seconds=excluded.shared_usage_seconds,
              ai_request_count=excluded.ai_request_count, error_json=excluded.error_json,
              updated_at=excluded.updated_at;
            """;
        AddParameters(command, job);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaJob>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,kind,input_path,output_path,status,progress,engine,model,shared_usage_seconds,ai_request_count,error_json,created_at,updated_at FROM media_jobs ORDER BY updated_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaJob>> GetQueuedAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,kind,input_path,output_path,status,progress,engine,model,shared_usage_seconds,ai_request_count,error_json,created_at,updated_at FROM media_jobs WHERE status='Queued' ORDER BY created_at;";
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaJob>> RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE media_jobs SET status='Failed', error_json=$error, updated_at=$updated WHERE status='Running';";
        AppError interrupted = new(
            AppErrorCode.Unknown, "任务被意外中断", "应用上次退出时任务仍在运行。",
            "可从历史记录重新执行该任务。", IsRetryable: true);
        update.Parameters.AddWithValue("$error", JsonSerializer.Serialize(interrupted));
        update.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetRecentAsync(100, cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameters(SqliteCommand command, MediaJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$kind", job.Kind);
        command.Parameters.AddWithValue("$input", job.InputPath);
        command.Parameters.AddWithValue("$output", (object?)job.OutputPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$progress", job.Progress);
        command.Parameters.AddWithValue("$engine", job.Engine.ToString());
        command.Parameters.AddWithValue("$model", (object?)job.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$usage", job.SharedUsageSeconds);
        command.Parameters.AddWithValue("$requests", job.AiRequestCount);
        command.Parameters.AddWithValue("$error", job.Error is null ? DBNull.Value : JsonSerializer.Serialize(job.Error));
        command.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", job.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static async Task<IReadOnlyList<MediaJob>> ReadAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var jobs = new List<MediaJob>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Enum.Parse<MediaJobStatus>(reader.GetString(4)), reader.GetDouble(5),
                Enum.Parse<TranscriptionEngine>(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetDouble(8), reader.GetInt32(9),
                reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<AppError>(reader.GetString(10)),
                DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture)));
        }
        return jobs;
    }
}
