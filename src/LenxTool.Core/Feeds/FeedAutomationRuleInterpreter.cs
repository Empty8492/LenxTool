using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LenxTool.Core.Models;

namespace LenxTool.Core.Feeds;

public static class FeedAutomationRuleInterpreter
{
    public const int MaximumRuleCount = 100;
    public const int MaximumEntryIdLength = 256;
    public const int MaximumTitleLength = 2_000;
    public const int MaximumAuthorLength = 1_000;
    public const int MaximumContentLength = 100_000;
    public const int MaximumLanguageLength = 32;

    public static FeedAutomationRuleSet Compile(IReadOnlyList<FeedAutomationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count > MaximumRuleCount)
        {
            throw InvalidRuleSet("Automation rule count exceeds the local execution limit.");
        }

        FeedAutomationRule[] normalized = rules
            .Select(rule => rule is null
                ? throw InvalidRuleSet("Automation rule cannot be null.")
                : FeedAutomationRuleValidator.ValidateAndNormalize(rule))
            .ToArray();
        if (normalized
            .GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw InvalidRuleSet("Automation rule identifiers must be unique.");
        }

        CompiledFeedAutomationRule[] compiled = normalized
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.ConflictOrder)
            .ThenBy(rule => rule.Id, StringComparer.Ordinal)
            .Select(CompileRule)
            .ToArray();
        return new(new CompiledFeedAutomationRuleSet(Array.AsReadOnly(compiled)));
    }

    internal static FeedAutomationEntryContext NormalizeContext(
        FeedAutomationEntryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string entryId = NormalizeEntryId(context.EntryId);
        string feedId = NormalizeGuid(context.FeedId, "Feed ID");
        string? categoryId = context.CategoryId is null
            ? null
            : NormalizeGuid(context.CategoryId, "Category ID");
        string title = NormalizeRequiredData(
            context.Title,
            MaximumTitleLength,
            "Entry title");
        string? author = NormalizeOptionalData(
            context.Author,
            MaximumAuthorLength,
            "Entry author");
        string content = NormalizeOptionalData(
            context.Content,
            MaximumContentLength,
            "Entry content") ?? string.Empty;
        string? language = NormalizeOptionalData(
            context.Language,
            MaximumLanguageLength,
            "Entry language");

        return new(
            entryId,
            feedId,
            categoryId,
            title,
            author,
            content,
            language,
            context.PublishedAt?.ToUniversalTime(),
            context.HasAudio,
            context.HasVideo);
    }

    private static CompiledFeedAutomationRule CompileRule(FeedAutomationRule rule)
    {
        CompiledFeedAutomationCondition[] conditions = rule.Conditions
            .Select(CompileCondition)
            .ToArray();
        return new(rule, Array.AsReadOnly(conditions));
    }

    private static CompiledFeedAutomationCondition CompileCondition(
        FeedAutomationCondition condition)
    {
        Regex? regex = condition.Operator == FeedAutomationOperator.Regex
            ? FeedAutomationRuleValidator.CompileRegex(condition.Value!)
            : null;
        DateTimeOffset? timestamp = condition.Field == FeedAutomationField.PublishedAt
            && condition.Operator != FeedAutomationOperator.Exists
            ? DateTimeOffset.ParseExact(
                condition.Value!,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None)
            : null;
        bool? boolean = condition.Field is (FeedAutomationField.HasAudio
            or FeedAutomationField.HasVideo)
            && condition.Operator == FeedAutomationOperator.Equals
            ? bool.Parse(condition.Value!)
            : null;
        return new(condition, regex, timestamp, boolean);
    }

    private static string NormalizeEntryId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidRuleSet("Entry ID is required.");
        }
        string normalized = value.Trim();
        if (normalized.Length > MaximumEntryIdLength || normalized.Any(char.IsControl))
        {
            throw InvalidRuleSet("Entry ID is oversized or contains control characters.");
        }
        return normalized;
    }

    private static string NormalizeGuid(string value, string label)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed))
        {
            throw InvalidRuleSet($"{label} must be a canonical identifier.");
        }
        return parsed.ToString("D");
    }

    private static string NormalizeRequiredData(string value, int maximumLength, string label)
    {
        string? normalized = NormalizeOptionalData(value, maximumLength, label);
        return normalized ?? throw InvalidRuleSet($"{label} is required.");
    }

    private static string? NormalizeOptionalData(
        string? value,
        int maximumLength,
        string label)
    {
        if (value is null)
        {
            return null;
        }
        string normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length > maximumLength)
        {
            throw InvalidRuleSet($"{label} exceeds the local execution limit.");
        }
        return normalized.Length == 0 ? null : normalized;
    }

    private static InvalidDataException InvalidRuleSet(string message) => new(message);

    internal sealed record CompiledFeedAutomationCondition(
        FeedAutomationCondition Condition,
        Regex? Regex,
        DateTimeOffset? Timestamp,
        bool? Boolean);

    internal sealed record CompiledFeedAutomationRule(
        FeedAutomationRule Rule,
        ReadOnlyCollection<CompiledFeedAutomationCondition> Conditions);

    internal sealed class CompiledFeedAutomationRuleSet
    {
        private readonly ReadOnlyCollection<CompiledFeedAutomationRule> _rules;

        internal CompiledFeedAutomationRuleSet(
            ReadOnlyCollection<CompiledFeedAutomationRule> rules)
        {
            _rules = rules;
        }

        public FeedAutomationPlan Plan(FeedAutomationEntryContext context)
        {
            FeedAutomationEntryContext normalized = NormalizeContext(context);
            var evaluations = new List<FeedAutomationRuleEvaluation>(_rules.Count);
            var decisions = new List<FeedAutomationActionDecision>();
            var singletonWinners =
                new Dictionary<FeedAutomationActionType, FeedAutomationActionDecision>();
            var tagWinners =
                new Dictionary<string, FeedAutomationActionDecision>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (CompiledFeedAutomationRule compiled in _rules)
            {
                FeedAutomationRule rule = compiled.Rule;
                if (!rule.IsEnabled)
                {
                    evaluations.Add(new(
                        rule.Id,
                        rule.Version,
                        FeedAutomationRuleEvaluationOutcome.Disabled));
                    continue;
                }

                bool matched = RuleMatches(compiled, normalized);
                evaluations.Add(new(
                    rule.Id,
                    rule.Version,
                    matched
                        ? FeedAutomationRuleEvaluationOutcome.Matched
                        : FeedAutomationRuleEvaluationOutcome.NotMatched));
                if (!matched)
                {
                    continue;
                }

                foreach (FeedAutomationAction action in rule.Actions)
                {
                    FeedAutomationActionDecision? winner = action.Type
                        == FeedAutomationActionType.AddTag
                        ? tagWinners.GetValueOrDefault(action.Value!)
                        : singletonWinners.GetValueOrDefault(action.Type);
                    FeedAutomationActionSuppressionReason suppressionReason =
                        winner is null
                            ? FeedAutomationActionSuppressionReason.None
                            : action.Type == FeedAutomationActionType.AddTag
                                ? FeedAutomationActionSuppressionReason.DuplicateTag
                                : FeedAutomationActionSuppressionReason.DuplicateSingleton;
                    var decision = new FeedAutomationActionDecision(
                        rule.Id,
                        rule.Version,
                        rule.Priority,
                        rule.ConflictOrder,
                        action.Type,
                        action.Order,
                        action.Value,
                        winner is null
                            ? FeedAutomationActionDisposition.Planned
                            : FeedAutomationActionDisposition.Suppressed,
                        suppressionReason,
                        winner?.RuleId,
                        winner?.RuleVersion,
                        winner?.ActionOrder);
                    decisions.Add(decision);
                    if (winner is null)
                    {
                        if (action.Type == FeedAutomationActionType.AddTag)
                        {
                            tagWinners.Add(action.Value!, decision);
                        }
                        else
                        {
                            singletonWinners.Add(action.Type, decision);
                        }
                    }
                }
            }

            return new(
                normalized.EntryId,
                Array.AsReadOnly(evaluations.ToArray()),
                Array.AsReadOnly(decisions.ToArray()));
        }

        private static bool RuleMatches(
            CompiledFeedAutomationRule rule,
            FeedAutomationEntryContext context)
        {
            return rule.Rule.MatchMode == FeedAutomationMatchMode.All
                ? rule.Conditions.All(condition => ConditionMatches(condition, context))
                : rule.Conditions.Any(condition => ConditionMatches(condition, context));
        }

        private static bool ConditionMatches(
            CompiledFeedAutomationCondition compiled,
            FeedAutomationEntryContext context)
        {
            FeedAutomationCondition condition = compiled.Condition;
            return condition.Operator switch
            {
                FeedAutomationOperator.Exists => FieldExists(condition.Field, context),
                FeedAutomationOperator.Before =>
                    context.PublishedAt is not null
                    && context.PublishedAt.Value < compiled.Timestamp!.Value,
                FeedAutomationOperator.After =>
                    context.PublishedAt is not null
                    && context.PublishedAt.Value > compiled.Timestamp!.Value,
                FeedAutomationOperator.Equals => EqualsCondition(compiled, context),
                FeedAutomationOperator.Contains =>
                    GetText(condition.Field, context)?.Contains(
                        condition.Value!,
                        StringComparison.OrdinalIgnoreCase) == true,
                FeedAutomationOperator.Regex =>
                    GetText(condition.Field, context) is string text
                    && compiled.Regex!.IsMatch(text),
                _ => false
            };
        }

        private static bool EqualsCondition(
            CompiledFeedAutomationCondition compiled,
            FeedAutomationEntryContext context)
        {
            FeedAutomationCondition condition = compiled.Condition;
            return condition.Field switch
            {
                FeedAutomationField.Feed =>
                    string.Equals(context.FeedId, condition.Value, StringComparison.Ordinal),
                FeedAutomationField.Category =>
                    string.Equals(context.CategoryId, condition.Value, StringComparison.Ordinal),
                FeedAutomationField.HasAudio => context.HasAudio == compiled.Boolean!.Value,
                FeedAutomationField.HasVideo => context.HasVideo == compiled.Boolean!.Value,
                _ => string.Equals(
                    GetText(condition.Field, context),
                    condition.Value,
                    StringComparison.OrdinalIgnoreCase)
            };
        }

        private static bool FieldExists(
            FeedAutomationField field,
            FeedAutomationEntryContext context)
        {
            return field switch
            {
                FeedAutomationField.Feed => true,
                FeedAutomationField.Category => context.CategoryId is not null,
                FeedAutomationField.Title => context.Title.Length > 0,
                FeedAutomationField.Author => context.Author is not null,
                FeedAutomationField.Content => context.Content.Length > 0,
                FeedAutomationField.Language => context.Language is not null,
                FeedAutomationField.PublishedAt => context.PublishedAt is not null,
                FeedAutomationField.HasAudio => context.HasAudio,
                FeedAutomationField.HasVideo => context.HasVideo,
                _ => false
            };
        }

        private static string? GetText(
            FeedAutomationField field,
            FeedAutomationEntryContext context)
        {
            return field switch
            {
                FeedAutomationField.Title => context.Title,
                FeedAutomationField.Author => context.Author,
                FeedAutomationField.Content => context.Content,
                FeedAutomationField.Language => context.Language,
                _ => null
            };
        }
    }
}

public sealed class FeedAutomationRuleSet
{
    private readonly FeedAutomationRuleInterpreter.CompiledFeedAutomationRuleSet _compiled;

    internal FeedAutomationRuleSet(
        FeedAutomationRuleInterpreter.CompiledFeedAutomationRuleSet compiled)
    {
        _compiled = compiled;
    }

    public FeedAutomationPlan Plan(FeedAutomationEntryContext context) =>
        _compiled.Plan(context);
}
