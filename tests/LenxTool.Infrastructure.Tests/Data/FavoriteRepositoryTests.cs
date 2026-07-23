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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }
}
