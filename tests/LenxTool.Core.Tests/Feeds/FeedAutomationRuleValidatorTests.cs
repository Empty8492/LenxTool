using System.Text.RegularExpressions;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Feeds;

public sealed class FeedAutomationRuleValidatorTests
{
    private const string RuleId = "10000000-0000-4000-8000-000000000014";
    private const string FeedId = "20000000-0000-4000-8000-000000000014";

    [Fact]
    public void ValidateAndNormalizeReturnsDeterministicSafeSnapshot()
    {
        FeedAutomationCondition[] sourceConditions =
        [
            new(FeedAutomationField.Feed, FeedAutomationOperator.Equals, FeedId.ToUpperInvariant()),
            new(FeedAutomationField.Title, FeedAutomationOperator.Contains, "  release notes  "),
            new(FeedAutomationField.PublishedAt, FeedAutomationOperator.After, "2026-07-01T08:00:00+08:00"),
            new(FeedAutomationField.HasVideo, FeedAutomationOperator.Equals, "TRUE")
        ];
        FeedAutomationAction[] sourceActions =
        [
            new(FeedAutomationActionType.Notify, 30, null),
            new(FeedAutomationActionType.AddTag, 10, "  AI  "),
            new(FeedAutomationActionType.GenerateSummary, 20, null)
        ];
        var source = new FeedAutomationRule(
            RuleId.ToUpperInvariant(),
            2,
            "  AI release digest  ",
            200,
            10,
            true,
            FeedAutomationMatchMode.All,
            sourceConditions,
            sourceActions);

        FeedAutomationRule normalized = FeedAutomationRuleValidator.ValidateAndNormalize(source);

        Assert.Equal(RuleId, normalized.Id);
        Assert.Equal("AI release digest", normalized.Name);
        Assert.Equal(FeedId, normalized.Conditions[0].Value);
        Assert.Equal("release notes", normalized.Conditions[1].Value);
        Assert.Equal("2026-07-01T00:00:00.0000000+00:00", normalized.Conditions[2].Value);
        Assert.Equal("true", normalized.Conditions[3].Value);
        Assert.Equal([10, 20, 30], normalized.Actions.Select(action => action.Order));
        Assert.Equal("AI", normalized.Actions[0].Value);

        sourceConditions[0] = new(FeedAutomationField.Title, FeedAutomationOperator.Exists, null);
        sourceActions[0] = new(FeedAutomationActionType.Hide, 30, null);
        Assert.Equal(FeedAutomationField.Feed, normalized.Conditions[0].Field);
        Assert.Equal(FeedAutomationActionType.Notify, normalized.Actions[2].Type);
    }

