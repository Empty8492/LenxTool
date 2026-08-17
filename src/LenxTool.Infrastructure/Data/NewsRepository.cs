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
        ContentSearchPage page = await SearchContentAsync(
                new(query, Limit: limit),
                cancellationToken)
            .ConfigureAwait(false);
        return page.Items;
    }

    public async Task<ContentSearchPage> SearchContentAsync(
        ContentSearchQuery query,
        CancellationToken cancellationToken)
    {
        ContentSearchQuery normalized = ValidateSearchQuery(query);

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH matched AS (
                SELECT
                    f.entity_type AS search_type,
                    CASE f.entity_type
                        WHEN 'favorite' THEN favorite.entity_id
                        ELSE f.entity_id
                    END AS result_entity_id,
                    CASE f.entity_type
                        WHEN 'news' THEN n.title
                        WHEN 'trend' THEN t.title
                        WHEN 'report' THEN a.title
                        WHEN 'feed_entry' THEN e.title
                        WHEN 'subtitle' THEN m.input_path
                        WHEN 'tag' THEN tag.name
                        WHEN 'favorite' THEN COALESCE(
                            favorite_feed.title,
                            favorite_news.title,
                            favorite_trend.title,
                            favorite_report.title,
                            favorite_media.input_path,
                            '收藏内容')
                    END AS title,
                    CASE f.entity_type
                        WHEN 'news' THEN n.summary
                        WHEN 'trend' THEN t.heat
                        WHEN 'report' THEN substr(a.content, 1, 240)
                        WHEN 'feed_entry' THEN e.summary
                        WHEN 'subtitle' THEN substr(f.content, 1, 240)
                        WHEN 'tag' THEN tag.color
                        WHEN 'favorite' THEN favorite.note
                    END AS summary,
                    CASE f.entity_type
                        WHEN 'news' THEN n.source
                        WHEN 'trend' THEN t.platform
                        WHEN 'report' THEN a.model
                        WHEN 'feed_entry' THEN COALESCE(
                            fc.display_name,
                            'RSS/Atom')
                        WHEN 'subtitle' THEN COALESCE(m.model, m.engine)
                        WHEN 'tag' THEN '本地标签'
                        WHEN 'favorite' THEN '收藏 · ' || favorite.entity_type
                    END AS source,
                    CASE f.entity_type
                        WHEN 'news' THEN n.url
                        WHEN 'trend' THEN t.url
                        WHEN 'feed_entry' THEN e.normalized_url
                        WHEN 'favorite' THEN COALESCE(
                            favorite_feed.normalized_url,
                            favorite_news.url,
                            favorite_trend.url)
                        ELSE NULL
                    END AS url,
                    CASE f.entity_type
                        WHEN 'news' THEN
                            n.published_date || 'T00:00:00.0000000+00:00'
                        WHEN 'trend' THEN t.captured_at
                        WHEN 'report' THEN a.created_at
                        WHEN 'feed_entry' THEN COALESCE(
                            e.published_at,
                            e.updated_at,
                            e.fetched_at)
                        WHEN 'subtitle' THEN m.updated_at
                        WHEN 'tag' THEN tag.created_at
                        WHEN 'favorite' THEN favorite.created_at
                    END AS result_timestamp,
                    CASE f.entity_type
                        WHEN 'favorite' THEN favorite.entity_type
                        WHEN 'subtitle' THEN 'media_job'
                        ELSE f.entity_type
                    END AS target_type,
                    CASE f.entity_type
                        WHEN 'favorite' THEN favorite.entity_id
                        ELSE f.entity_id
                    END AS target_id,
                    CASE f.entity_type
                        WHEN 'feed_entry' THEN e.feed_id
                        WHEN 'favorite' THEN favorite_feed.feed_id
                        ELSE NULL
                    END AS feed_id,
                    CASE f.entity_type
                        WHEN 'feed_entry' THEN fc.category_id
                        WHEN 'favorite' THEN favorite_fc.category_id
                        ELSE NULL
                    END AS category_id,
                    bm25(content_fts) AS search_rank,
                    f.entity_id AS search_document_id
                FROM content_fts f
                LEFT JOIN news_articles n
                    ON f.entity_type='news' AND f.entity_id=n.id
                LEFT JOIN trend_items t
                    ON f.entity_type='trend' AND f.entity_id=t.id
                LEFT JOIN ai_reports a
                    ON f.entity_type='report' AND f.entity_id=a.id
                LEFT JOIN feed_entries e
                    ON f.entity_type='feed_entry' AND f.entity_id=e.id
                LEFT JOIN feed_catalog fc ON fc.id=e.feed_id
                LEFT JOIN media_jobs m
                    ON f.entity_type='subtitle' AND f.entity_id=m.id
                LEFT JOIN tags tag
                    ON f.entity_type='tag' AND f.entity_id=tag.id
                LEFT JOIN favorites favorite
                    ON f.entity_type='favorite' AND f.entity_id=favorite.id
                LEFT JOIN feed_entries favorite_feed
                    ON favorite.entity_type='feed_entry'
                    AND favorite.entity_id=favorite_feed.id
                LEFT JOIN feed_catalog favorite_fc
                    ON favorite_fc.id=favorite_feed.feed_id
                LEFT JOIN news_articles favorite_news
                    ON favorite.entity_type='news'
                    AND favorite.entity_id=favorite_news.id
                LEFT JOIN trend_items favorite_trend
                    ON favorite.entity_type='trend'
                    AND favorite.entity_id=favorite_trend.id
                LEFT JOIN ai_reports favorite_report
                    ON favorite.entity_type='report'
                    AND favorite.entity_id=favorite_report.id
                LEFT JOIN media_jobs favorite_media
                    ON favorite.entity_type='media_job'
                    AND favorite.entity_id=favorite_media.id
                WHERE content_fts MATCH $query
                  AND f.entity_type IN (
                      'news',
                      'trend',
                      'report',
                      'feed_entry',
                      'subtitle',
                      'tag',
                      'favorite')
            )
            SELECT
                search_type,
                result_entity_id,
                title,
                COALESCE(summary, ''),
                COALESCE(source, ''),
                url,
                result_timestamp
            FROM matched
            WHERE result_entity_id IS NOT NULL
              AND title IS NOT NULL
              AND result_timestamp IS NOT NULL
              AND ($type IS NULL OR search_type=$type)
              AND (
                  $publishedFrom IS NULL
                  OR julianday(result_timestamp) >= julianday($publishedFrom))
              AND (
                  $publishedBefore IS NULL
                  OR julianday(result_timestamp) < julianday($publishedBefore))
              AND (
                  $feedId IS NULL
                  OR (search_type='feed_entry' AND feed_id=$feedId))
              AND (
                  $categoryId IS NULL
                  OR (search_type='feed_entry' AND category_id=$categoryId))
              AND (
                  $tagId IS NULL
                  OR EXISTS(
                      SELECT 1
                      FROM entity_tags entity_tag
                      WHERE entity_tag.entity_type=matched.target_type
                        AND entity_tag.entity_id=matched.target_id
                        AND entity_tag.tag_id=$tagId))
              AND (
                  $favoritesOnly=0
                  OR search_type='favorite'
                  OR EXISTS(
                      SELECT 1
                      FROM favorites private_favorite
                      WHERE private_favorite.entity_type=matched.target_type
                        AND private_favorite.entity_id=matched.target_id))
            ORDER BY
                search_rank,
                result_timestamp DESC,
                search_type,
                search_document_id
            LIMIT $fetchLimit
            OFFSET $offset;
            """;
        command.Parameters.AddWithValue(
            "$query",
            EscapeFtsPrefix(normalized.Text));
        command.Parameters.AddWithValue(
            "$type",
            normalized.Type is null
                ? DBNull.Value
                : ToSearchEntityType(normalized.Type.Value));
        command.Parameters.AddWithValue(
            "$publishedFrom",
            FormatNullableTimestamp(normalized.PublishedFrom));
        command.Parameters.AddWithValue(
            "$publishedBefore",
            FormatNullableTimestamp(normalized.PublishedBefore));
        command.Parameters.AddWithValue(
            "$feedId",
            (object?)normalized.FeedId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$categoryId",
            (object?)normalized.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$tagId",
            (object?)normalized.TagId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$favoritesOnly",
            normalized.FavoritesOnly ? 1 : 0);
        command.Parameters.AddWithValue(
            "$fetchLimit",
            checked(normalized.Limit + 1));
        command.Parameters.AddWithValue("$offset", normalized.Offset);

        var results = new List<ContentSearchResult>(normalized.Limit + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ContentSearchResultType type =
                ParseSearchResultType(reader.GetString(0));
            string title = reader.GetString(2);
            if (type == ContentSearchResultType.Subtitle)
            {
                title = Path.GetFileName(title);
            }
            results.Add(new(
                reader.GetString(1),
                type,
                title,
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        }

        bool hasMore = results.Count > normalized.Limit;
        if (hasMore)
        {
            results.RemoveAt(results.Count - 1);
        }
        return new(results.AsReadOnly(), hasMore);
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
        await AiReportSql.UpsertAsync(
            connection,
            transaction,
            report,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiReport?> GetReportByIdAsync(
        string reportId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reportId)
            || reportId.Length > 128
            || reportId.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(reportId));
        }
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, entity_type, entity_id, report_type, title, content, model,
                   request_count, token_usage, created_at
            FROM ai_reports
            WHERE id=$id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", reportId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadReport(reader)
            : null;
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
            reports.Add(ReadReport(reader));
        }

        return reports;
    }

    private static AiReport ReadReport(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            DateTimeOffset.Parse(
                reader.GetString(9),
                CultureInfo.InvariantCulture));

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
        foreach (string platform in trends.Select(trend => trend.Platform).Distinct(StringComparer.Ordinal))
        {
            await using SqliteCommand deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = """
                DELETE FROM content_fts
                WHERE entity_type='trend'
                  AND entity_id IN (SELECT id FROM trend_items WHERE platform=$platform);
                DELETE FROM trend_items WHERE platform=$platform;
                """;
            deleteCommand.Parameters.AddWithValue("$platform", platform);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

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
        "feed_entry" => ContentSearchResultType.FeedEntry,
        "subtitle" => ContentSearchResultType.Subtitle,
        "tag" => ContentSearchResultType.Tag,
        "favorite" => ContentSearchResultType.Favorite,
        _ => throw new InvalidDataException($"不支持的搜索结果类型：{value}")
    };

    private static string ToSearchEntityType(
        ContentSearchResultType value) => value switch
        {
            ContentSearchResultType.News => "news",
            ContentSearchResultType.Trend => "trend",
            ContentSearchResultType.AiReport => "report",
            ContentSearchResultType.FeedEntry => "feed_entry",
            ContentSearchResultType.Subtitle => "subtitle",
            ContentSearchResultType.Tag => "tag",
            ContentSearchResultType.Favorite => "favorite",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static ContentSearchQuery ValidateSearchQuery(
        ContentSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Text);
        string text = query.Text.Trim();
        if (text.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }
        if (query.Offset is < 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }
        if (query.Limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }
        if (query.PublishedFrom is { } from
            && query.PublishedBefore is { } before
            && from >= before)
        {
            throw new ArgumentException(
                "搜索结束日期必须晚于开始日期。",
                nameof(query));
        }
        if (query.Type is not null
            && query.Type != ContentSearchResultType.FeedEntry
            && (query.FeedId is not null || query.CategoryId is not null))
        {
            throw new ArgumentException(
                "Feed 或分类筛选只能与订阅条目类型组合。",
                nameof(query));
        }
        return query with
        {
            Text = text,
            FeedId = NormalizeSearchFilter(query.FeedId, nameof(query.FeedId)),
            CategoryId = NormalizeSearchFilter(
                query.CategoryId,
                nameof(query.CategoryId)),
            TagId = NormalizeSearchFilter(query.TagId, nameof(query.TagId))
        };
    }

    private static string? NormalizeSearchFilter(
        string? value,
        string parameterName)
    {
        if (value is null)
        {
            return null;
        }
        string normalized = value.Trim();
        if (normalized.Length is 0 or > 128
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return normalized;
    }

    private static object FormatNullableTimestamp(DateTimeOffset? value) =>
        value is null
            ? DBNull.Value
            : value.Value.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture);
}
