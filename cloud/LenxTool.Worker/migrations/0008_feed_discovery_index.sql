CREATE TABLE feed_discovery_index (
  feed_id TEXT NOT NULL PRIMARY KEY
    REFERENCES managed_feeds(id) ON DELETE CASCADE
    CHECK(length(feed_id) = 36),
  normalized_url TEXT NOT NULL
    CHECK(length(normalized_url) BETWEEN 1 AND 2048
      AND substr(normalized_url, 1, 8) = 'https://'
      AND instr(normalized_url, '#') = 0),
  display_name TEXT NOT NULL
    CHECK(length(trim(display_name)) BETWEEN 1 AND 160),
  display_name_norm TEXT NOT NULL
    CHECK(length(display_name_norm) BETWEEN 1 AND 320),
  site_url TEXT
    CHECK(site_url IS NULL OR
      (length(site_url) BETWEEN 1 AND 2048
        AND lower(substr(trim(site_url), 1, 8)) = 'https://')),
  category_id TEXT
    CHECK(category_id IS NULL OR length(category_id) = 36),
  category_name TEXT
    CHECK(category_name IS NULL OR length(trim(category_name)) BETWEEN 1 AND 80),
  category_name_norm TEXT
    CHECK(category_name_norm IS NULL OR length(category_name_norm) BETWEEN 1 AND 160),
  category_is_enabled INTEGER NOT NULL
    CHECK(typeof(category_is_enabled) = 'integer' AND category_is_enabled IN (0, 1)),
  view_kind TEXT NOT NULL
    CHECK(view_kind IN ('ARTICLE', 'PICTURE', 'AUDIO', 'VIDEO', 'NOTIFICATION')),
  feed_is_enabled INTEGER NOT NULL
    CHECK(typeof(feed_is_enabled) = 'integer' AND feed_is_enabled IN (0, 1)),
  updated_at TEXT NOT NULL
    CHECK(length(updated_at) BETWEEN 20 AND 40)
);

CREATE UNIQUE INDEX ux_feed_discovery_normalized_url
  ON feed_discovery_index(normalized_url);

CREATE INDEX ix_feed_discovery_title
  ON feed_discovery_index(display_name_norm, updated_at DESC, feed_id);

CREATE INDEX ix_feed_discovery_category
  ON feed_discovery_index(category_name_norm, updated_at DESC, feed_id)
  WHERE category_name_norm IS NOT NULL;

CREATE INDEX ix_feed_discovery_active
  ON feed_discovery_index(feed_is_enabled, category_is_enabled, updated_at DESC, feed_id);

INSERT INTO feed_discovery_index(
  feed_id,
  normalized_url,
  display_name,
  display_name_norm,
  site_url,
  category_id,
  category_name,
  category_name_norm,
  category_is_enabled,
  view_kind,
  feed_is_enabled,
  updated_at
)
SELECT
  f.id,
  f.normalized_url,
  f.display_name,
  lower(trim(f.display_name)),
  f.site_url,
  f.category_id,
  CASE WHEN c.deleted_at IS NULL THEN c.name ELSE NULL END,
  CASE WHEN c.deleted_at IS NULL THEN c.name_norm ELSE NULL END,
  CASE WHEN c.id IS NOT NULL AND c.deleted_at IS NULL THEN c.is_enabled ELSE 0 END,
  f.view_kind,
  f.is_enabled,
  f.updated_at
FROM managed_feeds f
LEFT JOIN feed_categories c ON c.id = f.category_id
WHERE f.deleted_at IS NULL;

CREATE TRIGGER tr_feed_discovery_feed_insert
AFTER INSERT ON managed_feeds
WHEN NEW.deleted_at IS NULL
BEGIN
  INSERT INTO feed_discovery_index(
    feed_id,
    normalized_url,
    display_name,
    display_name_norm,
    site_url,
    category_id,
    category_name,
    category_name_norm,
    category_is_enabled,
    view_kind,
    feed_is_enabled,
    updated_at
  )
  SELECT
    NEW.id,
    NEW.normalized_url,
    NEW.display_name,
    lower(trim(NEW.display_name)),
    NEW.site_url,
    NEW.category_id,
    CASE WHEN c.deleted_at IS NULL THEN c.name ELSE NULL END,
    CASE WHEN c.deleted_at IS NULL THEN c.name_norm ELSE NULL END,
    CASE WHEN c.id IS NOT NULL AND c.deleted_at IS NULL THEN c.is_enabled ELSE 0 END,
    NEW.view_kind,
    NEW.is_enabled,
    NEW.updated_at
  FROM (SELECT 1) seed
  LEFT JOIN feed_categories c ON c.id = NEW.category_id;
END;

CREATE TRIGGER tr_feed_discovery_feed_update
AFTER UPDATE OF
  normalized_url,
  display_name,
  site_url,
  category_id,
  view_kind,
  is_enabled,
  deleted_at,
  updated_at
ON managed_feeds
BEGIN
  DELETE FROM feed_discovery_index WHERE feed_id = OLD.id;
  INSERT INTO feed_discovery_index(
    feed_id,
    normalized_url,
    display_name,
    display_name_norm,
    site_url,
    category_id,
    category_name,
    category_name_norm,
    category_is_enabled,
    view_kind,
    feed_is_enabled,
    updated_at
  )
  SELECT
    NEW.id,
    NEW.normalized_url,
    NEW.display_name,
    lower(trim(NEW.display_name)),
    NEW.site_url,
    NEW.category_id,
    CASE WHEN c.deleted_at IS NULL THEN c.name ELSE NULL END,
    CASE WHEN c.deleted_at IS NULL THEN c.name_norm ELSE NULL END,
    CASE WHEN c.id IS NOT NULL AND c.deleted_at IS NULL THEN c.is_enabled ELSE 0 END,
    NEW.view_kind,
    NEW.is_enabled,
    NEW.updated_at
  FROM (SELECT 1) seed
  LEFT JOIN feed_categories c ON c.id = NEW.category_id
  WHERE NEW.deleted_at IS NULL;
END;

CREATE TRIGGER tr_feed_discovery_feed_delete
AFTER DELETE ON managed_feeds
BEGIN
  DELETE FROM feed_discovery_index WHERE feed_id = OLD.id;
END;

CREATE TRIGGER tr_feed_discovery_category_update
AFTER UPDATE OF name, name_norm, is_enabled, deleted_at ON feed_categories
BEGIN
  UPDATE feed_discovery_index
  SET
    category_name = CASE WHEN NEW.deleted_at IS NULL THEN NEW.name ELSE NULL END,
    category_name_norm = CASE WHEN NEW.deleted_at IS NULL THEN NEW.name_norm ELSE NULL END,
    category_is_enabled = CASE WHEN NEW.deleted_at IS NULL THEN NEW.is_enabled ELSE 0 END
  WHERE category_id = NEW.id;
END;

CREATE TABLE feed_discovery_rate_limits (
  actor_user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  bucket TEXT NOT NULL CHECK(length(bucket) = 16),
  attempts INTEGER NOT NULL
    CHECK(typeof(attempts) = 'integer' AND attempts BETWEEN 1 AND 1000000),
  PRIMARY KEY(actor_user_id, bucket)
);

CREATE INDEX ix_feed_discovery_rate_limit_bucket
  ON feed_discovery_rate_limits(bucket);
