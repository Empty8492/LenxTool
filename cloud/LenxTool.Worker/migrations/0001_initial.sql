PRAGMA foreign_keys = ON;

CREATE TABLE users (
  id TEXT PRIMARY KEY,
  username TEXT NOT NULL,
  username_norm TEXT NOT NULL UNIQUE,
  password_salt TEXT NOT NULL,
  password_hash TEXT NOT NULL,
  role TEXT NOT NULL CHECK(role IN ('user','admin')) DEFAULT 'user',
  disabled INTEGER NOT NULL DEFAULT 0,
  ai_daily_limit INTEGER NOT NULL DEFAULT 10 CHECK(ai_daily_limit >= 0),
  speech_daily_seconds INTEGER NOT NULL DEFAULT 600 CHECK(speech_daily_seconds >= 0),
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE invites (
  id TEXT PRIMARY KEY,
  code_hash TEXT NOT NULL UNIQUE,
  created_by TEXT NOT NULL REFERENCES users(id),
  role TEXT NOT NULL CHECK(role IN ('user','admin')) DEFAULT 'user',
  ai_daily_limit INTEGER NOT NULL DEFAULT 10,
  speech_daily_seconds INTEGER NOT NULL DEFAULT 600,
  max_uses INTEGER NOT NULL DEFAULT 1,
  used_count INTEGER NOT NULL DEFAULT 0,
  disabled INTEGER NOT NULL DEFAULT 0,
  expires_at TEXT,
  created_at TEXT NOT NULL
);

CREATE TABLE refresh_tokens (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  token_hash TEXT NOT NULL UNIQUE,
  expires_at TEXT NOT NULL,
  revoked_at TEXT,
  replaced_by TEXT,
  created_at TEXT NOT NULL
);

CREATE TABLE daily_usage (
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  usage_date TEXT NOT NULL,
  ai_used INTEGER NOT NULL DEFAULT 0,
  ai_reserved INTEGER NOT NULL DEFAULT 0,
  speech_used_seconds REAL NOT NULL DEFAULT 0,
  speech_reserved_seconds REAL NOT NULL DEFAULT 0,
  PRIMARY KEY(user_id, usage_date)
);

CREATE TABLE audit_events (
  id TEXT PRIMARY KEY,
  actor_user_id TEXT,
  target_type TEXT NOT NULL,
  target_id TEXT,
  action TEXT NOT NULL,
  request_id TEXT NOT NULL,
  ip_hash TEXT,
  created_at TEXT NOT NULL
);

CREATE TABLE auth_attempts (
  key_hash TEXT NOT NULL,
  bucket TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY(key_hash, bucket)
);

CREATE INDEX ix_refresh_tokens_user ON refresh_tokens(user_id, expires_at);
CREATE INDEX ix_audit_created ON audit_events(created_at DESC);
