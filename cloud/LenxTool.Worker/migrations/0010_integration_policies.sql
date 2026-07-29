CREATE TABLE integration_policy_state (
  singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
  policy_set_version INTEGER NOT NULL DEFAULT 0
    CHECK(typeof(policy_set_version) = 'integer' AND policy_set_version >= 0),
  updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40),
  last_mutation_id TEXT
    CHECK(last_mutation_id IS NULL OR length(last_mutation_id) = 36)
);

INSERT INTO integration_policy_state(
  singleton_id,
  policy_set_version,
  updated_at
) VALUES(1, 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

-- 共享表只保存类型开关和精确主机白名单；个人目标、凭据和健康结果禁止入库。
CREATE TABLE integration_policies (
  kind TEXT PRIMARY KEY CHECK(kind IN (
    'OBSIDIAN', 'EAGLE', 'ZOTERO', 'READWISE', 'CUBOX',
    'READECK', 'OUTLINE', 'QBITTORRENT', 'WEBHOOK')),
  is_enabled INTEGER NOT NULL CHECK(is_enabled IN (0, 1)),
  allowed_hosts_json TEXT NOT NULL
    CHECK(length(allowed_hosts_json) BETWEEN 2 AND 8192
      AND json_valid(allowed_hosts_json)
      AND json_type(allowed_hosts_json) = 'array'),
  updated_by TEXT NOT NULL REFERENCES users(id),
  updated_at TEXT NOT NULL CHECK(length(updated_at) BETWEEN 20 AND 40),
  last_mutation_id TEXT NOT NULL CHECK(length(last_mutation_id) = 36)
);

CREATE INDEX ix_integration_policies_active
  ON integration_policies(is_enabled, kind);

CREATE TABLE integration_policy_versions (
  policy_set_version INTEGER PRIMARY KEY
    CHECK(typeof(policy_set_version) = 'integer' AND policy_set_version >= 1),
  snapshot_json TEXT NOT NULL
    CHECK(length(snapshot_json) BETWEEN 2 AND 65536
      AND json_valid(snapshot_json)),
  published_by TEXT NOT NULL REFERENCES users(id),
  published_at TEXT NOT NULL CHECK(length(published_at) BETWEEN 20 AND 40)
);

CREATE TABLE integration_policy_idempotency (
  actor_user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  idempotency_key TEXT NOT NULL CHECK(length(idempotency_key) BETWEEN 16 AND 128),
  request_hash TEXT NOT NULL CHECK(length(request_hash) = 43),
  status_code INTEGER NOT NULL CHECK(status_code = 200),
  response_body TEXT NOT NULL CHECK(length(response_body) BETWEEN 2 AND 65536),
  created_at TEXT NOT NULL CHECK(length(created_at) BETWEEN 20 AND 40),
  expires_at TEXT NOT NULL CHECK(length(expires_at) BETWEEN 20 AND 40),
  PRIMARY KEY(actor_user_id, idempotency_key)
);

CREATE INDEX ix_integration_policy_idempotency_expires
  ON integration_policy_idempotency(expires_at);

CREATE TABLE integration_policy_mutation_guards (
  mutation_id TEXT PRIMARY KEY CHECK(length(mutation_id) = 36),
  valid INTEGER NOT NULL CHECK(valid = 1)
);
