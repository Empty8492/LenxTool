using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedMediaDeliveryRepository(SqliteDatabase database)
    : IFeedMediaDeliveryRepository
{
    public async Task<FeedMediaDeliveryRegistration> CreateOrGetQueuedAsync(
        FeedMediaDelivery delivery,
        MediaJob queuedJob,
        CancellationToken cancellationToken)
    {
        Validate(delivery, queuedJob);

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        FeedMediaDelivery? existing = await FindDeliveryAsync(
            connection,
            transaction,
            delivery.EntryId,
            delivery.SourceUrl,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            MediaJob existingJob = await FindRequiredJobAsync(
                connection,
                transaction,
                existing.MediaJobId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(existing, existingJob, Created: false);
        }

        await MediaJobRepository.InsertAsync(
            connection,
            transaction,
            queuedJob,
            cancellationToken).ConfigureAwait(false);
        await InsertDeliveryAsync(
            connection,
            transaction,
            delivery,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(delivery, queuedJob, Created: true);
    }

    public async Task<FeedMediaDeliveryRegistration?> GetAsync(
        string entryId,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        ValidateKey(entryId, sourceUrl);

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        FeedMediaDelivery? delivery = await FindDeliveryAsync(
            connection,
            transaction,
            entryId,
            sourceUrl,
            cancellationToken).ConfigureAwait(false);
        if (delivery is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        MediaJob job = await FindRequiredJobAsync(
            connection,
            transaction,
            delivery.MediaJobId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(delivery, job, Created: false);
    }

    private static async Task InsertDeliveryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeedMediaDelivery delivery,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO feed_media_deliveries(
                entry_id, feed_id, entry_title, source_url, source_title,
                media_type, source_length, media_job_id, created_at)
            VALUES(
                $entryId, $feedId, $entryTitle, $sourceUrl, $sourceTitle,
                $mediaType, $sourceLength, $mediaJobId, $createdAt);
            """;
        command.Parameters.AddWithValue("$entryId", delivery.EntryId);
        command.Parameters.AddWithValue("$feedId", delivery.FeedId);
        command.Parameters.AddWithValue("$entryTitle", delivery.EntryTitle);
        command.Parameters.AddWithValue("$sourceUrl", delivery.SourceUrl);
        command.Parameters.AddWithValue(
            "$sourceTitle",
            (object?)delivery.SourceTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$mediaType", delivery.MediaType);
        command.Parameters.AddWithValue(
            "$sourceLength",
            delivery.SourceLength is null ? DBNull.Value : delivery.SourceLength.Value);
        command.Parameters.AddWithValue("$mediaJobId", delivery.MediaJobId);
        command.Parameters.AddWithValue(
            "$createdAt",
            delivery.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FeedMediaDelivery?> FindDeliveryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entryId,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT entry_id, feed_id, entry_title, source_url, source_title,
                   media_type, source_length, media_job_id, created_at
            FROM feed_media_deliveries
            WHERE entry_id=$entryId AND source_url=$sourceUrl;
            """;
        command.Parameters.AddWithValue("$entryId", entryId);
        command.Parameters.AddWithValue("$sourceUrl", sourceUrl);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture));
    }

    private static async Task<MediaJob> FindRequiredJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string mediaJobId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {MediaJobRepository.SelectColumns} FROM media_jobs WHERE id=$id;";
        command.Parameters.AddWithValue("$id", mediaJobId);
        IReadOnlyList<MediaJob> jobs =
            await MediaJobRepository.ReadAsync(command, cancellationToken).ConfigureAwait(false);
        return jobs.Count == 1
            ? jobs[0]
            : throw new InvalidDataException(
                $"Feed 媒体投递引用的任务不存在：{mediaJobId}");
    }

    private static void Validate(FeedMediaDelivery delivery, MediaJob queuedJob)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(queuedJob);
        ValidateKey(delivery.EntryId, delivery.SourceUrl);
        ValidateText(delivery.FeedId, nameof(delivery.FeedId), 512);
        ValidateText(delivery.EntryTitle, nameof(delivery.EntryTitle), 1_024);
        ValidateOptionalText(delivery.SourceTitle, nameof(delivery.SourceTitle), 1_024);
        ValidateText(delivery.MediaType, nameof(delivery.MediaType), 255);
        ValidateText(delivery.MediaJobId, nameof(delivery.MediaJobId), 255);
        if (delivery.SourceLength is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delivery),
                "Feed 媒体附件长度不能为负数。");
        }
        if (!string.Equals(delivery.MediaJobId, queuedJob.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Feed 媒体投递与媒体任务 ID 必须一致。",
                nameof(queuedJob));
        }
        if (queuedJob.Status != MediaJobStatus.Queued)
        {
            throw new ArgumentException("只能登记待处理的媒体任务。", nameof(queuedJob));
        }
    }

    private static void ValidateKey(string entryId, string sourceUrl)
    {
        ValidateText(entryId, nameof(entryId), 512);
        ValidateText(sourceUrl, nameof(sourceUrl), 4_096);
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("Feed 媒体来源必须是绝对 HTTP(S) URL。", nameof(sourceUrl));
        }
    }

    private static void ValidateText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"文本长度不能超过 {maximumLength} 个字符。");
        }
    }

    private static void ValidateOptionalText(
        string? value,
        string parameterName,
        int maximumLength)
    {
        if (value?.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"文本长度不能超过 {maximumLength} 个字符。");
        }
    }
}
