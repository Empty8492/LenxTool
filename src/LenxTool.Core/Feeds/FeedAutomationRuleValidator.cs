using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LenxTool.Core.Models;

namespace LenxTool.Core.Feeds;

public static class FeedAutomationRuleValidator
{
    public const int MaximumRuleNameLength = 120;
    public const int MaximumConditionCount = 16;
    public const int MaximumActionCount = 8;
    public const int MaximumTextValueLength = 512;
    public const int MaximumRegexPatternLength = 256;
    public const int MaximumActionValueLength = 80;
    public const int MaximumPriority = 1000;
    public const int MaximumConflictOrder = 1000;
    public const int MaximumActionOrder = 1000;

    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Dictionary<string, string> TranslationLanguages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-Hans"] = "zh-Hans",
            ["en"] = "en",
            ["ja"] = "ja",
            ["ko"] = "ko"
        };

    public static FeedAutomationRule ValidateAndNormalize(FeedAutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!Guid.TryParseExact(rule.Id, "D", out Guid ruleId)
            || rule.Version < 1
            || rule.Priority is < 0 or > MaximumPriority
            || rule.ConflictOrder is < 0 or > MaximumConflictOrder
            || !Enum.IsDefined(rule.MatchMode))
        {
            throw InvalidRule("Rule metadata contains an invalid value.");
        }

        string name = NormalizeText(rule.Name, MaximumRuleNameLength, "Rule name");
        if (rule.Conditions is null
            || rule.Conditions.Count is < 1 or > MaximumConditionCount
            || rule.Actions is null
            || rule.Actions.Count is < 1 or > MaximumActionCount)
        {
            throw InvalidRule("A rule must have a bounded number of conditions and actions.");
        }

        ReadOnlyCollection<FeedAutomationCondition> conditions = Array.AsReadOnly(
            rule.Conditions.Select(NormalizeCondition).ToArray());
        FeedAutomationAction[] actions = rule.Actions
            .Select(NormalizeAction)
            .OrderBy(action => action.Order)
            .ToArray();
        if (actions.Select(action => action.Order).Distinct().Count() != actions.Length)
        {
            throw InvalidRule("Action order values must be unique.");
        }
        if (actions
            .Where(action => action.Type != FeedAutomationActionType.AddTag)
            .GroupBy(action => action.Type)
            .Any(group => group.Count() > 1))
        {
            throw InvalidRule("Only tag actions may be repeated in one rule.");
        }

        return new(
            ruleId.ToString("D"),
            rule.Version,
            name,
            rule.Priority,
            rule.ConflictOrder,
            rule.IsEnabled,
            rule.MatchMode,
            conditions,
            Array.AsReadOnly(actions));
    }

    public static Regex CompileRegex(string pattern)
    {
        string normalized = NormalizeRegexPattern(pattern);
        try
        {
            return new(
                normalized,
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                RegexTimeout);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            throw InvalidRule("Regular expression is invalid or requires backtracking.", exception);
        }
    }

    private static FeedAutomationCondition NormalizeCondition(FeedAutomationCondition condition)
    {
        if (condition is null
            || !Enum.IsDefined(condition.Field)
            || !Enum.IsDefined(condition.Operator)
            || !IsOperatorAllowed(condition.Field, condition.Operator))
        {
            throw InvalidRule("Condition field and operator combination is not supported.");
        }

        if (condition.Operator == FeedAutomationOperator.Exists)
        {
            if (condition.Value is not null)
            {
                throw InvalidRule("The exists operator does not accept a value.");
            }

            return condition;
        }

        if (condition.Value is null)
        {
            throw InvalidRule("Condition value is required.");
        }

        string value = condition.Field switch
        {
            FeedAutomationField.Feed or FeedAutomationField.Category =>
                NormalizeIdentifier(condition.Value),
            FeedAutomationField.PublishedAt =>
                NormalizeTimestamp(condition.Value),
            FeedAutomationField.HasAudio or FeedAutomationField.HasVideo =>
                NormalizeBoolean(condition.Value),
            _ when condition.Operator == FeedAutomationOperator.Regex =>
                CompileRegex(condition.Value).ToString(),
            _ => NormalizeText(condition.Value, MaximumTextValueLength, "Condition value")
        };

        return condition with { Value = value };
    }

    private static FeedAutomationAction NormalizeAction(FeedAutomationAction action)
    {
        if (action is null
            || !Enum.IsDefined(action.Type)
            || action.Order is < 0 or > MaximumActionOrder)
        {
            throw InvalidRule("Action contains an invalid type or order.");
        }

        string? value = action.Type switch
        {
            FeedAutomationActionType.AddTag =>
                NormalizeText(action.Value, MaximumActionValueLength, "Tag"),
            FeedAutomationActionType.Translate =>
                NormalizeTranslationLanguage(action.Value),
            _ => RequireNoActionValue(action.Value)
        };
        return action with { Value = value };
    }

    private static bool IsOperatorAllowed(
        FeedAutomationField field,
        FeedAutomationOperator @operator)
    {
        return field switch
        {
            FeedAutomationField.Feed =>
                @operator == FeedAutomationOperator.Equals,
            FeedAutomationField.Category =>
                @operator is FeedAutomationOperator.Equals or FeedAutomationOperator.Exists,
            FeedAutomationField.Title
                or FeedAutomationField.Author
                or FeedAutomationField.Content =>
                @operator is FeedAutomationOperator.Equals
                    or FeedAutomationOperator.Contains
                    or FeedAutomationOperator.Regex
                    or FeedAutomationOperator.Exists,
            FeedAutomationField.Language =>
                @operator is FeedAutomationOperator.Equals or FeedAutomationOperator.Exists,
            FeedAutomationField.PublishedAt =>
                @operator is FeedAutomationOperator.Before
                    or FeedAutomationOperator.After
                    or FeedAutomationOperator.Exists,
            FeedAutomationField.HasAudio
                or FeedAutomationField.HasVideo =>
                @operator is FeedAutomationOperator.Equals or FeedAutomationOperator.Exists,
            _ => false
        };
    }

    private static string NormalizeIdentifier(string value)
    {
        string normalized = NormalizeText(value, 36, "Identifier");
        if (!Guid.TryParseExact(normalized, "D", out Guid identifier))
        {
            throw InvalidRule("Feed and category values must be canonical identifiers.");
        }

        return identifier.ToString("D");
    }

    private static string NormalizeTimestamp(string value)
    {
        string normalized = NormalizeText(value, 64, "Timestamp");
        if (!Regex.IsMatch(
                normalized,
                @"(?:Z|[+-]\d{2}:\d{2})$",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                RegexTimeout)
            || !DateTimeOffset.TryParseExact(
                normalized,
                ["O", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTimeOffset timestamp))
        {
            throw InvalidRule("Timestamp must be an ISO 8601 value with an offset.");
        }

        return timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static string NormalizeBoolean(string value)
    {
        string normalized = NormalizeText(value, 5, "Boolean value");
        if (!bool.TryParse(normalized, out bool parsed))
        {
            throw InvalidRule("Media presence value must be true or false.");
        }

        return parsed ? "true" : "false";
    }

    private static string NormalizeTranslationLanguage(string? value)
    {
        string normalized = NormalizeText(value, 16, "Translation language");
        if (!TranslationLanguages.TryGetValue(normalized, out string? canonical))
        {
            throw InvalidRule("Translation language is not supported.");
        }

        return canonical;
    }

    private static string NormalizeRegexPattern(string? value)
    {
        if (value is null
            || value.Length is < 1 or > MaximumRegexPatternLength
            || string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl))
        {
            throw InvalidRule(
                "Regular expression is empty, oversized, or contains control characters.");
        }

        return value;
    }

    private static string? RequireNoActionValue(string? value)
    {
        if (value is not null)
        {
            throw InvalidRule("This action does not accept a payload.");
        }

        return null;
    }

    private static string NormalizeText(
        string? value,
        int maximumLength,
        string fieldName)
    {
        if (value is null)
        {
            throw InvalidRule($"{fieldName} is required.");
        }

        string normalized = value.Trim();
        normalized = normalized.Normalize(NormalizationForm.FormKC);

        if (normalized.Length is < 1
            || normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
        {
            throw InvalidRule($"{fieldName} is empty, oversized, or contains control characters.");
        }

        return normalized;
    }

    private static InvalidDataException InvalidRule(string message, Exception? inner = null)
        => new(message, inner);
}
