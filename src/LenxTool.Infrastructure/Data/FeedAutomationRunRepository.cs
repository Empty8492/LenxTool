using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedAutomationRunRepository(SqliteDatabase database)
    : IFeedAutomationRunRepository
{
    private const int MaximumActionCount =
        FeedAutomationRuleInterpreter.MaximumRuleCount
        * FeedAutomationRuleValidator.MaximumActionCount;

    public async Task<FeedAutomationStageResult> StageAsync(
        FeedAutomationPlan plan,
        DateTimeOffset stagedAt,
        CancellationToken cancellationToken)
    {
        ValidatedPlan validated = ValidatePlan(plan);
        string timestamp = Format(stagedAt);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        int ruleRunsCreated = 0;
        int actionRunsCreated = 0;
        for (int planOrder = 0; planOrder < validated.Evaluations.Count; planOrder++)
        {
            FeedAutomationRuleEvaluation evaluation = validated.Evaluations[planOrder];
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO feed_automation_runs(
                    entry_id, rule_id, rule_version, evaluation_outcome,
                    plan_order, evaluated_at)
                VALUES(
                    $entryId, $ruleId, $ruleVersion, $evaluationOutcome,
                    $planOrder, $evaluatedAt)
                ON CONFLICT(entry_id, rule_id, rule_version) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$entryId", validated.EntryId);
            command.Parameters.AddWithValue("$ruleId", evaluation.RuleId);
            command.Parameters.AddWithValue("$ruleVersion", evaluation.RuleVersion);
            command.Parameters.AddWithValue(
                "$evaluationOutcome",
                ToDatabase(evaluation.Outcome));
            command.Parameters.AddWithValue("$planOrder", planOrder);
            command.Parameters.AddWithValue("$evaluatedAt", timestamp);
            int inserted = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            ruleRunsCreated += inserted;
            if (inserted == 0
                || !validated.ActionsByRule.TryGetValue(
                    new(evaluation.RuleId, evaluation.RuleVersion),
                    out IReadOnlyList<FeedAutomationActionDecision>? actions))
            {
                continue;
            }

            foreach (FeedAutomationActionDecision action in actions)
            {
                string idempotencyKey = CreateIdempotencyKey(
                    validated.EntryId,
                    action.RuleId,
                    action.RuleVersion,
                    action.ActionOrder);
                bool planned =
                    action.Disposition == FeedAutomationActionDisposition.Planned;
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO feed_automation_action_runs(
                        idempotency_key, entry_id, rule_id, rule_version,
                        rule_priority, rule_conflict_order, action_type, action_order,
                        action_value, disposition, suppression_reason,
                        winning_rule_id, winning_rule_version, winning_action_order,
                        status, attempt_count, next_attempt_at,
                        lease_token, lease_expires_at, last_error_code,
                        created_at, updated_at)
                    VALUES(
                        $idempotencyKey, $entryId, $ruleId, $ruleVersion,
                        $rulePriority, $ruleConflictOrder, $actionType, $actionOrder,
                        $actionValue, $disposition, $suppressionReason,
                        $winningRuleId, $winningRuleVersion, $winningActionOrder,
                        $status, 0, $nextAttemptAt,
                        NULL, NULL, NULL,
                        $createdAt, $updatedAt)
                    ON CONFLICT(entry_id, rule_id, rule_version, action_order) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
                command.Parameters.AddWithValue("$entryId", validated.EntryId);
                command.Parameters.AddWithValue("$ruleId", action.RuleId);
                command.Parameters.AddWithValue("$ruleVersion", action.RuleVersion);
                command.Parameters.AddWithValue("$rulePriority", action.RulePriority);
                command.Parameters.AddWithValue(
                    "$ruleConflictOrder",
                    action.RuleConflictOrder);
                command.Parameters.AddWithValue("$actionType", ToDatabase(action.Type));
                command.Parameters.AddWithValue("$actionOrder", action.ActionOrder);
                command.Parameters.AddWithValue(
                    "$actionValue",
                    (object?)action.Value ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$disposition",
                    planned ? "PLANNED" : "SUPPRESSED");
                command.Parameters.AddWithValue(
                    "$suppressionReason",
                    ToDatabase(action.SuppressionReason));
                command.Parameters.AddWithValue(
                    "$winningRuleId",
                    (object?)action.WinningRuleId ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$winningRuleVersion",
                    (object?)action.WinningRuleVersion ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$winningActionOrder",
                    (object?)action.WinningActionOrder ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$status",
                    planned ? "PENDING" : "SUPPRESSED");
                command.Parameters.AddWithValue(
                    "$nextAttemptAt",
                    planned ? timestamp : DBNull.Value);
                command.Parameters.AddWithValue("$createdAt", timestamp);
                command.Parameters.AddWithValue("$updatedAt", timestamp);
                actionRunsCreated += await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(ruleRunsCreated, actionRunsCreated);
    }

    public async Task<FeedAutomationRunSnapshot> GetAsync(
        string entryId,
        CancellationToken cancellationToken)
    {
        string validatedEntryId = ValidateEntryId(entryId);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT entry_id, rule_id, rule_version, evaluation_outcome,
                   plan_order, evaluated_at
            FROM feed_automation_runs
            WHERE entry_id=$entryId
            ORDER BY evaluated_at, plan_order, rule_id, rule_version;
            """;
        command.Parameters.AddWithValue("$entryId", validatedEntryId);
        var ruleRuns = new List<FeedAutomationRuleRun>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ruleRuns.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    ParseEvaluationOutcome(reader.GetString(3)),
                    reader.GetInt32(4),
                    ParseTimestamp(reader.GetString(5))));
            }
        }

        command.Parameters.Clear();
        command.CommandText = """
            SELECT idempotency_key, entry_id, rule_id, rule_version,
                   rule_priority, rule_conflict_order, action_type, action_order,
                   action_value, disposition, suppression_reason,
                   winning_rule_id, winning_rule_version, winning_action_order,
                   status, attempt_count, next_attempt_at, created_at, updated_at
            FROM feed_automation_action_runs
            WHERE entry_id=$entryId
            ORDER BY created_at, rule_priority DESC, rule_conflict_order,
                     rule_id, action_order;
            """;
        command.Parameters.AddWithValue("$entryId", validatedEntryId);
        var actionRuns = new List<FeedAutomationActionRun>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                actionRuns.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    ParseActionType(reader.GetString(6)),
                    reader.GetInt32(7),
                    GetNullableString(reader, 8),
                    ParseDisposition(reader.GetString(9)),
                    ParseSuppressionReason(reader.GetString(10)),
                    GetNullableString(reader, 11),
                    GetNullableInt32(reader, 12),
                    GetNullableInt32(reader, 13),
                    ParseStatus(reader.GetString(14)),
                    reader.GetInt32(15),
                    GetNullableTimestamp(reader, 16),
                    ParseTimestamp(reader.GetString(17)),
                    ParseTimestamp(reader.GetString(18))));
            }
        }

        return new(
            Array.AsReadOnly(ruleRuns.ToArray()),
            Array.AsReadOnly(actionRuns.ToArray()));
    }

    private static ValidatedPlan ValidatePlan(FeedAutomationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string entryId = ValidateEntryId(plan.EntryId);
        if (plan.RuleEvaluations is null
            || plan.RuleEvaluations.Count > FeedAutomationRuleInterpreter.MaximumRuleCount)
        {
            throw InvalidPlan("Rule evaluation count exceeds the local execution limit.");
        }
        if (plan.Actions is null || plan.Actions.Count > MaximumActionCount)
        {
            throw InvalidPlan("Action decision count exceeds the local execution limit.");
        }
        FeedAutomationRuleEvaluation[] evaluationItems =
            plan.RuleEvaluations.ToArray();
        FeedAutomationActionDecision[] actionItems = plan.Actions.ToArray();

        var evaluations = new Dictionary<RuleKey, FeedAutomationRuleEvaluation>();
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (FeedAutomationRuleEvaluation evaluation in evaluationItems)
        {
            if (evaluation is null)
            {
                throw InvalidPlan("Rule evaluation cannot be null.");
            }
            ValidateRuleIdentity(evaluation.RuleId, evaluation.RuleVersion);
            if (!Enum.IsDefined(evaluation.Outcome))
            {
                throw InvalidPlan("Rule evaluation outcome is invalid.");
            }
            if (!ruleIds.Add(evaluation.RuleId)
                || !evaluations.TryAdd(
                    new(evaluation.RuleId, evaluation.RuleVersion),
                    evaluation))
            {
                throw InvalidPlan("Rule identifiers must be unique within one plan.");
            }
        }

        var actions = new Dictionary<ActionKey, FeedAutomationActionDecision>();
        foreach (FeedAutomationActionDecision action in actionItems)
        {
            ValidateAction(action, evaluations);
            if (!actions.TryAdd(
                    new(
                        action.RuleId,
                        action.RuleVersion,
                        action.ActionOrder),
                    action))
            {
                throw InvalidPlan("Action orders must be unique within a rule version.");
            }
        }
        ValidateSuppressionWinners(actions);
        if (actions.Values
            .GroupBy(action => new RuleKey(action.RuleId, action.RuleVersion))
            .Any(group =>
                group.Count() > FeedAutomationRuleValidator.MaximumActionCount))
        {
            throw InvalidPlan("A rule has too many action decisions.");
        }

        Dictionary<RuleKey, IReadOnlyList<FeedAutomationActionDecision>> actionsByRule =
            actionItems
                .GroupBy(action => new RuleKey(action.RuleId, action.RuleVersion))
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<FeedAutomationActionDecision>)group.ToArray());
        return new(entryId, Array.AsReadOnly(evaluationItems), actionsByRule);
    }

    private static void ValidateAction(
        FeedAutomationActionDecision action,
        IReadOnlyDictionary<RuleKey, FeedAutomationRuleEvaluation> evaluations)
    {
        if (action is null)
        {
            throw InvalidPlan("Action decision cannot be null.");
        }
        ValidateRuleIdentity(action.RuleId, action.RuleVersion);
        if (!evaluations.TryGetValue(
                new(action.RuleId, action.RuleVersion),
                out FeedAutomationRuleEvaluation? evaluation)
            || evaluation.Outcome != FeedAutomationRuleEvaluationOutcome.Matched)
        {
            throw InvalidPlan("Each action must belong to a matched rule evaluation.");
        }
        if (action.RulePriority is < 0 or > FeedAutomationRuleValidator.MaximumPriority
            || action.RuleConflictOrder is < 0
                or > FeedAutomationRuleValidator.MaximumConflictOrder
            || action.ActionOrder is < 0 or > FeedAutomationRuleValidator.MaximumActionOrder
            || !Enum.IsDefined(action.Type)
            || !Enum.IsDefined(action.Disposition)
            || !Enum.IsDefined(action.SuppressionReason))
        {
            throw InvalidPlan("Action decision metadata is invalid.");
        }

        switch (action.Type)
        {
            case FeedAutomationActionType.AddTag:
                ValidateActionValue(action.Value, "Tag");
                break;
            case FeedAutomationActionType.Translate:
                if (action.Value is not ("zh-Hans" or "en" or "ja" or "ko"))
                {
                    throw InvalidPlan("Translation action language is invalid.");
                }
                break;
            default:
                if (action.Value is not null)
                {
                    throw InvalidPlan("This action type cannot contain a value.");
                }
                break;
        }

        bool hasCompleteWinner = action.WinningRuleId is not null
            && action.WinningRuleVersion is not null
            && action.WinningActionOrder is not null;
        bool hasAnyWinner = action.WinningRuleId is not null
            || action.WinningRuleVersion is not null
            || action.WinningActionOrder is not null;
        if (action.Disposition == FeedAutomationActionDisposition.Planned)
        {
            if (action.SuppressionReason != FeedAutomationActionSuppressionReason.None
                || hasAnyWinner)
            {
                throw InvalidPlan("A planned action cannot contain suppression metadata.");
            }
            return;
        }

        if (action.SuppressionReason == FeedAutomationActionSuppressionReason.None
            || !hasCompleteWinner)
        {
            throw InvalidPlan("A suppressed action requires a complete winner reference.");
        }
        ValidateRuleIdentity(action.WinningRuleId!, action.WinningRuleVersion!.Value);
        if (action.WinningActionOrder is < 0
            or > FeedAutomationRuleValidator.MaximumActionOrder)
        {
            throw InvalidPlan("Suppression winner action order is invalid.");
        }
        if ((action.Type == FeedAutomationActionType.AddTag)
            != (action.SuppressionReason
                == FeedAutomationActionSuppressionReason.DuplicateTag))
        {
            throw InvalidPlan("Suppression reason does not match the action type.");
        }
    }

    private static void ValidateSuppressionWinners(
        IReadOnlyDictionary<ActionKey, FeedAutomationActionDecision> actions)
    {
        foreach (FeedAutomationActionDecision action in actions.Values
                     .Where(item =>
                         item.Disposition == FeedAutomationActionDisposition.Suppressed))
        {
            var winnerKey = new ActionKey(
                action.WinningRuleId!,
                action.WinningRuleVersion!.Value,
                action.WinningActionOrder!.Value);
            if (!actions.TryGetValue(
                    winnerKey,
                    out FeedAutomationActionDecision? winner)
                || winner.Disposition != FeedAutomationActionDisposition.Planned
                || winner.Type != action.Type
                || (action.Type == FeedAutomationActionType.AddTag
                    && !string.Equals(
                        winner.Value,
                        action.Value,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw InvalidPlan("Suppression winner must identify a matching planned action.");
            }
        }
    }

    private static string ValidateEntryId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > FeedAutomationRuleInterpreter.MaximumEntryIdLength
            || value.Any(char.IsControl)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw InvalidPlan("Entry ID is invalid.");
        }
        return value;
    }

    private static void ValidateRuleIdentity(string ruleId, int ruleVersion)
    {
        if (!Guid.TryParseExact(ruleId, "D", out Guid parsed)
            || !string.Equals(
                parsed.ToString("D"),
                ruleId,
                StringComparison.Ordinal)
            || ruleVersion < 1)
        {
            throw InvalidPlan("Rule identity is invalid.");
        }
    }

    private static void ValidateActionValue(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > FeedAutomationRuleValidator.MaximumActionValueLength
            || value.Any(char.IsControl)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw InvalidPlan($"{label} action value is invalid.");
        }
    }

    private static string CreateIdempotencyKey(
        string entryId,
        string ruleId,
        int ruleVersion,
        int actionOrder)
    {
        string identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{entryId}\n{ruleId}\n{ruleVersion}\n{actionOrder}");
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    private static string ToDatabase(
        FeedAutomationRuleEvaluationOutcome outcome) =>
        outcome switch
        {
            FeedAutomationRuleEvaluationOutcome.Disabled => "DISABLED",
            FeedAutomationRuleEvaluationOutcome.Matched => "MATCHED",
            FeedAutomationRuleEvaluationOutcome.NotMatched => "NOT_MATCHED",
            _ => throw InvalidPlan("Rule evaluation outcome is invalid.")
        };

    private static string ToDatabase(FeedAutomationActionType type) =>
        type switch
        {
            FeedAutomationActionType.AddTag => "ADD_TAG",
            FeedAutomationActionType.Hide => "HIDE",
            FeedAutomationActionType.MarkRead => "MARK_READ",
            FeedAutomationActionType.GenerateSummary => "GENERATE_SUMMARY",
            FeedAutomationActionType.Translate => "TRANSLATE",
            FeedAutomationActionType.SendToMedia => "SEND_TO_MEDIA",
            FeedAutomationActionType.Notify => "NOTIFY",
            _ => throw InvalidPlan("Action type is invalid.")
        };

    private static string ToDatabase(
        FeedAutomationActionSuppressionReason reason) =>
        reason switch
        {
            FeedAutomationActionSuppressionReason.None => "NONE",
            FeedAutomationActionSuppressionReason.DuplicateSingleton =>
                "DUPLICATE_SINGLETON",
            FeedAutomationActionSuppressionReason.DuplicateTag => "DUPLICATE_TAG",
            _ => throw InvalidPlan("Suppression reason is invalid.")
        };

    private static FeedAutomationRuleEvaluationOutcome ParseEvaluationOutcome(
        string value) =>
        value switch
        {
            "DISABLED" => FeedAutomationRuleEvaluationOutcome.Disabled,
            "MATCHED" => FeedAutomationRuleEvaluationOutcome.Matched,
            "NOT_MATCHED" => FeedAutomationRuleEvaluationOutcome.NotMatched,
            _ => throw new InvalidDataException(
                "Stored automation rule evaluation outcome is invalid.")
        };

    private static FeedAutomationActionType ParseActionType(string value) =>
        value switch
        {
            "ADD_TAG" => FeedAutomationActionType.AddTag,
            "HIDE" => FeedAutomationActionType.Hide,
            "MARK_READ" => FeedAutomationActionType.MarkRead,
            "GENERATE_SUMMARY" => FeedAutomationActionType.GenerateSummary,
            "TRANSLATE" => FeedAutomationActionType.Translate,
            "SEND_TO_MEDIA" => FeedAutomationActionType.SendToMedia,
            "NOTIFY" => FeedAutomationActionType.Notify,
            _ => throw new InvalidDataException("Stored automation action type is invalid.")
        };

    private static FeedAutomationActionDisposition ParseDisposition(string value) =>
        value switch
        {
            "PLANNED" => FeedAutomationActionDisposition.Planned,
            "SUPPRESSED" => FeedAutomationActionDisposition.Suppressed,
            _ => throw new InvalidDataException(
                "Stored automation action disposition is invalid.")
        };

    private static FeedAutomationActionSuppressionReason ParseSuppressionReason(
        string value) =>
        value switch
        {
            "NONE" => FeedAutomationActionSuppressionReason.None,
            "DUPLICATE_SINGLETON" =>
                FeedAutomationActionSuppressionReason.DuplicateSingleton,
            "DUPLICATE_TAG" => FeedAutomationActionSuppressionReason.DuplicateTag,
            _ => throw new InvalidDataException(
                "Stored automation suppression reason is invalid.")
        };

    private static FeedAutomationActionRunStatus ParseStatus(string value) =>
        value switch
        {
            "PENDING" => FeedAutomationActionRunStatus.Pending,
            "RUNNING" => FeedAutomationActionRunStatus.Running,
            "RETRY" => FeedAutomationActionRunStatus.Retry,
            "SUCCEEDED" => FeedAutomationActionRunStatus.Succeeded,
            "FAILED" => FeedAutomationActionRunStatus.Failed,
            "SUPPRESSED" => FeedAutomationActionRunStatus.Suppressed,
            _ => throw new InvalidDataException(
                "Stored automation action status is invalid.")
        };

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static DateTimeOffset? GetNullableTimestamp(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : ParseTimestamp(reader.GetString(ordinal));

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);

    private static InvalidDataException InvalidPlan(string message) => new(message);

    private readonly record struct RuleKey(string RuleId, int RuleVersion);

    private readonly record struct ActionKey(
        string RuleId,
        int RuleVersion,
        int ActionOrder);

    private sealed record ValidatedPlan(
        string EntryId,
        IReadOnlyList<FeedAutomationRuleEvaluation> Evaluations,
        IReadOnlyDictionary<RuleKey, IReadOnlyList<FeedAutomationActionDecision>>
            ActionsByRule);
}
