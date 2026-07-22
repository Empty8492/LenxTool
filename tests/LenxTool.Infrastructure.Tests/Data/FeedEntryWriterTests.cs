using System.Text;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedEntryWriterTests : IDisposable
{
    private const string FeedId = "30000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools feed entry writer tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RepeatedExternalIdUpdatesEntryWithoutCreatingDuplicate()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var writer = new FeedEntryWriter(database);
        FeedEntry original = ParseEntry("Original", "body", "https://cdn.example/one.mp3");
        FeedEntry updated = ParseEntry("Updated", "new body", "https://cdn.example/two.mp3");

        await writer.UpsertAsync(FeedId, [original], CancellationToken.None);
        await writer.UpsertAsync(FeedId, [updated], CancellationToken.None);

        await using SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), title, sanitized_content, enclosure_json FROM feed_entries;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal("Updated", reader.GetString(1));
        Assert.Equal("new body", reader.GetString(2));
        Assert.Contains("two.mp3", reader.GetString(3), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchRollsBackWhenAnyEntryFails()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        await using (SqliteConnection connection = await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_feed_entry
                BEFORE INSERT ON feed_entries
                WHEN NEW.title='Force failure'
                BEGIN
                    SELECT RAISE(ABORT, 'forced failure');
                END;
                """;
            await trigger.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var parser = new FeedDocumentParser();
        const string xml = "<rss version='2.0'><channel><title>x</title><item><guid>one</guid><title>First</title></item><item><guid>two</guid><title>Force failure</title></item></channel></rss>";
        IReadOnlyList<FeedEntry> entries = parser.Parse(
            FeedId,
            "https://feeds.example/feed.xml",
            Encoding.UTF8.GetBytes(xml),
            Now).Entries;

        await Assert.ThrowsAsync<SqliteException>(
            () => new FeedEntryWriter(database).UpsertAsync(FeedId, entries, CancellationToken.None));

        await using SqliteConnection verification = await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand count = verification.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM feed_entries;";
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(CancellationToken.None))!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        return database;
    }

    private static FeedEntry ParseEntry(string title, string body, string enclosureUrl)
    {
        string xml = $"<rss version='2.0'><channel><title>x</title><item><guid>stable</guid><title>{title}</title><description>{body}</description><enclosure url='{enclosureUrl}' type='audio/mpeg' length='42'/></item></channel></rss>";
        return Assert.Single(new FeedDocumentParser().Parse(
            FeedId,
            "https://feeds.example/feed.xml",
            Encoding.UTF8.GetBytes(xml),
            Now).Entries);
    }
}
