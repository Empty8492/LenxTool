using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Feeds;

public sealed class FeedAutomationRuleInterpreterTests
{
    private const string FeedId = "20000000-0000-4000-8000-000000000015";
    private const string CategoryId = "10000000-0000-4000-8000-000000000015";

    [Fact]
    public void PlanMatchesAllAndAnyRulesAcrossEverySupportedField()
    {
        FeedAutomationRule all = Rule(
            "30000000-0000-4000-8000-000000000015",
            200,
            0,
            FeedAutomationMatchMode.All,
            conditions:
            [
                new(FeedAutomationField.Feed, FeedAutomationOperator.Equals, FeedId),
                new(FeedAutomationField.Category, FeedAutomationOperator.Equals, CategoryId),
                new(FeedAutomationField.Title, FeedAutomationOperator.Contains, "release notes"),
                new(FeedAutomationField.Author, FeedAutomationOperator.Regex, "^Ali"),
                new(FeedAutomationField.Content, FeedAutomationOperator.Contains, "agent"),
                new(FeedAutomationField.Language, FeedAutomationOperator.Equals, "EN"),
                new(
                    FeedAutomationField.PublishedAt,
                    FeedAutomationOperator.After,
                    "2026-07-24T23:59:59+00:00"),
                new(
                    FeedAutomationField.PublishedAt,
                    FeedAutomationOperator.Before,
                    "2026-07-26T00:00:00+00:00"),
                new(FeedAutomationField.HasAudio, FeedAutomationOperator.Equals, "false"),
                new(FeedAutomationField.HasVideo, FeedAutomationOperator.Exists, null)
            ]);
        FeedAutomationRule any = Rule(
            "30000000-0000-4000-8000-000000000016",
            100,
            0,
            FeedAutomationMatchMode.Any,
            conditions:
            [
                new(FeedAutomationField.Title, FeedAutomationOperator.Equals, "does not match"),
                new(FeedAutomationField.Author, FeedAutomationOperator.Exists, null)
            ],
            actions: [new(FeedAutomationActionType.MarkRead, 0, null)]);
        FeedAutomationRule missed = Rule(
            "30000000-0000-4000-8000-000000000017",
            50,
            0,
            FeedAutomationMatchMode.All,
            conditions:
            [
                new(FeedAutomationField.Content, FeedAutomationOperator.Regex, "^unrelated$")
            ]);
        FeedAutomationRule disabled = Rule(
            "30000000-0000-4000-8000-000000000018",
            500,
            0,
            FeedAutomationMatchMode.All,
            isEnabled: false);
        FeedAutomationRuleSet ruleSet =
            FeedAutomationRuleInterpreter.Compile([missed, disabled, any, all]);

        FeedAutomationPlan plan = ruleSet.Plan(Context());

        Assert.Equal(
            [
                FeedAutomationRuleEvaluationOutcome.Disabled,
                FeedAutomationRuleEvaluationOutcome.Matched,
                FeedAutomationRuleEvaluationOutcome.Matched,
                FeedAutomationRuleEvaluationOutcome.NotMatched
            ],
            plan.RuleEvaluations.Select(evaluation => evaluation.Outcome));
        Assert.Equal(
            [
                "30000000-0000-4000-8000-000000000015",
                "30000000-0000-4000-8000-000000000016"
            ],
            plan.Actions
                .Where(action => action.Disposition == FeedAutomationActionDisposition.Planned)
                .Select(action => action.RuleId));
    }

    [Fact]
    public void ExistsUsesValuePresenceAndMediaAvailability()
    {
        FeedAutomationEntryContext context = Context() with
        {
            CategoryId = null,
            Author = null,
            Content = " ",
            PublishedAt = null,
            HasAudio = false,
            HasVideo = true
        };
        FeedAutomationRule[] rules =
        [
            ExistsRule("30000000-0000-4000-8000-000000000021", FeedAutomationField.Category),
            ExistsRule("30000000-0000-4000-8000-000000000022", FeedAutomationField.Author),
            ExistsRule("30000000-0000-4000-8000-000000000023", FeedAutomationField.Content),
            ExistsRule("30000000-0000-4000-8000-000000000024", FeedAutomationField.PublishedAt),
            ExistsRule("30000000-0000-4000-8000-000000000025", FeedAutomationField.HasAudio),
            ExistsRule("30000000-0000-4000-8000-000000000026", FeedAutomationField.HasVideo)
        ];

        FeedAutomationPlan plan = FeedAutomationRuleInterpreter.Compile(rules).Plan(context);

        Assert.Equal(
            [
                FeedAutomationRuleEvaluationOutcome.NotMatched,
                FeedAutomationRuleEvaluationOutcome.NotMatched,
                FeedAutomationRuleEvaluationOutcome.NotMatched,
                FeedAutomationRuleEvaluationOutcome.NotMatched,
                FeedAutomationRuleEvaluationOutcome.NotMatched,
                FeedAutomationRuleEvaluationOutcome.Matched
            ],
            plan.RuleEvaluations.Select(evaluation => evaluation.Outcome));
    }

