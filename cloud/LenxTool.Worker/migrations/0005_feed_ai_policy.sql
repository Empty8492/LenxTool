ALTER TABLE feed_categories
  ADD COLUMN ai_manual_summary_policy TEXT NOT NULL DEFAULT 'INHERIT'
  CHECK(ai_manual_summary_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));

ALTER TABLE feed_categories
  ADD COLUMN ai_auto_summary_policy TEXT NOT NULL DEFAULT 'INHERIT'
  CHECK(ai_auto_summary_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));

ALTER TABLE feed_categories
  ADD COLUMN ai_auto_translation_policy TEXT NOT NULL DEFAULT 'INHERIT'
  CHECK(ai_auto_translation_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));

ALTER TABLE feed_categories
  ADD COLUMN ai_translation_target_language TEXT
  CHECK(ai_translation_target_language IS NULL OR ai_translation_target_language IN ('zh-Hans', 'en', 'ja', 'ko'));

ALTER TABLE feed_categories
  ADD COLUMN ai_daily_entry_limit INTEGER
  CHECK(ai_daily_entry_limit IS NULL OR
    (typeof(ai_daily_entry_limit) = 'integer' AND ai_daily_entry_limit BETWEEN 1 AND 1000));

ALTER TABLE feed_categories
  ADD COLUMN ai_max_concurrency INTEGER
  CHECK(ai_max_concurrency IS NULL OR
    (typeof(ai_max_concurrency) = 'integer' AND ai_max_concurrency BETWEEN 1 AND 4));

ALTER TABLE managed_feeds
  ADD COLUMN ai_manual_summary_policy TEXT NOT NULL DEFAULT 'INHERIT'
  CHECK(ai_manual_summary_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));

ALTER TABLE managed_feeds
  ADD COLUMN ai_auto_summary_policy TEXT NOT NULL DEFAULT 'INHERIT'
  CHECK(ai_auto_summary_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));

ALTER TABLE managed_feeds
  ADD COLUMN ai_auto_translation_policy TEXT NOT NULL DEFAULT 'INHERIT'
  CHECK(ai_auto_translation_policy IN ('INHERIT', 'ENABLED', 'DISABLED'));

ALTER TABLE managed_feeds
  ADD COLUMN ai_translation_target_language TEXT
  CHECK(ai_translation_target_language IS NULL OR ai_translation_target_language IN ('zh-Hans', 'en', 'ja', 'ko'));

ALTER TABLE managed_feeds
  ADD COLUMN ai_daily_entry_limit INTEGER
  CHECK(ai_daily_entry_limit IS NULL OR
    (typeof(ai_daily_entry_limit) = 'integer' AND ai_daily_entry_limit BETWEEN 1 AND 1000));

ALTER TABLE managed_feeds
  ADD COLUMN ai_max_concurrency INTEGER
  CHECK(ai_max_concurrency IS NULL OR
    (typeof(ai_max_concurrency) = 'integer' AND ai_max_concurrency BETWEEN 1 AND 4));

CREATE INDEX ix_feed_categories_ai_automation
  ON feed_categories(ai_auto_summary_policy, ai_auto_translation_policy, id)
  WHERE deleted_at IS NULL;

CREATE INDEX ix_managed_feeds_ai_automation
  ON managed_feeds(ai_auto_summary_policy, ai_auto_translation_policy, id)
  WHERE deleted_at IS NULL;
