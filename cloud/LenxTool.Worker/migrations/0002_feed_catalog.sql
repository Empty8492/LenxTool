CREATE TABLE feed_catalog_state (
  singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
  catalog_version INTEGER NOT NULL DEFAULT 0
    CHECK(typeof(catalog_version) = 'integer' AND catalog_version >= 0),
  updated_at TEXT NOT NULL
    CHECK(length(updated_at) BETWEEN 20 AND 40)
);

INSERT INTO feed_catalog_state(singleton_id, catalog_version, updated_at)
VALUES(1, 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

CREATE TABLE feed_categories (
  id TEXT NOT NULL PRIMARY KEY CHECK(length(id) = 36),
  name TEXT NOT NULL CHECK(length(trim(name)) BETWEEN 1 AND 80),
  name_norm TEXT NOT NULL CHECK(length(name_norm) BETWEEN 1 AND 160),
  sort_order INTEGER NOT NULL DEFAULT 0
    CHECK(typeof(sort_order) = 'integer' AND sort_order BETWEEN 0 AND 1000000),
  is_enabled INTEGER NOT NULL DEFAULT 1
    CHECK(typeof(is_enabled) = 'integer' AND is_enabled IN (0, 1)),
  deleted_at TEXT CHECK(deleted_at IS NULL OR length(deleted_at) BETWEEN 20 AND 40),
  version INTEGER NOT NULL DEFAULT 0
    CHECK(typeof(version) = 'integer' AND version >= 0),
  created_at TEXT NOT NULL CHECK(length(created_at) BETWEEN 20 AND 40),
  updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40)
);

CREATE UNIQUE INDEX ux_feed_categories_name_norm_active
  ON feed_categories(name_norm)
  WHERE deleted_at IS NULL;

CREATE INDEX ix_feed_categories_catalog_order
  ON feed_categories(is_enabled, sort_order, id)
  WHERE deleted_at IS NULL;

CREATE INDEX ix_feed_categories_version
  ON feed_categories(version);

CREATE TABLE managed_feeds (
  id TEXT NOT NULL PRIMARY KEY CHECK(length(id) = 36),
  original_url TEXT NOT NULL
    CHECK(length(original_url) BETWEEN 1 AND 2048 AND lower(substr(trim(original_url), 1, 8)) = 'https://'),
  normalized_url TEXT NOT NULL
    CHECK(length(normalized_url) BETWEEN 1 AND 2048 AND substr(normalized_url, 1, 8) = 'https://' AND instr(normalized_url, '#') = 0),
  display_name TEXT NOT NULL CHECK(length(trim(display_name)) BETWEEN 1 AND 160),
  site_url TEXT
    CHECK(site_url IS NULL OR (length(site_url) BETWEEN 1 AND 2048 AND lower(substr(trim(site_url), 1, 8)) = 'https://')),
  category_id TEXT,
  view_kind TEXT NOT NULL DEFAULT 'ARTICLE'
    CHECK(view_kind IN ('ARTICLE', 'PICTURE', 'AUDIO', 'VIDEO', 'NOTIFICATION')),
  refresh_interval_minutes INTEGER NOT NULL DEFAULT 60
    CHECK(typeof(refresh_interval_minutes) = 'integer' AND refresh_interval_minutes BETWEEN 5 AND 1440),
  sort_order INTEGER NOT NULL DEFAULT 0
    CHECK(typeof(sort_order) = 'integer' AND sort_order BETWEEN 0 AND 1000000),
  is_enabled INTEGER NOT NULL DEFAULT 1
    CHECK(typeof(is_enabled) = 'integer' AND is_enabled IN (0, 1)),
  deleted_at TEXT CHECK(deleted_at IS NULL OR length(deleted_at) BETWEEN 20 AND 40),
  version INTEGER NOT NULL DEFAULT 0
    CHECK(typeof(version) = 'integer' AND version >= 0),
  created_at TEXT NOT NULL CHECK(length(created_at) BETWEEN 20 AND 40),
  updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40),
  FOREIGN KEY(category_id) REFERENCES feed_categories(id) ON UPDATE RESTRICT ON DELETE RESTRICT
);

CREATE UNIQUE INDEX ux_managed_feeds_normalized_url_active
  ON managed_feeds(normalized_url)
  WHERE deleted_at IS NULL;

CREATE INDEX ix_managed_feeds_catalog_order
  ON managed_feeds(category_id, is_enabled, sort_order, id)
  WHERE deleted_at IS NULL;

CREATE INDEX ix_managed_feeds_version
  ON managed_feeds(version);
