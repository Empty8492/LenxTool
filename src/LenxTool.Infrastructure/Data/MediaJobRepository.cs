using System.Globalization;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class MediaJobRepository(SqliteDatabase database) : IMediaJobRepository, ISubtitleRepository
{
    public async Task UpsertAsync(MediaJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertAsync(connection, transaction, job, cancellationToken).ConfigureAwait(false);
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

    public async Task ReplaceAsync(
        string mediaJobId,
        IReadOnlyList<SubtitleSegment> segments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaJobId);
        ArgumentNullException.ThrowIfNull(segments);

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(connection, transaction, mediaJobId, segments, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateMediaJobWithSegmentsAsync(
        MediaJob job,
        IReadOnlyList<SubtitleSegment> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(segments);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertAsync(connection, transaction, job, cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(connection, transaction, job.Id, segments, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string mediaJobId,
        IReadOnlyList<SubtitleSegment> segments,
        CancellationToken cancellationToken)
    {
        var sequences = new HashSet<int>();
        var timelines = new HashSet<(long StartMilliseconds, long EndMilliseconds)>();
        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM subtitle_segments WHERE media_job_id=$mediaJobId;";
            delete.Parameters.AddWithValue("$mediaJobId", mediaJobId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (int index = 0; index < segments.Count; index++)
        {
            SubtitleSegment segment = segments[index];
            if (segment.Sequence is < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segments), "字幕序号不能为负数。");
            }
            int sequence = segment.Sequence ?? checked(index + 1);
            long startMilliseconds = ToMilliseconds(segment.Start, nameof(segment.Start));
            long endMilliseconds = ToMilliseconds(segment.End, nameof(segment.End));
            if (endMilliseconds <= startMilliseconds)
            {
                throw new ArgumentException("字幕结束时间必须晚于开始时间。", nameof(segments));
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(segment.Text);
            if (!sequences.Add(sequence))
            {
                throw new ArgumentException("同一媒体任务中的字幕序号必须唯一。", nameof(segments));
            }
            if (!timelines.Add((startMilliseconds, endMilliseconds)))
            {
                throw new ArgumentException("同一媒体任务中的字幕时间轴必须唯一。", nameof(segments));
            }

            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO subtitle_segments(
                    media_job_id, sequence, start_ms, end_ms, text, translated_text,
                    avg_log_probability, no_speech_probability)
                VALUES(
                    $mediaJobId, $sequence, $startMs, $endMs, $text, $translatedText,
                    $averageLogProbability, $noSpeechProbability);
                """;
            insert.Parameters.AddWithValue("$mediaJobId", mediaJobId);
            insert.Parameters.AddWithValue("$sequence", sequence);
            insert.Parameters.AddWithValue("$startMs", startMilliseconds);
            insert.Parameters.AddWithValue("$endMs", endMilliseconds);
            insert.Parameters.AddWithValue("$text", segment.Text);
            insert.Parameters.AddWithValue(
                "$translatedText",
                (object?)segment.TranslatedText ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "$averageLogProbability",
                (object?)segment.AverageLogProbability ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "$noSpeechProbability",
                (object?)segment.NoSpeechProbability ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SubtitleSegment>> GetByMediaJobIdAsync(
        string mediaJobId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaJobId);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, start_ms, end_ms, text, translated_text,
                   avg_log_probability, no_speech_probability
            FROM subtitle_segments
            WHERE media_job_id=$mediaJobId
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$mediaJobId", mediaJobId);

        var segments = new List<SubtitleSegment>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            segments.Add(new(
                TimeSpan.FromMilliseconds(reader.GetInt64(1)),
                TimeSpan.FromMilliseconds(reader.GetInt64(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6))
            {
                Sequence = reader.GetInt32(0)
            });
        }
        return segments;
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MediaJob job,
        CancellationToken cancellationToken)
    {
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

    private static long ToMilliseconds(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(parameterName);
        return value.Ticks / TimeSpan.TicksPerMillisecond;
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