    [Fact]
    public void PlanResolvesConflictsByPriorityConflictOrderAndActionOrder()
    {
        FeedAutomationRule lower = Rule(
            "30000000-0000-4000-8000-000000000032",
            200,
            0,
            FeedAutomationMatchMode.All,
            actions:
            [
                new(FeedAutomationActionType.GenerateSummary, 5, null),
                new(FeedAutomationActionType.Translate, 10, "ko"),
                new(FeedAutomationActionType.AddTag, 20, "Release")
            ]);
        FeedAutomationRule samePriorityLater = Rule(
            "30000000-0000-4000-8000-000000000033",
            300,
            99,
            FeedAutomationMatchMode.All,
            actions:
            [
                new(FeedAutomationActionType.Translate, 20, "en"),
                new(FeedAutomationActionType.Notify, 30, null),
                new(FeedAutomationActionType.AddTag, 40, "AI")
            ]);
        FeedAutomationRule winner = Rule(
            "30000000-0000-4000-8000-000000000031",
            300,
            1,
            FeedAutomationMatchMode.All,
            actions:
            [
                new(FeedAutomationActionType.Hide, 10, null),
                new(FeedAutomationActionType.Translate, 20, "ja"),
                new(FeedAutomationActionType.AddTag, 30, "ai")
            ]);

        FeedAutomationPlan plan =
            FeedAutomationRuleInterpreter.Compile([lower, samePriorityLater, winner]).Plan(Context());

        Assert.Equal(
            [
                FeedAutomationActionType.Hide,
                FeedAutomationActionType.Translate,
                FeedAutomationActionType.AddTag,
                FeedAutomationActionType.Translate,
                FeedAutomationActionType.Notify,
                FeedAutomationActionType.AddTag,
                FeedAutomationActionType.GenerateSummary,
                FeedAutomationActionType.Translate,
                FeedAutomationActionType.AddTag
            ],
            plan.Actions.Select(action => action.Type));
        Assert.Equal(
            [
                FeedAutomationActionDisposition.Planned,
                FeedAutomationActionDisposition.Planned,
                FeedAutomationActionDisposition.Planned,
                FeedAutomationActionDisposition.Suppressed,
                FeedAutomationActionDisposition.Planned,
                FeedAutomationActionDisposition.Suppressed,
                FeedAutomationActionDisposition.Planned,
                FeedAutomationActionDisposition.Suppressed,
                FeedAutomationActionDisposition.Planned
            ],
            plan.Actions.Select(action => action.Disposition));
        FeedAutomationActionDecision[] suppressed = plan.Actions
            .Where(action => action.Disposition == FeedAutomationActionDisposition.Suppressed)
            .ToArray();
        Assert.All(suppressed, action =>
            Assert.Equal("30000000-0000-4000-8000-000000000031", action.WinningRuleId));
        Assert.Equal(
            [
                FeedAutomationActionSuppressionReason.DuplicateSingleton,
                FeedAutomationActionSuppressionReason.DuplicateTag,
                FeedAutomationActionSuppressionReason.DuplicateSingleton
            ],
            suppressed.Select(action => action.SuppressionReason));
    }

    [Fact]
    public void CompiledRuleSetIsDeterministicAndDetachedFromSourceCollections()
    {
        FeedAutomationCondition[] conditions =
        [
            new(FeedAutomationField.Title, FeedAutomationOperator.Contains, "release")
        ];
        FeedAutomationAction[] actions =
        [
            new(FeedAutomationActionType.AddTag, 5, "AI"),
            new(FeedAutomationActionType.MarkRead, 10, null)
        ];
        FeedAutomationRule first = Rule(
            "30000000-0000-4000-8000-000000000041",
            100,
            0,
            FeedAutomationMatchMode.All,
            conditions: conditions,
            actions: actions);
        FeedAutomationRule second = Rule(
            "30000000-0000-4000-8000-000000000042",
            200,
            0,
            FeedAutomationMatchMode.All);
        FeedAutomationRuleSet ascending = FeedAutomationRuleInterpreter.Compile([first, second]);
        FeedAutomationRuleSet descending = FeedAutomationRuleInterpreter.Compile([second, first]);

        conditions[0] = new(FeedAutomationField.Title, FeedAutomationOperator.Equals, "changed");
        actions[0] = new(FeedAutomationActionType.Notify, 5, null);
        FeedAutomationPlan left = ascending.Plan(Context());
        FeedAutomationPlan right = descending.Plan(Context());

        Assert.Equal(left.RuleEvaluations.ToArray(), right.RuleEvaluations.ToArray());
        Assert.Equal(left.Actions.ToArray(), right.Actions.ToArray());
        Assert.Contains(
            left.Actions,
            action => action.Type == FeedAutomationActionType.AddTag && action.Value == "AI");
    }

