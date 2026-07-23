using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class EntryStateRepositoryTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools entry state repository tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PatchPreservesUntouchedFieldsAndRoundTripsAcrossProfiles()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new EntryStateRepository(database);

        EntryState first = await repository.PatchAsync(
            "entry-1",
            "default",
            new(IsRead: true, IsStarred: true, Progress: 42.5, Note: "keep"),
            CancellationToken.None);
        EntryState partial = await repository.PatchAsync(
            "entry-1",
            "default",
            new(IsRead: false),
            CancellationToken.None);
        EntryState otherProfile = await repository.PatchAsync(
            "entry-1",
            "work",
            new(IsStarred: true),
            CancellationToken.None);

        Assert.True(first.IsRead);
        Assert.True(first.IsStarred);
        Assert.Equal(42.5, first.Progress);
        Assert.Equal("keep", first.Note);
        Assert.False(partial.IsRead);
        Assert.True(partial.IsStarred);
        Assert.Equal(42.5, partial.Progress);
        Assert.Equal("keep", partial.Note);
        Assert.Equal("work", otherProfile.LocalProfile);
        Assert.False(otherProfile.IsRead);

        IReadOnlyDictionary<string, EntryState> states = await repository.GetAsync(
            ["entry-1", "missing"],
            "default",
            CancellationToken.None);

        Assert.Equal(partial, Assert.Single(states).Value);
    }

    [Fact]
    public async Task PatchRejectsInvalidBoundsAndOversizedPrivateData()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new EntryStateRepository(database);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.PatchAsync(
                "entry-1",
                "default",
                new(Progress: 100.1),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.PatchAsync(
                "entry-1",
                "default",
                new(Note: new string('x', 4001)),
                CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentPartialPatchesMergeWithoutDroppingIndependentFields()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new EntryStateRepository(database);
        await repository.PatchAsync(
            "entry-1",
            "default",
            new(Note: "keep this note"),
            CancellationToken.None);

        Task<EntryState>[] patches =
        [
            repository.PatchAsync(
                "entry-1",
                "default",
                new(IsRead: true),
                CancellationToken.None),
            repository.PatchAsync(
                "entry-1",
                "default",
                new(IsStarred: true),
                CancellationToken.None)
        ];

        EntryState[] results = await Task.WhenAll(patches);
        IReadOnlyDictionary<string, EntryState> states = await repository.GetAsync(
            ["entry-1"],
            "default",
            CancellationToken.None);

        EntryState final = Assert.Single(states).Value;
        Assert.True(final.IsRead);
        Assert.True(final.IsStarred);
        Assert.Equal("keep this note", final.Note);
        Assert.All(results, result => Assert.Equal("default", result.LocalProfile));
    }

    [Fact]
    public async Task PrivateStateSurvivesDatabaseReopen()
    {
        using (SqliteDatabase firstDatabase = await CreateDatabaseAsync())
        {
            var repository = new EntryStateRepository(firstDatabase);
            await repository.PatchAsync(
                "entry-restart",
                "default",
                new(IsRead: true, IsStarred: true, Progress: 87.5, Note: "resume"),
                CancellationToken.None);
        }

        using SqliteDatabase reopened = await CreateDatabaseAsync();
        var reopenedRepository = new EntryStateRepository(reopened);
        IReadOnlyDictionary<string, EntryState> states = await reopenedRepository.GetAsync(
            ["entry-restart"],
            "default",
            CancellationToken.None);

        EntryState state = Assert.Single(states).Value;
        Assert.True(state.IsRead);
        Assert.True(state.IsStarred);
        Assert.Equal(87.5, state.Progress);
        Assert.Equal("resume", state.Note);
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        return database;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }
}
