using LenxTool.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FavoriteRepository(SqliteDatabase database) : IFavoriteRepository
{
    public async Task<int> GetCountAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM favorites;";
        long count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return checked((int)count);
    }
}
