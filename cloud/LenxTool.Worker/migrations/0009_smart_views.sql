CREATE TABLE smart_view_state (
  singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
  view_set_version INTEGER NOT NULL DEFAULT 0
    CHECK(typeof(view_set_version) = 'integer' AND view_set_version >= 0),
  updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40),
  last_mutation_id TEXT
    CHECK(last_mutation_id IS NULL OR length(last_mutation_id) = 36)
);

INSERT INTO smart_view_state(singleton_id, view_set_version, updated_at)
VALUES(1, 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

CREATE TABLE smart_views (
  id TEXT PRIMARY KEY CHECK(length(id) = 36),
  current_version INTEGER NOT NULL
    CHECK(typeof(current_version) = 'integer' AND current_version >= 1),
  name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
  sort_order INTEGER NOT NULL
    CHECK(typeof(sort_order) = 'integer' AND sort_order BETWEEN 0 AND 1000),
  is_enabled INTEGER NOT NULL CHECK(is_enabled IN (0, 1)),
  feed_id TEXT CHECK(feed_id IS NULL OR length(feed_id) = 36),
  category_id TEXT CHECK(category_id IS NULL OR length(category_id) = 36),
  view_kind TEXT CHECK(view_kind IS NULL OR view_kind IN (
    'ARTICLE', 'PICTURE', 'AUDIO', 'VIDEO', 'NOTIFICATION')),
  read_filter TEXT NOT NULL CHECK(read_filter IN ('ALL', 'UNREAD', 'READ')),
  favorites_only INTEGER NOT NULL CHECK(favorites_only IN (0, 1)),
  search_text TEXT CHECK(search_text IS NULL OR length(search_text) BETWEEN 1 AND 200),
  published_within_days INTEGER
    CHECK(published_within_days IS NULL OR (
      typeof(published_within_days) = 'integer'
      AND published_within_days BETWEEN 1 AND 365)),
  created_by TEXT NOT NULL REFERENCES users(id),
  updated_by TEXT NOT NULL REFERENCES users(id),
  created_at TEXT NOT NULL CHECK(length(created_at) BETWEEN 20 AND 40),
  updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40),
  last_mutation_id TEXT NOT NULL CHECK(length(last_mutation_id) = 36)
);

CREATE INDEX ix_smart_views_active_order
  ON smart_views(is_enabled, sort_order, name, id);

CREATE TABLE smart_view_versions (
  view_id TEXT NOT NULL CHECK(length(view_id) = 36),
  version INTEGER NOT NULL
    CHECK(typeof(version) = 'integer' AND version >= 1),
  snapshot_json TEXT NOT NULL
    CHECK(length(snapshot_json) BETWEEN 2 AND 8192
      AND json_valid(snapshot_json)),
  published_by TEXT NOT NULL REFERENCES users(id),
  published_at TEXT NOT NULL CHECK(length(published_at) BETWEEN 20 AND 40),
  PRIMARY KEY(view_id, version)
) WITHOUT ROWID;
