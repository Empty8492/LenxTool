using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedAutomationPlanningServiceTests
{
    private const string FeedId =
        "30000000-0000-4000-8000-000000000301";
    private const string CategoryId =
        "20000000-0000-4000-8000-000000000301";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StagesMatchingPlanFromPersistedEntryContext()
    {
        var rules = new FakeRuleRepository(
            Snapshot(
                Rule(
                    [
                        new(
                            FeedAutomationField.Category,
                            FeedAutomationOperator.Equals,
                            CategoryId),
                        new(
                            FeedAutomationField.Content,
                            FeedAutomationOperator.Contains,
                            "launch"),
                        new(
                            FeedAutomationField.HasAudio,
                            FeedAutomationOperator.Equals,
                            "true")
                    ])));
        var runs = new FakeRunRepository();
        var service = new FeedAutomationPlanningService(
            rules,
            runs,
            new FixedTimeProvider(Now));
        FeedEntry entry = Entry(
            sanitizedContent: string.Empty,
            summary: "Major AI launch today",
            enclosures:
            [
                new(
                    "https://media.example/episode.mp3",
                    "audio/mpeg",
                    1024,
                    "Episode")
            ]);

        FeedAutomationPlanningResult result = await service.StageAsync(
            Feed(),
            [entry],
            CancellationToken.None);

        Assert.Equal(4, result.RuleSetVersion);
        Assert.Equal(1, result.EntriesEvaluated);
        Assert.Equal(1, result.RuleRunsCreated);
        Assert.Equal(1, result.ActionRunsCreated);
        FeedAutomationPlan plan = Assert.Single(runs.Plans);
        Assert.Equal(entry.Id, plan.EntryId);
        Assert.Equal(
            FeedAutomationRuleEvaluationOutcome.Matched,
            Assert.Single(plan.RuleEvaluations).Outcome);
        FeedAutomationActionDecision action =
            Assert.Single(plan.Actions);
        Assert.Equal(FeedAutomationActionType.Hide, action.Type);
        Assert.Equal(
            FeedAutomationActionDisposition.Planned,
            action.Disposition);
        Assert.Equal(Now, Assert.Single(runs.StagedAt));
    }

    [Fact]
    public async Task EmptyActiveRuleSnapshotSkipsRunRepository()
    {
        var rules = new FakeRuleRepository(
            new(
                0,
                GeneratedAt: null,
                LastSyncedAt: Now,
                Rules: Array.Empty<FeedAutomationRule>()));
        var runs = new FakeRunRepository();
        var service = new FeedAutomationPlanningService(
            rules,
            runs,
            new FixedTimeProvider(Now));

        FeedAutomationPlanningResult result = await service.StageAsync(
            Feed(),
            [Entry()],
            CancellationToken.None);

        Assert.Equal(0, result.RuleSetVersion);
        Assert.Equal(0, result.EntriesEvaluated);
        Assert.Equal(0, result.RuleRunsCreated);
        Assert.Equal(0, result.ActionRunsCreated);
        Assert.Empty(runs.Plans);
    }

    [Fact]
    public async Task MismatchedFeedEntryIsRejectedBeforeAnyPlanIsStaged()
    {
        var rules = new FakeRuleRepository(
            Snapshot(Rule([])));
        var runs = new FakeRunRepository();
        var service = new FeedAutomationPlanningService(
            rules,
            runs,
            new FixedTimeProvider(Now));
        FeedEntry mismatched = Entry() with
        {
            FeedId = "30000000-0000-4000-8000-000000000399"
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.StageAsync(
                Feed(),
                [Entry(), mismatched],
                CancellationToken.None));

        Assert.Empty(runs.Plans);
    }

    [Fact]
    public async Task OversizedContentIsSafelyBoundedBeforeEvaluation()
    {
        var rules = new FakeRuleRepository(
            Snapshot(
                Rule(
                    [
                        new(
                            FeedAutomationField.Content,
                            FeedAutomationOperator.Contains,
                            "tail-marker")
                    ])));
        var runs = new FakeRunRepository();
        var service = new FeedAutomationPlanningService(
            rules,
            runs,
            new FixedTimeProvider(Now));
        string oversized =
            new string('a', 100_000) + "tail-marker";

        FeedAutomationPlanningResult result = await service.StageAsync(
            Feed(),
            [Entry(sanitizedContent: oversized)],
            CancellationToken.None);

        Assert.Equal(1, result.EntriesEvaluated);
        Assert.Equal(
            FeedAutomationRuleEvaluationOutcome.NotMatched,
            Assert.Single(
                Assert.Single(runs.Plans).RuleEvaluations).Outcome);
        Assert.Empty(Assert.Single(runs.Plans).Actions);
    }

    [Fact]
    public async Task AutomaticAudioViewDoesNotOverrideAttachmentClassification()
    {
        var rules = new FakeRuleRepository(
            Snapshot(
                Rule(
                    [
                        new(
                            FeedAutomationField.HasAudio,
                            FeedAutomationOperator.Equals,
                            "true")
                    ])));
        var automaticRuns = new FakeRunRepository();
        var automaticService = new FeedAutomationPlanningService(
            rules,
            automaticRuns,
            new FixedTimeProvider(Now));
        var explicitRuns = new FakeRunRepository();
        var explicitService = new FeedAutomationPlanningService(
            rules,
            explicitRuns,
            new FixedTimeProvider(Now));

        await automaticService.StageAsync(
            Feed(FeedViewKind.Audio, isViewKindExplicit: false),
            [Entry()],
            CancellationToken.None);
        await explicitService.StageAsync(
            Feed(FeedViewKind.Audio, isViewKindExplicit: true),
            [Entry()],
            CancellationToken.None);

        Assert.Equal(
            FeedAutomationRuleEvaluationOutcome.NotMatched,
            Assert.Single(Assert.Single(automaticRuns.Plans).RuleEvaluations).Outcome);
        Assert.Equal(
            FeedAutomationRuleEvaluationOutcome.Matched,
            Assert.Single(Assert.Single(explicitRuns.Plans).RuleEvaluations).Outcome);
    }

    private static FeedAutomationRuleSnapshot Snapshot(
        params FeedAutomationRule[] rules) => new(
        4,
        Now.AddMinutes(-10),
        Now.AddMinutes(-5),
        rules);

    private static FeedAutomationRule Rule(
        IReadOnlyList<FeedAutomationCondition> conditions) => new(
        "40000000-0000-4000-8000-000000000301",
        2,
        "Hide matching entry",
        500,
        10,
        true,
        FeedAutomationMatchMode.All,
        conditions.Count == 0
            ? [
                new(
                    FeedAutomationField.Title,
                    FeedAutomationOperator.Contains,
                    "Entry")
            ]
            : conditions,
        [
            new(
                FeedAutomationActionType.Hide,
                10,
                null)
        ]);

    private static FeedCatalogItem Feed(
        FeedViewKind viewKind = FeedViewKind.Article,
        bool isViewKindExplicit = false) => new(
        FeedId,
        "https://feeds.example/daily.xml",
        "https://feeds.example/daily.xml",
        "Daily",
        "https://feeds.example/",
        CategoryId,
        viewKind,
        60,
        10,
        true,
        1,
        Now.AddDays(-1),
        Now.AddDays(-1),
        IsViewKindExplicit: isViewKindExplicit);

    private static FeedEntry Entry(
        string sanitizedContent = "Entry content",
        string summary = "Summary",
        IReadOnlyList<FeedEnclosure>? enclosures = null) => new(
        "50000000-0000-4000-8000-000000000301",
        FeedId,
        "external-301",
        "https://feeds.example/entry",
        "Entry",
        "Author",
        Now.AddMinutes(-30),
        Now.AddMinutes(-20),
        summary,
        sanitizedContent,
        [],
        enclosures ?? [],
        new string('a', 64),
        Now);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeRuleRepository(
        FeedAutomationRuleSnapshot snapshot)
        : IFeedAutomationRuleRepository
    {
        public Task<FeedAutomationRuleSnapshot> GetAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);

        public Task ReplaceAsync(
            FeedAutomationRuleSnapshot next,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> MarkSynchronizedAsync(
            long expectedRuleSetVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRunRepository
        : IFeedAutomationRunRepository
    {
        public List<FeedAutomationPlan> Plans { get; } = [];
        public List<DateTimeOffset> StagedAt { get; } = [];

        public Task<FeedAutomationStageResult> StageAsync(
            FeedAutomationPlan plan,
            DateTimeOffset stagedAt,
            CancellationToken cancellationToken)
        {
            Plans.Add(plan);
            StagedAt.Add(stagedAt);
            return Task.FromResult(new FeedAutomationStageResult(
                plan.RuleEvaluations.Count,
                plan.Actions.Count));
        }

        public Task<FeedAutomationRunSnapshot> GetAsync(
            string entryId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
