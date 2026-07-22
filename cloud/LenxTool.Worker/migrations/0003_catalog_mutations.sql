ALTER TABLE feed_catalog_state
  ADD COLUMN last_mutation_id TEXT
    CHECK(last_mutation_id IS NULL OR length(last_mutation_id) = 36);

ALTER TABLE audit_events
  ADD COLUMN catalog_version INTEGER
    CHECK(catalog_version IS NULL OR (typeof(catalog_version) = 'integer' AND catalog_version >= 0));

CREATE TABLE catalog_idempotency (
  actor_user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  http_method TEXT NOT NULL CHECK(http_method IN ('POST', 'PATCH', 'DELETE')),
  normalized_path TEXT NOT NULL CHECK(length(normalized_path) BETWEEN 1 AND 256),
  idempotency_key TEXT NOT NULL CHECK(length(idempotency_key) BETWEEN 16 AND 128),
  request_hash TEXT NOT NULL CHECK(length(request_hash) = 43),
  status_code INTEGER NOT NULL CHECK(status_code IN (200, 201)),
  response_body TEXT NOT NULL CHECK(length(response_body) BETWEEN 2 AND 131072),
  created_at TEXT NOT NULL CHECK(length(created_at) BETWEEN 20 AND 40),
  expires_at TEXT NOT NULL CHECK(length(expires_at) BETWEEN 20 AND 40),
  PRIMARY KEY(actor_user_id, http_method, normalized_path, idempotency_key)
);

CREATE INDEX ix_catalog_idempotency_expires
  ON catalog_idempotency(expires_at);

CREATE TABLE catalog_mutation_guards (
  mutation_id TEXT PRIMARY KEY CHECK(length(mutation_id) = 36),
  valid INTEGER NOT NULL CHECK(valid = 1)
);
