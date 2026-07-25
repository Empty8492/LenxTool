CREATE TABLE automation_rule_state (
  singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
  rule_set_version INTEGER NOT NULL DEFAULT 0
    CHECK(typeof(rule_set_version) = 'integer' AND rule_set_version >= 0),
  updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40),
  last_mutation_id TEXT
    CHECK(last_mutation_id IS NULL OR length(last_mutation_id) = 36)
);

INSERT INTO automation_rule_state(singleton_id, rule_set_version, updated_at)
VALUES(1, 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

CREATE TABLE automation_rules (
  id TEXT PRIMARY KEY CHECK(length(id) = 36),
  current_version INTEGER NOT NULL
    CHECK(typeof(current_version) = 'integer' AND current_version >= 1),
  name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
  priority INTEGER NOT NULL
    CHECK(typeof(priority) = 'integer' AND priority BETWEEN 0 AND 1000),
  conflict_order INTEGER NOT NULL
    CHECK(typeof(conflict_order) = 'integer' AND conflict_order BETWEEN 0 AND 1000),
  is_enabled INTEGER NOT NULL CHECK(is_enabled IN (0, 1)),
  match_mode TEXT NOT NULL CHECK(match_mode IN ('ALL', 'ANY')),
  conditions_json TEXT NOT NULL
    CHECK(length(conditions_json) BETWEEN 2 AND 32768 AND json_valid(conditions_json)),
  actions_json TEXT NOT NULL
    CHECK(length(actions_json) BETWEEN 2 AND 16384 AND json_valid(actions_json)),
  created_by TEXT NOT NULL REFERENCES users(id),
  updated_by TEXT NOT NULL REFERENCES users(id),
  created_at TEXT NOT NULL CHECK(length(created_at) BETWEEN 20 AND 40),
  updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40),
  last_mutation_id TEXT NOT NULL CHECK(length(last_mutation_id) = 36)
);

CREATE TABLE automation_rule_versions (
  rule_id TEXT NOT NULL REFERENCES automation_rules(id) ON DELETE CASCADE,
  version INTEGER NOT NULL
    CHECK(typeof(version) = 'integer' AND version >= 1),
  snapshot_json TEXT NOT NULL
    CHECK(length(snapshot_json) BETWEEN 2 AND 65536 AND json_valid(snapshot_json)),
  published_by TEXT NOT NULL REFERENCES users(id),
  published_at TEXT NOT NULL CHECK(length(published_at) BETWEEN 20 AND 40),
  PRIMARY KEY(rule_id, version)
);

CREATE INDEX ix_automation_rules_active_order
  ON automation_rules(is_enabled, priority DESC, conflict_order, id);

CREATE INDEX ix_automation_rule_versions_published
  ON automation_rule_versions(published_at DESC, rule_id, version DESC);
