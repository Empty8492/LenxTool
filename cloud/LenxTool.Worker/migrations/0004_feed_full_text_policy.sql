ALTER TABLE managed_feeds
  ADD COLUMN full_text_policy TEXT NOT NULL DEFAULT 'NONE'
  CHECK(full_text_policy IN ('NONE', 'ON_OPEN', 'BACKGROUND'));

CREATE INDEX ix_managed_feeds_full_text_policy
  ON managed_feeds(full_text_policy, is_enabled, id)
  WHERE deleted_at IS NULL;