    [Theory]
    [InlineData(FeedAutomationField.Feed, FeedAutomationOperator.Contains, "feed")]
    [InlineData(FeedAutomationField.Category, FeedAutomationOperator.Regex, "news")]
    [InlineData(FeedAutomationField.Title, FeedAutomationOperator.Before, "2026-07-01")]
    [InlineData(FeedAutomationField.Language, FeedAutomationOperator.Contains, "zh")]
    [InlineData(FeedAutomationField.PublishedAt, FeedAutomationOperator.Equals, "2026-07-01")]
    [InlineData(FeedAutomationField.HasAudio, FeedAutomationOperator.After, "true")]
    public void ValidateAndNormalizeRejectsUnsupportedFieldOperatorPairs(
        FeedAutomationField field,
        FeedAutomationOperator @operator,
        string value)
    {
        FeedAutomationRule rule = ValidRule(
            conditions: [new(field, @operator, value)]);

        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleValidator.ValidateAndNormalize(rule));
    }

    [Fact]
    public void ValidateAndNormalizeRejectsUnknownEnumsAndAmbiguousOrdering()
    {
        FeedAutomationRule unknownField = ValidRule(
            conditions: [new((FeedAutomationField)999, FeedAutomationOperator.Equals, "value")]);
        FeedAutomationRule unknownOperator = ValidRule(
            conditions: [new(FeedAutomationField.Title, (FeedAutomationOperator)999, "value")]);
        FeedAutomationRule unknownAction = ValidRule(
            actions: [new((FeedAutomationActionType)999, 0, null)]);
        FeedAutomationRule unknownMatchMode = ValidRule(matchMode: (FeedAutomationMatchMode)999);
        FeedAutomationRule duplicateOrder = ValidRule(
            actions:
            [
                new(FeedAutomationActionType.Hide, 10, null),
                new(FeedAutomationActionType.MarkRead, 10, null)
            ]);

        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleValidator.ValidateAndNormalize(unknownField));
        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleValidator.ValidateAndNormalize(unknownOperator));
        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleValidator.ValidateAndNormalize(unknownAction));
        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleValidator.ValidateAndNormalize(unknownMatchMode));
        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleValidator.ValidateAndNormalize(duplicateOrder));
    }

    [Fact]
    public void ValidateAndNormalizeRejectsMissingOrOversizedRuleParts()
    {
        FeedAutomationRule invalidIdentity = ValidRule() with { Id = "not-a-rule-id" };
        FeedAutomationRule invalidVersion = ValidRule() with { Version = 0 };
        FeedAutomationRule invalidPriority = ValidRule() with
        {
            Priority = FeedAutomationRuleValidator.MaximumPriority + 1
        };
        FeedAutomationRule invalidConflictOrder = ValidRule() with
        {
            ConflictOrder = FeedAutomationRuleValidator.MaximumConflictOrder + 1
        };
        FeedAutomationRule invalidName = ValidRule() with
        {
            Name = new string('n', FeedAutomationRuleValidator.MaximumRuleNameLength + 1)
        };
        FeedAutomationRule noConditions = ValidRule(conditions: []);
        FeedAutomationRule tooManyConditions = ValidRule(
            conditions: Enumerable.Range(0, FeedAutomationRuleValidator.MaximumConditionCount + 1)
                .Select(index => new FeedAutomationCondition(
                    FeedAutomationField.Title,
                    FeedAutomationOperator.Contains,
                    $"value-{index}"))
                .ToArray());
        FeedAutomationRule noActions = ValidRule(actions: []);
        FeedAutomationRule tooManyActions = ValidRule(
            actions: Enumerable.Range(0, FeedAutomationRuleValidator.MaximumActionCount + 1)
                .Select(index => new FeedAutomationAction(
                    FeedAutomationActionType.AddTag,
                    index,
                    $"tag-{index}"))
                .ToArray());

        FeedAutomationRule[] invalidRules =
        [
            invalidIdentity,
            invalidVersion,
            invalidPriority,
            invalidConflictOrder,
            invalidName,
            noConditions,
            tooManyConditions,
            noActions,
            tooManyActions
        ];
        Assert.All(invalidRules, rule =>
            Assert.Throws<InvalidDataException>(() =>
                FeedAutomationRuleValidator.ValidateAndNormalize(rule)));
    }

    [Fact]
    public void ValidateAndNormalizeAcceptsExactCollectionAndTextBoundaries()
    {
        FeedAutomationCondition[] conditions =
        [
            new(
                FeedAutomationField.Content,
                FeedAutomationOperator.Contains,
                new string('x', FeedAutomationRuleValidator.MaximumTextValueLength)),
            new(
                FeedAutomationField.Title,
                FeedAutomationOperator.Regex,
                new string('a', FeedAutomationRuleValidator.MaximumRegexPatternLength)),
            .. Enumerable.Range(2, FeedAutomationRuleValidator.MaximumConditionCount - 2)
                .Select(index => new FeedAutomationCondition(
                    FeedAutomationField.Author,
                    FeedAutomationOperator.Contains,
                    $"author-{index}"))
        ];
        FeedAutomationAction[] actions = Enumerable
            .Range(0, FeedAutomationRuleValidator.MaximumActionCount)
            .Select(index => new FeedAutomationAction(
                FeedAutomationActionType.AddTag,
                index == FeedAutomationRuleValidator.MaximumActionCount - 1
                    ? FeedAutomationRuleValidator.MaximumActionOrder
                    : index,
                index == 0
                    ? new string('t', FeedAutomationRuleValidator.MaximumActionValueLength)
                    : $"tag-{index}"))
            .ToArray();
        FeedAutomationRule rule = ValidRule(conditions, actions) with
        {
            Name = new string('n', FeedAutomationRuleValidator.MaximumRuleNameLength),
            Priority = FeedAutomationRuleValidator.MaximumPriority,
            ConflictOrder = FeedAutomationRuleValidator.MaximumConflictOrder
        };

        FeedAutomationRule normalized = FeedAutomationRuleValidator.ValidateAndNormalize(rule);

        Assert.Equal(FeedAutomationRuleValidator.MaximumConditionCount, normalized.Conditions.Count);
        Assert.Equal(FeedAutomationRuleValidator.MaximumActionCount, normalized.Actions.Count);
        Assert.Equal(FeedAutomationRuleValidator.MaximumActionOrder, normalized.Actions[^1].Order);
    }

    [Fact]
    public void ValidateAndNormalizeRejectsInvalidConditionOperands()
    {
        FeedAutomationRule existsWithValue = ValidRule(
            conditions:
            [
                new(FeedAutomationField.Author, FeedAutomationOperator.Exists, "arbitrary value")
            ]);
        FeedAutomationRule equalsWithoutValue = ValidRule(
            conditions:
            [
                new(FeedAutomationField.Title, FeedAutomationOperator.Equals, null)
            ]);
        FeedAutomationRule invalidFeedId = ValidRule(
            conditions:
            [
                new(FeedAutomationField.Feed, FeedAutomationOperator.Equals, "feed-id")
            ]);
        FeedAutomationRule invalidDate = ValidRule(
            conditions:
            [
                new(FeedAutomationField.PublishedAt, FeedAutomationOperator.Before, "tomorrow")
            ]);
        FeedAutomationRule dateWithoutOffset = ValidRule(
            conditions:
            [
                new(
                    FeedAutomationField.PublishedAt,
                    FeedAutomationOperator.Before,
                    "2026-07-01T08:00:00")
            ]);
        FeedAutomationRule invalidBoolean = ValidRule(
            conditions:
            [
                new(FeedAutomationField.HasAudio, FeedAutomationOperator.Equals, "yes")
            ]);
        FeedAutomationRule oversizedText = ValidRule(
            conditions:
            [
                new(
                    FeedAutomationField.Content,
                    FeedAutomationOperator.Contains,
                    new string('x', FeedAutomationRuleValidator.MaximumTextValueLength + 1))
            ]);

        FeedAutomationRule[] invalidRules =
        [
            existsWithValue,
            equalsWithoutValue,
            invalidFeedId,
            invalidDate,
            dateWithoutOffset,
            invalidBoolean,
            oversizedText
        ];
        Assert.All(invalidRules, rule =>
            Assert.Throws<InvalidDataException>(() =>
                FeedAutomationRuleValidator.ValidateAndNormalize(rule)));
    }

    [Fact]
    public void CompileRegexUsesBoundedNonBacktrackingEngine()
    {
        const string traditionallyCatastrophicPattern = "(a+)+$";

        Regex regex = FeedAutomationRuleValidator.CompileRegex(traditionallyCatastrophicPattern);

        Assert.True(regex.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.Equal(FeedAutomationRuleValidator.RegexTimeout, regex.MatchTimeout);
        Assert.Matches(regex, new string('a', 50_000));
    }

    [Fact]
    public void ValidateAndNormalizeRejectsUnsafeOrOversizedRegex()
    {
        FeedAutomationRule invalidSyntax = ValidRule(
            conditions:
            [
                new(FeedAutomationField.Title, FeedAutomationOperator.Regex, "[")
            ]);
        FeedAutomationRule backReference = ValidRule(
            conditions:
            [
                new(FeedAutomationField.Title, FeedAutomationOperator.Regex, @"(a+)\1")
            ]);
        FeedAutomationRule oversized = ValidRule(
            conditions:
            [
                new(
                    FeedAutomationField.Title,
                    FeedAutomationOperator.Regex,
                    new string('a', FeedAutomationRuleValidator.MaximumRegexPatternLength + 1))
            ]);

        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleValidator.ValidateAndNormalize(invalidSyntax));
        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleValidator.ValidateAndNormalize(backReference));
        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleValidator.ValidateAndNormalize(oversized));
    }

    [Fact]
    public void ValidateAndNormalizeRejectsActionPayloadInjection()
    {
        FeedAutomationRule notifyUrl = ValidRule(
            actions:
            [
                new(FeedAutomationActionType.Notify, 0, "https://example.com/hook")
            ]);
        FeedAutomationRule hiddenCommand = ValidRule(
            actions:
            [
                new(FeedAutomationActionType.Hide, 0, "DROP TABLE feed_entries")
            ]);
        FeedAutomationRule missingTag = ValidRule(
            actions:
            [
                new(FeedAutomationActionType.AddTag, 0, null)
            ]);
        FeedAutomationRule unsupportedLanguage = ValidRule(
            actions:
            [
                new(FeedAutomationActionType.Translate, 0, "not-a-language")
            ]);
        FeedAutomationRule controlCharacter = ValidRule(
            actions:
            [
                new(FeedAutomationActionType.AddTag, 0, "safe\r\ninjected")
            ]);

        FeedAutomationRule[] invalidRules =
        [
            notifyUrl,
            hiddenCommand,
            missingTag,
            unsupportedLanguage,
            controlCharacter
        ];
        Assert.All(invalidRules, rule =>
            Assert.Throws<InvalidDataException>(() =>
                FeedAutomationRuleValidator.ValidateAndNormalize(rule)));
    }

    [Fact]
    public void ValidateAndNormalizeCanonicalizesSupportedTranslationLanguage()
    {
        FeedAutomationRule rule = ValidRule(
            actions:
            [
                new(FeedAutomationActionType.Translate, 0, "ZH-hans")
            ]);

        FeedAutomationRule normalized = FeedAutomationRuleValidator.ValidateAndNormalize(rule);

        Assert.Equal("zh-Hans", normalized.Actions[0].Value);
    }

    [Fact]
    public void ValidateAndNormalizeAllowsMultipleTagsButRejectsDuplicateSingletonActions()
    {
        FeedAutomationRule multipleTags = ValidRule(
            actions:
            [
                new(FeedAutomationActionType.AddTag, 0, "AI"),
                new(FeedAutomationActionType.AddTag, 1, "Release")
            ]);
        FeedAutomationRule duplicateTranslations = ValidRule(
            actions:
            [
                new(FeedAutomationActionType.Translate, 0, "en"),
                new(FeedAutomationActionType.Translate, 1, "ja")
            ]);

        FeedAutomationRule normalized =
            FeedAutomationRuleValidator.ValidateAndNormalize(multipleTags);

        Assert.Equal(2, normalized.Actions.Count);
        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleValidator.ValidateAndNormalize(duplicateTranslations));
    }

    private static FeedAutomationRule ValidRule(
        IReadOnlyList<FeedAutomationCondition>? conditions = null,
        IReadOnlyList<FeedAutomationAction>? actions = null,
        FeedAutomationMatchMode matchMode = FeedAutomationMatchMode.All)
    {
        return new(
            RuleId,
            1,
            "AI release rule",
            100,
            0,
            true,
            matchMode,
            conditions
                ?? [new(FeedAutomationField.Title, FeedAutomationOperator.Contains, "release")],
            actions
                ?? [new(FeedAutomationActionType.AddTag, 0, "AI")]);
    }
}
