namespace LenxTool.Infrastructure.Data;

public sealed partial class SqliteDatabase
{
    private const string MigrationSeventeenSql = """
        DELETE FROM content_fts
        WHERE entity_type IN ('subtitle', 'favorite', 'tag');

        INSERT INTO content_fts(entity_type, entity_id, title, content)
        SELECT
            'subtitle',
            m.id,
            m.input_path,
            (
                SELECT group_concat(document.segment_text, char(10))
                FROM (
                    SELECT trim(
                        s.text || ' ' || COALESCE(s.translated_text, ''))
                        AS segment_text
                    FROM subtitle_segments s
                    WHERE s.media_job_id=m.id
                    ORDER BY s.sequence
                ) document
            )
        FROM media_jobs m
        WHERE EXISTS(
            SELECT 1
            FROM subtitle_segments s
            WHERE s.media_job_id=m.id);

        INSERT INTO content_fts(entity_type, entity_id, title, content)
        SELECT
            'favorite',
            id,
            '收藏 ' || entity_type,
            note
        FROM favorites;

        INSERT INTO content_fts(entity_type, entity_id, title, content)
        SELECT 'tag', id, name, color
        FROM tags;

        CREATE TRIGGER media_jobs_fts_delete
        AFTER DELETE ON media_jobs
        BEGIN
            DELETE FROM content_fts
            WHERE entity_type='subtitle' AND entity_id=OLD.id;
        END;

        CREATE TRIGGER favorites_fts_insert
        AFTER INSERT ON favorites
        BEGIN
            INSERT INTO content_fts(entity_type, entity_id, title, content)
            VALUES(
                'favorite',
                NEW.id,
                '收藏 ' || NEW.entity_type,
                NEW.note);
        END;

        CREATE TRIGGER favorites_fts_update
        AFTER UPDATE ON favorites
        BEGIN
            DELETE FROM content_fts
            WHERE entity_type='favorite' AND entity_id=OLD.id;
            INSERT INTO content_fts(entity_type, entity_id, title, content)
            VALUES(
                'favorite',
                NEW.id,
                '收藏 ' || NEW.entity_type,
                NEW.note);
        END;

        CREATE TRIGGER favorites_fts_delete
        AFTER DELETE ON favorites
        BEGIN
            DELETE FROM content_fts
            WHERE entity_type='favorite' AND entity_id=OLD.id;
        END;

        CREATE TRIGGER tags_fts_insert
        AFTER INSERT ON tags
        BEGIN
            INSERT INTO content_fts(entity_type, entity_id, title, content)
            VALUES('tag', NEW.id, NEW.name, NEW.color);
        END;

        CREATE TRIGGER tags_fts_update
        AFTER UPDATE ON tags
        BEGIN
            DELETE FROM content_fts
            WHERE entity_type='tag' AND entity_id=OLD.id;
            INSERT INTO content_fts(entity_type, entity_id, title, content)
            VALUES('tag', NEW.id, NEW.name, NEW.color);
        END;

        CREATE TRIGGER tags_fts_delete
        AFTER DELETE ON tags
        BEGIN
            DELETE FROM content_fts
            WHERE entity_type='tag' AND entity_id=OLD.id;
        END;
        """;
}
