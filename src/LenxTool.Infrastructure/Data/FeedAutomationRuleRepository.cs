using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedAutomationRuleRepository(SqliteDatabase database)
    : IFeedAutomationRuleRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        CreateJsonOptions();

    public async Task<FeedAutomationRuleSnapshot> GetAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        (long version, DateTimeOffset? generatedAt, DateTimeOffset? lastSyncedAt) =
            await ReadStateAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FeedAutomationRule> rules = await ReadRulesAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        FeedAutomationRule[] normalized;
        try
        {
            normalized = ValidateSnapshot(
                new(version, generatedAt, lastSyncedAt, rules),
                requireSynchronizationTime: false);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidDataException)
        {
            throw new InvalidDataException(
                "Stored automation rule snapshot is invalid.",
                exception);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            version,
            generatedAt,
            lastSyncedAt,
            Array.AsReadOnly(normalized));
    }

    public async Task ReplaceAsync(
        FeedAutomationRuleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        FeedAutomationRule[] normalized = ValidateSnapshot(
            snapshot,
            requireSynchronizationTime: true);
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        (long currentVersion, _, _) = await ReadStateAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (snapshot.RuleSetVersion < currentVersion
            || (snapshot.RuleSetVersion == currentVersion
                && currentVersion != 0))
        {
            throw new InvalidOperationException(
                "The automation rule snapshot version is not newer.");
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM feed_automation_rules;";
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (FeedAutomationRule rule in normalized)
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO feed_automation_rules(
                    id, version, priority, conflict_order, rule_json)
                VALUES($id, $version, $priority, $conflictOrder, $ruleJson);
                """;
            command.Parameters.AddWithValue("$id", rule.Id);
            command.Parameters.AddWithValue("$version", rule.Version);
            command.Parameters.AddWithValue("$priority", rule.Priority);
            command.Parameters.AddWithValue(
                "$conflictOrder",
                rule.ConflictOrder);
            command.Parameters.AddWithValue(
                "$ruleJson",
                JsonSerializer.Serialize(rule, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        command.Parameters.Clear();
        command.CommandText = """
            UPDATE feed_automation_rule_state
            SET rule_set_version=$version,
                generated_at=$generatedAt,
                last_synced_at=$lastSyncedAt
            WHERE singleton_id=1;
            """;
        command.Parameters.AddWithValue(
            "$version",
            snapshot.RuleSetVersion);
        command.Parameters.AddWithValue(
            "$generatedAt",
            snapshot.GeneratedAt is null
                ? DBNull.Value
                : Format(snapshot.GeneratedAt.Value));
        command.Parameters.AddWithValue(
            "$lastSyncedAt",
            Format(snapshot.LastSyncedAt!.Value));
        int changed = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidDataException(
                "The automation rule cache state is missing.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> MarkSynchronizedAsync(
        long expectedRuleSetVersion,
        DateTimeOffset synchronizedAt,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRuleSetVersion);
        ValidateUtcTimestamp(synchronizedAt, nameof(synchronizedAt));
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE feed_automation_rule_state
            SET last_synced_at=$lastSyncedAt
            WHERE singleton_id=1 AND rule_set_version=$expectedVersion;
            """;
        command.Parameters.AddWithValue(
            "$lastSyncedAt",
            Format(synchronizedAt));
        command.Parameters.AddWithValue(
            "$expectedVersion",
            expectedRuleSetVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    private static async Task<(
        long Version,
        DateTimeOffset? GeneratedAt,
        DateTimeOffset? LastSyncedAt)> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rule_set_version, generated_at, last_synced_at
            FROM feed_automation_rule_state
            WHERE singleton_id=1;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(
                "The automation rule cache state is missing.");
        }
        return (
            reader.GetInt64(0),
            ReadNullableTimestamp(reader, 1),
            ReadNullableTimestamp(reader, 2));
    }

    private static async Task<IReadOnlyList<FeedAutomationRule>>
        ReadRulesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, version, priority, conflict_order, rule_json
            FROM feed_automation_rules
            ORDER BY priority DESC, conflict_order, id;
            """;
        var rules = new List<FeedAutomationRule>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            FeedAutomationRule rule;
            try
            {
                rule = JsonSerializer.Deserialize<FeedAutomationRule>(
                    reader.GetString(4),
                    JsonOptions)
                    ?? throw new InvalidDataException(
                        "Stored automation rule data is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Stored automation rule data is invalid.",
                    exception);
            }
            if (!string.Equals(
                    rule.Id,
                    reader.GetString(0),
                    StringComparison.Ordinal)
                || rule.Version != reader.GetInt32(1)
                || rule.Priority != reader.GetInt32(2)
                || rule.ConflictOrder != reader.GetInt32(3))
            {
                throw new InvalidDataException(
                    "Stored automation rule metadata is inconsistent.");
            }
            rules.Add(rule);
        }
        return Array.AsReadOnly(rules.ToArray());
    }

    private static FeedAutomationRule[] ValidateSnapshot(
        FeedAutomationRuleSnapshot snapshot,
        bool requireSynchronizationTime)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Rules);
        if (snapshot.RuleSetVersion < 0
            || snapshot.Rules.Count
                > FeedAutomationRuleInterpreter.MaximumRuleCount
            || (snapshot.RuleSetVersion == 0
                && snapshot.Rules.Count != 0)
            || snapshot.GeneratedAt is null
                != (snapshot.RuleSetVersion == 0
                    && snapshot.Rules.Count == 0)
            || (requireSynchronizationTime
                && snapshot.LastSyncedAt is null))
        {
            throw new ArgumentException(
                "The automation rule snapshot metadata is invalid.",
                nameof(snapshot));
        }
        if (snapshot.GeneratedAt is not null)
        {
            ValidateUtcTimestamp(
                snapshot.GeneratedAt.Value,
                nameof(snapshot));
        }
        if (snapshot.LastSyncedAt is not null)
        {
            ValidateUtcTimestamp(
                snapshot.LastSyncedAt.Value,
                nameof(snapshot));
        }

        FeedAutomationRule[] normalized = snapshot.Rules
            .Select(FeedAutomationRuleValidator.ValidateAndNormalize)
            .ToArray();
        if (normalized.Any(rule => !rule.IsEnabled))
        {
            throw new ArgumentException(
                "The local automation cache only accepts active rules.",
                nameof(snapshot));
        }
        try
        {
            _ = FeedAutomationRuleInterpreter.Compile(normalized);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "The automation rule snapshot is invalid.",
                nameof(snapshot),
                exception);
        }
        return normalized
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.ConflictOrder)
            .ThenBy(rule => rule.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateUtcTimestamp(
        DateTimeOffset value,
        string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Automation rule cache timestamps must be UTC.",
                parameterName);
        }
    }

    private static DateTimeOffset? ReadNullableTimestamp(
        SqliteDataReader reader,
        int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }
        if (!DateTimeOffset.TryParse(
                reader.GetString(ordinal),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset value))
        {
            throw new InvalidDataException(
                "Stored automation rule timestamp is invalid.");
        }
        return value;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "O",
            CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(
            JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