    [Fact]
    public void RuleIdBreaksExactPriorityAndConflictOrderTies()
    {
        FeedAutomationRule laterId = Rule(
            "30000000-0000-4000-8000-000000000072",
            100,
            5,
            FeedAutomationMatchMode.All,
            actions: [new(FeedAutomationActionType.Translate, 0, "en")]);
        FeedAutomationRule earlierId = Rule(
            "30000000-0000-4000-8000-000000000071",
            100,
            5,
            FeedAutomationMatchMode.All,
            actions: [new(FeedAutomationActionType.Translate, 0, "ja")]);

        FeedAutomationPlan plan =
            FeedAutomationRuleInterpreter.Compile([laterId, earlierId]).Plan(Context());

        Assert.Equal(
            [
                "30000000-0000-4000-8000-000000000071",
                "30000000-0000-4000-8000-000000000072"
            ],
            plan.RuleEvaluations.Select(evaluation => evaluation.RuleId));
        Assert.Equal(
            [
                FeedAutomationActionDisposition.Planned,
                FeedAutomationActionDisposition.Suppressed
            ],
            plan.Actions.Select(action => action.Disposition));
        Assert.Equal(
            "30000000-0000-4000-8000-000000000071",
            plan.Actions[1].WinningRuleId);
    }

    [Fact]
    public void PlanUsesCompiledNonBacktrackingRegexForLargeInput()
    {
        FeedAutomationRule rule = Rule(
            "30000000-0000-4000-8000-000000000051",
            100,
            0,
            FeedAutomationMatchMode.All,
            conditions:
            [
                new(FeedAutomationField.Content, FeedAutomationOperator.Regex, "(a+)+$")
            ]);
        FeedAutomationRuleSet ruleSet = FeedAutomationRuleInterpreter.Compile([rule]);
        FeedAutomationEntryContext context = Context() with { Content = new string('a', 50_000) };

        FeedAutomationPlan plan = ruleSet.Plan(context);

        Assert.Equal(
            FeedAutomationRuleEvaluationOutcome.Matched,
            Assert.Single(plan.RuleEvaluations).Outcome);
    }

    [Fact]
    public void CompileAndPlanRejectAmbiguousOrOversizedInputs()
    {
        FeedAutomationRule duplicate = Rule(
            "30000000-0000-4000-8000-000000000061",
            100,
            0,
            FeedAutomationMatchMode.All);
        FeedAutomationRule[] tooMany = Enumerable.Range(
                0,
                FeedAutomationRuleInterpreter.MaximumRuleCount + 1)
            .Select(index => Rule(
                $"30000000-0000-4000-8000-{index:D12}",
                100,
                index,
                FeedAutomationMatchMode.All))
            .ToArray();
        FeedAutomationRule invalid = duplicate with { Version = 0 };
        FeedAutomationRuleSet validSet = FeedAutomationRuleInterpreter.Compile([duplicate]);

        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleInterpreter.Compile([duplicate, duplicate]));
        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleInterpreter.Compile(tooMany));
        Assert.Throws<InvalidDataException>(() =>
            FeedAutomationRuleInterpreter.Compile([invalid]));
        Assert.Throws<InvalidDataException>(() =>
            validSet.Plan(Context() with { FeedId = "not-a-feed-id" }));
        Assert.Throws<InvalidDataException>(() =>
            validSet.Plan(Context() with
            {
                Content = new string(
                    'x',
                    FeedAutomationRuleInterpreter.MaximumContentLength + 1)
            }));
    }

    private static FeedAutomationRule ExistsRule(string id, FeedAutomationField field) =>
        Rule(
            id,
            100,
            int.Parse(id[^2..], System.Globalization.CultureInfo.InvariantCulture),
            FeedAutomationMatchMode.All,
            conditions: [new(field, FeedAutomationOperator.Exists, null)]);

    private static FeedAutomationEntryContext Context() =>
        new(
            "entry-1",
            FeedId,
            CategoryId,
            "OpenAI Release Notes",
            "Alice",
            "A new agent capability is available.",
            "en",
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero),
            HasAudio: false,
            HasVideo: true);

    private static FeedAutomationRule Rule(
        string id,
        int priority,
        int conflictOrder,
        FeedAutomationMatchMode matchMode,
        IReadOnlyList<FeedAutomationCondition>? conditions = null,
        IReadOnlyList<FeedAutomationAction>? actions = null,
        bool isEnabled = true) =>
        new(
            id,
            1,
            $"Rule {id[^2..]}",
            priority,
            conflictOrder,
            isEnabled,
            matchMode,
            conditions
                ?? [new(FeedAutomationField.Title, FeedAutomationOperator.Exists, null)],
            actions
                ?? [new(FeedAutomationActionType.Notify, 0, null)]);
}
