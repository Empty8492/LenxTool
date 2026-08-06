using System.Globalization;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

internal static class AiReportSql
{
    public static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AiReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(report);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ai_reports(
                id, entity_type, entity_id, report_type, title, content, model,
                request_count, token_usage, created_at)
            VALUES (
                $id, $entityType, $entityId, $reportType, $title, $content, $model,
                $requestCount, $tokenUsage, $createdAt)
            ON CONFLICT(id) DO UPDATE SET
                entity_type=excluded.entity_type,
                entity_id=excluded.entity_id,
                report_type=excluded.report_type,
                title=excluded.title,
                content=excluded.content,
                model=excluded.model,
                request_count=excluded.request_count,
                token_usage=excluded.token_usage,
                created_at=excluded.created_at;
            """;
        command.Parameters.AddWithValue("$id", report.Id);
        command.Parameters.AddWithValue("$entityType", report.EntityType);
        command.Parameters.AddWithValue(
            "$entityId",
            (object?)report.EntityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reportType", report.ReportType);
        command.Parameters.AddWithValue("$title", report.Title);
        command.Parameters.AddWithValue("$content", report.Content);
        command.Parameters.AddWithValue("$model", report.Model);
        command.Parameters.AddWithValue(
            "$requestCount",
            report.RequestCount);
        command.Parameters.AddWithValue("$tokenUsage", report.TokenUsage);
        command.Parameters.AddWithValue(
            "$createdAt",
            report.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText =
            "DELETE FROM content_fts WHERE entity_type='report' AND entity_id=$id;";
        command.Parameters.AddWithValue("$id", report.Id);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO content_fts(entity_type, entity_id, title, content)
            VALUES ('report', $id, $title, $content);
            """;
        command.Parameters.AddWithValue("$id", report.Id);
        command.Parameters.AddWithValue("$title", report.Title);
        command.Parameters.AddWithValue("$content", report.Content);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
