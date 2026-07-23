using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FavoriteRepositoryTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools favorite repository tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetCountReturnsAllPrivateFavorites()
    {
        using SqliteDatabase database = new(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        await using SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO favorites(id, entity_type, entity_id, created_at)
            VALUES('favorite-1', 'feed_entry', 'entry-1', $created),
                  ('favorite-2', 'news', 'news-1', $created);
            """;
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        int count = await new FavoriteRepository(database).GetCountAsync(CancellationToken.None);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task UpsertUpdatesPrivateNoteAndRemoveDeletesOnlyTheFavorite()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FavoriteRepository(database);

        FavoriteItem first = await repository.UpsertAsync(
            "feed_entry",
            "entry-1",
            "稍后精读",
            CancellationToken.None);
        FavoriteItem updated = await repository.UpsertAsync(
            "feed_entry",
            "entry-1",
            "已读完",
            CancellationToken.None);

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("已读完", updated.Note);
        Assert.Equal(updated, await repository.GetAsync(
            "feed_entry",
            "entry-1",
            CancellationToken.None));
        await repository.UpsertAsync(
            "feed_entry",
            "entry-2",
            string.Empty,
            CancellationToken.None);
        IReadOnlyDictionary<string, FavoriteItem> batch = await repository.GetForEntitiesAsync(
            "feed_entry",
            ["entry-1", "entry-2", "missing"],
            CancellationToken.None);
        Assert.Equal(["entry-1", "entry-2"], batch.Keys.Order().ToArray());
        Assert.True(await repository.RemoveAsync(
            "feed_entry",
            "entry-1",
            CancellationToken.None));
        Assert.Null(await repository.GetAsync(
            "feed_entry",
            "entry-1",
            CancellationToken.None));
    }

    [Fact]
    public async Task TagsNormalizeNamesAndDeletingTagKeepsFavoriteNote()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FavoriteRepository(database);
        FavoriteItem favorite = await repository.UpsertAsync(
            "news",
            "news-1",
            "保留这条私人备注",
            CancellationToken.None);

        TagItem first = await repository.UpsertTagAsync(
            "  Ｒｅａｄ later  ",
            "blue",
            CancellationToken.None);
        TagItem same = await repository.UpsertTagAsync(
            "read later",
            "red",
            CancellationToken.None);
        TagItem second = await repository.UpsertTagAsync(
            "重点",
            "orange",
            CancellationToken.None);

        Assert.Equal(first.Id, same.Id);
        Assert.Equal("Read later", same.Name);
        Assert.Equal("red", same.Color);

        await repository.SetTagsAsync(
            favorite.EntityType,
            favorite.EntityId,
            [same.Id, second.Id],
            CancellationToken.None);
        Assert.Equal(
            [same.Id, second.Id],
            (await repository.GetTagsForEntityAsync(
                favorite.EntityType,
                favorite.EntityId,
                CancellationToken.None))
            .Select(tag => tag.Id)
            .ToArray());

        Assert.True(await repository.DeleteTagAsync(
            same.Id,
            CancellationToken.None));
        Assert.Equal(
            "保留这条私人备注",
            (await repository.GetAsync(
                favorite.EntityType,
                favorite.EntityId,
                CancellationToken.None))?.Note);
        Assert.Equal(
            [second.Id],
            (await repository.GetTagsForEntityAsync(
                favorite.EntityType,
                favorite.EntityId,
                CancellationToken.None))
            .Select(tag => tag.Id)
            .ToArray());
    }

    [Fact]
    public async Task SetTagsRejectsUnknownTagAndTooManyTags()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new FavoriteRepository(database);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.SetTagsAsync(
                "trend",
                "trend-1",
                ["missing-tag"],
                CancellationToken.None));

        string[] tagIds = new string[51];
        for (int index = 0; index < tagIds.Length; index++)
        {
            tagIds[index] = $"tag-{index}";
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.SetTagsAsync(
                "trend",
                "trend-1",
                tagIds,
                CancellationToken.None));
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
