using System.Globalization;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

/// <summary>
/// 用一次有界查询批量投影发现卡片所需字段，不读取摘要、正文或附件。
/// </summary>
public sealed class FeedDiscoveryPreviewRepository(SqliteDatabase database)
    : IFeedDiscoveryPreviewRepository
{
    private const int MaximumFeedCount = 100;

    public async Task<IReadOnlyList<FeedDiscoveryPreviewItem>> GetRecentAsync(
        IReadOnlyCollection<string> feedIds,
        int maximumPerFeed,
        string localProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feedIds);
        string[] distinctFeedIds = feedIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctFeedIds.Length == 0) return [];
        if (distinctFeedIds.Length > MaximumFeedCount
            || distinctFeedIds.Any(id => !Guid.TryParseExact(id, "D", out _))
            || maximumPerFeed is < 1 or > 4
            || string.IsNullOrWhiteSpace(localProfile)
            || localProfile.Length > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(feedIds),
                "发现预览查询参数超过安全边界。");
        }

        await using SqliteConnection connection = await database
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        var placeholders = new StringBuilder();
        for (int index = 0; index < distinctFeedIds.Length; index++)
        {
            if (index > 0) placeholders.Append(',');
            string parameterName = $"$feed{index}";
            placeholders.Append(parameterName);
            command.Parameters.AddWithValue(
                parameterName,
                distinctFeedIds[index]);
        }

        // ROW_NUMBER 先在每个 Feed 内稳定排序，再统一限制为最多四条。
        command.CommandText = $$"""
            WITH ranked AS (
                SELECT
                    e.feed_id,
                    e.title,
                    COALESCE(e.published_at, e.updated_at, e.fetched_at) AS preview_at,
                    ROW_NUMBER() OVER (
                        PARTITION BY e.feed_id
                        ORDER BY
                            julianday(COALESCE(e.published_at, e.updated_at, e.fetched_at)) DESC,
                            e.id
                    ) AS preview_rank
                FROM feed_entries e
                INNER JOIN feed_catalog f ON f.id = e.feed_id
                LEFT JOIN feed_categories c ON c.id = f.category_id
                WHERE e.feed_id IN ({{placeholders}})
                  AND f.is_enabled = 1
                  AND (f.category_id IS NULL OR c.is_enabled = 1)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM user_entry_states hidden
                      WHERE hidden.entry_id = e.id
                        AND hidden.local_profile = $localProfile
                        AND hidden.is_hidden = 1
                  )
            )
            SELECT feed_id, title, preview_at
            FROM ranked
            WHERE preview_rank <= $maximumPerFeed
            ORDER BY feed_id, preview_rank;
            """;
        command.Parameters.AddWithValue("$localProfile", localProfile);
        command.Parameters.AddWithValue(
            "$maximumPerFeed",
            maximumPerFeed);

        var result = new List<FeedDiscoveryPreviewItem>(
            distinctFeedIds.Length * maximumPerFeed);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(
                    reader.GetString(2),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)));
        }
        return result;
    }
}
