using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class NewsRepository(SqliteDatabase database) : INewsRepository
{
    public async Task UpsertAsync(
        IReadOnlyCollection<NewsArticle> articles,
        CancellationToken cancellationToken)
    {
        if (articles.Count == 0) return;

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (NewsArticle article in articles)
        {
            await UpsertArticleAsync(connection, transaction, article, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NewsArticle>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id, n.published_date, n.source, n.title, n.summary, n.content,
                   n.url, n.content_hash, n.fetched_at, n.rich_content
            FROM content_fts f
            JOIN news_articles n ON f.entity_type = 'news' AND f.entity_id = n.id
            WHERE content_fts MATCH $query
            ORDER BY bm25(content_fts), n.published_date DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", EscapeFtsPrefix(query));
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<NewsArticle>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadArticle(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<ContentSearchResult>> SearchContentAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 200) throw new ArgumentOutOfRangeException(nameof(query));
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.entity_type,
                   f.entity_id,
                   f.title,
                   CASE f.entity_type
                       WHEN 'news' THEN n.summary
                       WHEN 'trend' THEN t.heat
                       WHEN 'report' THEN substr(a.content, 1, 240)
                       ELSE ''
                   END AS summary,
                   CASE f.entity_type
                       WHEN 'news' THEN n.source
                       WHEN 'trend' THEN t.platform
                       WHEN 'report' THEN a.model
                       ELSE ''
                   END AS source,
                   CASE f.entity_type
                       WHEN 'news' THEN n.url
                       WHEN 'trend' THEN t.url
                       ELSE NULL
                   END AS url,
                   CASE f.entity_type
                       WHEN 'news' THEN n.fetched_at
                       WHEN 'trend' THEN t.captured_at
                       WHEN 'report' THEN a.created_at
                       ELSE NULL
                   END AS result_timestamp
            FROM content_fts f
            LEFT JOIN news_articles n
                ON f.entity_type = 'news' AND f.entity_id = n.id
            LEFT JOIN trend_items t
                ON f.entity_type = 'trend' AND f.entity_id = t.id
            LEFT JOIN ai_reports a
                ON f.entity_type = 'report' AND f.entity_id = a.id
            WHERE content_fts MATCH $query
              AND f.entity_type IN ('news', 'trend', 'report')
              AND CASE f.entity_type
                      WHEN 'news' THEN n.fetched_at
                      WHEN 'trend' THEN t.captured_at
                      WHEN 'report' THEN a.created_at
                  END IS NOT NULL
            ORDER BY bm25(content_fts), result_timestamp DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", EscapeFtsPrefix(query));
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<ContentSearchResult>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new(
                reader.GetString(1),
                ParseSearchResultType(reader.GetString(0)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        }

        return results;
    }

    public async Task UpsertReportAsync(
        AiReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
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
        command.Parameters.AddWithValue("$entityId", (object?)report.EntityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reportType", report.ReportType);
        command.Parameters.AddWithValue("$title", report.Title);
        command.Parameters.AddWithValue("$content", report.Content);
        command.Parameters.AddWithValue("$model", report.Model);
        command.Parameters.AddWithValue("$requestCount", report.RequestCount);
        command.Parameters.AddWithValue("$tokenUsage", report.TokenUsage);
        command.Parameters.AddWithValue("$createdAt", report.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = "DELETE FROM content_fts WHERE entity_type='report' AND entity_id=$id;";
        command.Parameters.AddWithValue("$id", report.Id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO content_fts(entity_type, entity_id, title, content)
            VALUES ('report', $id, $title, $content);
            """;
        command.Parameters.AddWithValue("$id", report.Id);
        command.Parameters.AddWithValue("$title", report.Title);
        command.Parameters.AddWithValue("$content", report.Content);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AiReport>> GetLatestReportsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, entity_type, entity_id, report_type, title, content, model,
                   request_count, token_usage, created_at
            FROM ai_reports
            ORDER BY created_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var reports = new List<AiReport>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            reports.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)));
        }

        return reports;
    }

    public async Task<IReadOnlyList<NewsArticle>> GetLatestAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, published_date, source, title, summary, content, url, content_hash, fetched_at, rich_content
            FROM news_articles
            ORDER BY published_date DESC, fetched_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<NewsArticle>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadArticle(reader));
        return results;
    }

    public async Task UpsertTrendsAsync(
        IReadOnlyCollection<TrendItem> trends,
        CancellationToken cancellationToken)
    {
        if (trends.Count == 0) return;
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (TrendItem trend in trends)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO trend_items(id, platform, rank, title, heat, url, content_hash, captured_at)
                VALUES ($id, $platform, $rank, $title, $heat, $url, $hash, $capturedAt)
                ON CONFLICT(content_hash) DO UPDATE SET
                    platform=excluded.platform, rank=excluded.rank, title=excluded.title,
                    heat=excluded.heat, url=excluded.url, captured_at=excluded.captured_at;
                """;
            command.Parameters.AddWithValue("$id", trend.Id);
            command.Parameters.AddWithValue("$platform", trend.Platform);
            command.Parameters.AddWithValue("$rank", trend.Rank);
            command.Parameters.AddWithValue("$title", trend.Title);
            command.Parameters.AddWithValue("$heat", trend.Heat);
            command.Parameters.AddWithValue("$url", trend.Url);
            command.Parameters.AddWithValue("$hash", trend.ContentHash);
            command.Parameters.AddWithValue("$capturedAt", trend.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            command.Parameters.Clear();
            command.CommandText = "SELECT id FROM trend_items WHERE content_hash=$hash;";
            command.Parameters.AddWithValue("$hash", trend.ContentHash);
            string canonicalId = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            command.Parameters.Clear();
            command.CommandText = "DELETE FROM content_fts WHERE entity_type='trend' AND entity_id=$id;";
            command.Parameters.AddWithValue("$id", canonicalId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO content_fts(entity_type, entity_id, title, content) VALUES ('trend', $id, $title, $content);";
            command.Parameters.AddWithValue("$id", canonicalId);
            command.Parameters.AddWithValue("$title", trend.Title);
            command.Parameters.AddWithValue("$content", $"{trend.Platform} {trend.Heat}");
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrendItem>> GetLatestTrendsAsync(
        int limit,
        string? platform,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, platform, rank, title, heat, url, content_hash, captured_at
            FROM trend_items
            WHERE $platform IS NULL OR platform=$platform
            ORDER BY captured_at DESC, rank ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$platform", (object?)platform ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<TrendItem>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)));
        }

        return results;
    }

    private static async Task UpsertArticleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NewsArticle article,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO news_articles(
                id, published_date, source, title, summary, content, url, content_hash, fetched_at, rich_content)
            VALUES ($id, $date, $source, $title, $summary, $content, $url, $hash, $fetchedAt, $richContent)
            ON CONFLICT(content_hash) DO UPDATE SET
                published_date=excluded.published_date,
                source=excluded.source,
                title=excluded.title,
                summary=excluded.summary,
                content=excluded.content,
                url=excluded.url,
                fetched_at=excluded.fetched_at,
                rich_content=excluded.rich_content;
            """;
        command.Parameters.AddWithValue("$id", article.Id);
        command.Parameters.AddWithValue("$date", article.PublishedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$source", article.Source);
        command.Parameters.AddWithValue("$title", article.Title);
        command.Parameters.AddWithValue("$summary", article.Summary);
        command.Parameters.AddWithValue("$content", article.Content);
        command.Parameters.AddWithValue("$url", article.Url);
        command.Parameters.AddWithValue("$hash", article.ContentHash);
        command.Parameters.AddWithValue("$fetchedAt", article.FetchedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$richContent", article.RichContent);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = "SELECT id FROM news_articles WHERE content_hash=$hash;";
        command.Parameters.AddWithValue("$hash", article.ContentHash);
        string canonicalId = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        command.Parameters.Clear();
        command.CommandText = "DELETE FROM content_fts WHERE entity_type='news' AND entity_id=$id;";
        command.Parameters.AddWithValue("$id", canonicalId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO content_fts(entity_type, entity_id, title, content)
            VALUES ('news', $id, $title, $content);
            """;
        command.Parameters.AddWithValue("$id", canonicalId);
        command.Parameters.AddWithValue("$title", article.Title);
        command.Parameters.AddWithValue("$content", $"{article.Summary} {article.Content}");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NewsArticle ReadArticle(SqliteDataReader reader) =>
        new NewsArticle(
            reader.GetString(0),
            DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture))
        {
            RichContent = reader.GetString(9)
        };

    private static string EscapeFtsPrefix(string value)
    {
        string normalized = value.Trim().Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{normalized}\"*";
    }

    private static ContentSearchResultType ParseSearchResultType(string value) => value switch
    {
        "news" => ContentSearchResultType.News,
        "trend" => ContentSearchResultType.Trend,
        "report" => ContentSearchResultType.AiReport,
        _ => throw new InvalidDataException($"不支持的搜索结果类型：{value}")
    };
}
