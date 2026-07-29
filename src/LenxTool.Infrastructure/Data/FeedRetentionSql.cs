namespace LenxTool.Infrastructure.Data;

internal static class FeedRetentionSql
{
    internal const string CandidateWhereClause = """
        julianday(COALESCE(e.updated_at, e.published_at, e.fetched_at))
            < julianday($cutoff)
        AND NOT EXISTS (
            SELECT 1 FROM favorites private_favorite
            WHERE private_favorite.entity_type='feed_entry'
              AND private_favorite.entity_id=e.id)
        AND NOT EXISTS (
            SELECT 1 FROM entity_tags private_tag
            WHERE private_tag.entity_type='feed_entry'
              AND private_tag.entity_id=e.id)
        AND NOT EXISTS (
            SELECT 1 FROM user_entry_states private_state
            WHERE private_state.entry_id=e.id)
        AND NOT EXISTS (
            SELECT 1 FROM feed_full_text_jobs full_text_job
            WHERE full_text_job.entry_id=e.id
              AND full_text_job.status IN (
                  'PENDING', 'IN_PROGRESS', 'RETRY'))
        AND NOT EXISTS (
            SELECT 1 FROM feed_ai_automation_jobs ai_job
            WHERE ai_job.entry_id=e.id
              AND ai_job.status IN ('PENDING', 'RUNNING', 'RETRY'))
        AND NOT EXISTS (
            SELECT 1 FROM feed_automation_action_runs action_run
            WHERE action_run.entry_id=e.id
              AND action_run.status IN ('PENDING', 'RUNNING', 'RETRY'))
        AND NOT EXISTS (
            SELECT 1
            FROM feed_media_deliveries delivery
            JOIN media_jobs media_job ON media_job.id=delivery.media_job_id
            WHERE delivery.entry_id=e.id
              AND media_job.status IN ('Queued', 'Running'))
        AND NOT EXISTS (
            SELECT 1 FROM entry_export_tasks export_task
            WHERE export_task.entry_id=e.id
              AND export_task.status IN ('QUEUED', 'RUNNING'))
        """;
}
