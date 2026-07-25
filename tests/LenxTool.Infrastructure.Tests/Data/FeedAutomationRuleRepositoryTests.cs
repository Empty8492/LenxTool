using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedAutomationRuleRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 16, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools feed automation rule repository tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReplaceRoundTripsNormalizedSnapshotAcrossRestart()
    {
        using (SqliteDatabase database = await CreateDatabaseAsync())
        {
            var repository = new FeedAutomationRuleRepository(database);
            await repository.ReplaceAsync(
                Snapshot(
                    3,
                    Rule(
                        "30000000-0000-4000-8000-000000000101",
                        "  Important AI  ",
                        priority: 900,
                        FeedAutomationActionType.MarkRead)),
                CancellationToken.None);
        }

        using SqliteDatabase reopened = await CreateDatabaseAsync();
        var reopenedRepository = new FeedAutomationRuleRepository(reopened);
        FeedAutomationRuleSnapshot stored =
            await reopenedRepository.GetAsync(CancellationToken.None);

        Assert.Equal(3, stored.RuleSetVersion);
        Assert.Equal(Now.AddMinutes(-1), stored.GeneratedAt);
        Assert.Equal(Now, stored.LastSyncedAt);
        FeedAutomationRule rule = Assert.Single(stored.Rules);
        Assert.Equal("Important AI", rule.Name);
        Assert.Equal(FeedAutomationActionType.MarkRead, Assert.Single(rule.Actions).Type);

        Assert.True(await reopenedRepository.MarkSynchronizedAsync(
            3,
            Now.AddMinutes(5),
            CancellationToken.None));
        Assert.False(await reopenedRepository.MarkSynchronizedAsync(
            2,
            Now.AddMinutes(6),
            CancellationToken.None));
        Assert.Equal(
            Now.AddMinutes(5),
            (await reopenedRepository.GetAsync(CancellationToken.None)).LastSyncedAt);
    }

    [Fact]
    public async Task ReplaceAcceptsEmptyVersionZeroSnapshot()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedAutomationRuleRepository(database);

        await repository.ReplaceAsync(
            new(
                0,
                GeneratedAt: null,
                LastSyncedAt: Now,
                Rules: Array.Empty<FeedAutomationRule>()),
            CancellationToken.None);

        FeedAutomationRuleSnapshot stored =
            await repository.GetAsync(CancellationToken.None);
        Assert.Equal(0, stored.RuleSetVersion);
        Assert.Null(stored.GeneratedAt);
        Assert.Equal(Now, stored.LastSyncedAt);
        Assert.Empty(stored.Rules);
    }

    [Fact]
    public async Task ReplaceRejectsStaleOrInvalidSnapshotWithoutChangingCache()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedAutomationRuleRepository(database);
        FeedAutomationRuleSnapshot current = Snapshot(
            2,
            Rule(
                "30000000-0000-4000-8000-000000000102",
                "Current",
                priority: 500,
                FeedAutomationActionType.Hide));
        await repository.ReplaceAsync(current, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReplaceAsync(
                Snapshot(
                    1,
                    Rule(
                        "30000000-0000-4000-8000-000000000103",
                        "Stale",
                        priority: 800,
                        FeedAutomationActionType.MarkRead)),
                CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => repository.ReplaceAsync(
                Snapshot(
                    3,
                    Rule(
                        "30000000-0000-4000-8000-000000000104",
                        "Disabled",
                        priority: 900,
                        FeedAutomationActionType.MarkRead) with
                    {
                        IsEnabled = false
                    }),
                CancellationToken.None));

        FeedAutomationRuleSnapshot preserved =
            await repository.GetAsync(CancellationToken.None);
        Assert.Equal(2, preserved.RuleSetVersion);
        Assert.Equal("Current", Assert.Single(preserved.Rules).Name);
    }

    [Fact]
    public async Task GetRejectsCorruptStoredRuleJson()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedAutomationRuleRepository(database);
        await repository.ReplaceAsync(
            Snapshot(
                1,
                Rule(
                    "30000000-0000-4000-8000-000000000105",
                    "Stored",
                    priority: 100,
                    FeedAutomationActionType.AddTag)),
            CancellationToken.None);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE feed_automation_rules SET rule_json='{not-json';";
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetRejectsSemanticallyInvalidStoredRule()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FeedAutomationRuleRepository(database);
        await repository.ReplaceAsync(
            Snapshot(
                1,
                Rule(
                    "30000000-0000-4000-8000-000000000106",
                    "Stored",
                    priority: 100,
                    FeedAutomationActionType.MarkRead)),
            CancellationToken.None);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE feed_automation_rules
            SET rule_json=replace(
                rule_json,
                '"isEnabled":true',
                '"isEnabled":false');
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        command.CommandText =
            "SELECT rule_json FROM feed_automation_rules;";
        Assert.Contains(
            "\"isEnabled\":false",
            (string)(await command.ExecuteScalarAsync(
                CancellationToken.None))!);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.GetAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        ClearTestPool();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        return database;
    }

    private void ClearTestPool()
    {
        AppPaths paths = new(_testRoot);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5
        };
        using var connection = new SqliteConnection(builder.ToString());
        SqliteConnection.ClearPool(connection);
    }

    private static FeedAutomationRuleSnapshot Snapshot(
        long version,
        params FeedAutomationRule[] rules) =>
        new(
            version,
            Now.AddMinutes(-1),
            Now,
            rules);

    private static FeedAutomationRule Rule(
        string id,
        string name,
        int priority,
        FeedAutomationActionType actionType) =>
        new(
            id,
            1,
            name,
            priority,
            0,
            true,
            FeedAutomationMatchMode.All,
            [
                new(
                    FeedAutomationField.Title,
                    FeedAutomationOperator.Contains,
                    "AI")
            ],
            [
                new(
                    actionType,
                    10,
                    actionType == FeedAutomationActionType.AddTag
                        ? "AI"
                        : null)
            ]);
}
