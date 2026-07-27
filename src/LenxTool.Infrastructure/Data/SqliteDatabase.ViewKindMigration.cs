namespace LenxTool.Infrastructure.Data;

public sealed partial class SqliteDatabase
{
    private const string MigrationEighteenSql = """
        ALTER TABLE feed_catalog
        ADD COLUMN view_kind_explicit INTEGER NOT NULL DEFAULT 0
            CHECK(typeof(view_kind_explicit) = 'integer' AND view_kind_explicit IN (0, 1));

        UPDATE feed_catalog
        SET view_kind_explicit = 1
        WHERE view_kind <> 'ARTICLE';
        """;
}
