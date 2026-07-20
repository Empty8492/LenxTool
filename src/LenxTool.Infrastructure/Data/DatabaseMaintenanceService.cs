using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class DatabaseMaintenanceService(
    AppPaths paths,
    SqliteDatabase database) : IDatabaseMaintenanceService
{
    public async Task<string> BackupAsync(string? destinationPath, CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        string destination = destinationPath ?? Path.Combine(
            paths.BackupDirectory,
            $"lenx-backup-{DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);

        await using SqliteConnection source = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var builder = new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate };
        await using var target = new SqliteConnection(builder.ToString());
        await target.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(target);
        return destination;
    }

    public async Task RestoreAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("找不到数据库备份。", sourcePath);

        await using (var validation = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString()))
        {
            await validation.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = validation.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            string result = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException(new(
                    AppErrorCode.DatabaseCorrupted, "备份文件已损坏",
                    "所选备份未通过 SQLite 完整性检查。", "请选择其他备份文件。",
                    result, "SQLite"));
            }
        }

        await BackupAsync(null, cancellationToken).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        string staging = paths.DatabasePath + ".restore";
        File.Copy(sourcePath, staging, overwrite: true);
        File.Move(staging, paths.DatabasePath, overwrite: true);
    }
}
