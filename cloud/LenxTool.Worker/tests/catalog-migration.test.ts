import { applyD1Migrations } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";

const now = "2026-07-21T00:00:00.000Z";

beforeEach(async () => {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM managed_feeds"),
    env.DB.prepare("DELETE FROM feed_categories"),
    env.DB.prepare("UPDATE feed_catalog_state SET catalog_version=0,updated_at=? WHERE singleton_id=1").bind(now)
  ]);
});

describe("feed catalog migrations", () => {
  it("upgrades populated 0001 data and can safely re-run the migration flow", async () => {
    const sentinelBefore = await env.DB.prepare(
      "SELECT attempts FROM auth_attempts WHERE key_hash='migration-v1-sentinel'"
    ).first<{ attempts: number }>();
    const stateBefore = await env.DB.prepare(
      "SELECT singleton_id,catalog_version FROM feed_catalog_state"
    ).first<{ singleton_id: number; catalog_version: number }>();

    expect(sentinelBefore?.attempts).toBe(7);
    expect(stateBefore).toEqual({ singleton_id: 1, catalog_version: 0 });

    await applyD1Migrations(env.DB, env.TEST_MIGRATIONS);

    const sentinelAfter = await env.DB.prepare(
      "SELECT attempts FROM auth_attempts WHERE key_hash='migration-v1-sentinel'"
    ).first<{ attempts: number }>();
    const migrations = await env.DB.prepare(
      "SELECT name FROM d1_migrations ORDER BY id"
    ).all<{ name: string }>();
    expect(sentinelAfter?.attempts).toBe(7);
    expect(migrations.results.map(row => row.name)).toEqual([
      "0001_initial.sql",
      "0002_feed_catalog.sql",
      "0003_catalog_mutations.sql",
      "0004_feed_full_text_policy.sql",
      "0005_feed_ai_policy.sql",
      "0006_automation_rules.sql",
      "0007_explicit_feed_view_kind.sql",
      "0008_feed_discovery_index.sql",
      "0009_smart_views.sql"
    ]);
  });

  it("backfills and maintains the privacy-scoped discovery index from existing catalog rows", async () => {
    for (const trigger of [
      "tr_feed_discovery_feed_insert",
      "tr_feed_discovery_feed_update",
      "tr_feed_discovery_feed_delete",
      "tr_feed_discovery_category_update"
    ]) {
      await env.DB.prepare(`DROP TRIGGER IF EXISTS ${trigger}`).run();
    }
    await env.DB.prepare("DROP TABLE IF EXISTS feed_discovery_rate_limits").run();
    await env.DB.prepare("DROP TABLE IF EXISTS feed_discovery_index").run();
    await env.DB.prepare(
      "DELETE FROM d1_migrations WHERE name='0008_feed_discovery_index.sql'"
    ).run();

    const categoryId = crypto.randomUUID();
    const feedId = crypto.randomUUID();
    await insertCategory(categoryId);
    await insertFeed({
      id: feedId,
      normalizedUrl: "https://example.com/discovery.xml",
      categoryId
    });

    await applyD1Migrations(env.DB, env.TEST_MIGRATIONS);

    const columns = await tableColumns("feed_discovery_index");
    expect(columns).toEqual([
      "feed_id",
      "normalized_url",
      "display_name",
      "display_name_norm",
      "site_url",
      "category_id",
      "category_name",
      "category_name_norm",
      "category_is_enabled",
      "view_kind",
      "feed_is_enabled",
      "updated_at"
    ]);
    expect(columns.join(" ")).not.toMatch(
      /article|body|content|summary|translation|prompt|path|file|user_state|secret|credential/iu
    );

    const backfilled = await env.DB.prepare(
      "SELECT feed_id,normalized_url,display_name,category_name,category_is_enabled,feed_is_enabled " +
        "FROM feed_discovery_index WHERE feed_id=?"
    ).bind(feedId).first<{
      feed_id: string;
      normalized_url: string;
      display_name: string;
      category_name: string;
      category_is_enabled: number;
      feed_is_enabled: number;
    }>();
    expect(backfilled).toEqual({
      feed_id: feedId,
      normalized_url: "https://example.com/discovery.xml",
      display_name: "Example Feed",
      category_name: "Technology",
      category_is_enabled: 1,
      feed_is_enabled: 1
    });

    await env.DB.batch([
      env.DB.prepare(
        "UPDATE managed_feeds SET display_name='Discovery Renamed',is_enabled=0,updated_at=? WHERE id=?"
      ).bind(now, feedId),
      env.DB.prepare(
        "UPDATE feed_categories SET name='Engineering',name_norm='engineering',is_enabled=0,updated_at=? WHERE id=?"
      ).bind(now, categoryId)
    ]);
    const updated = await env.DB.prepare(
      "SELECT display_name,display_name_norm,category_name,category_name_norm," +
        "category_is_enabled,feed_is_enabled FROM feed_discovery_index WHERE feed_id=?"
    ).bind(feedId).first<Record<string, unknown>>();
    expect(updated).toEqual({
      display_name: "Discovery Renamed",
      display_name_norm: "discovery renamed",
      category_name: "Engineering",
      category_name_norm: "engineering",
      category_is_enabled: 0,
      feed_is_enabled: 0
    });

    await env.DB.prepare(
      "UPDATE managed_feeds SET deleted_at=?,updated_at=? WHERE id=?"
    ).bind(now, now, feedId).run();
    const removed = await env.DB.prepare(
      "SELECT feed_id FROM feed_discovery_index WHERE feed_id=?"
    ).bind(feedId).first();
    expect(removed).toBeNull();
  });

  it("preserves legacy non-article view overrides when applying 0007", async () => {
    const feedId = crypto.randomUUID();
    await insertFeed({
      id: feedId,
      normalizedUrl: "https://example.com/legacy-picture.xml",
      viewKind: "PICTURE"
    });
    await env.DB.prepare("ALTER TABLE managed_feeds DROP COLUMN view_kind_explicit").run();
    await env.DB.prepare(
      "DELETE FROM d1_migrations WHERE name='0007_explicit_feed_view_kind.sql'"
    ).run();

    await applyD1Migrations(env.DB, env.TEST_MIGRATIONS);

    const migrated = await env.DB.prepare(
      "SELECT view_kind,view_kind_explicit FROM managed_feeds WHERE id=?"
    ).bind(feedId).first<{ view_kind: string; view_kind_explicit: number }>();
    expect(migrated).toEqual({
      view_kind: "PICTURE",
      view_kind_explicit: 1
    });
  });

  it("keeps meaningful query parameters and prevents duplicate active normalized URLs", async () => {
    const categoryId = crypto.randomUUID();
    await insertCategory(categoryId);
    const normalizedUrl = "https://example.com/feed.xml?lang=zh&edition=full";

    await insertFeed({
      id: crypto.randomUUID(),
      normalizedUrl,
      categoryId
    });

    const stored = await env.DB.prepare(
      "SELECT normalized_url FROM managed_feeds WHERE deleted_at IS NULL"
    ).first<{ normalized_url: string }>();
    expect(stored?.normalized_url).toBe(normalizedUrl);

    await expect(insertFeed({
      id: crypto.randomUUID(),
      normalizedUrl,
      categoryId
    })).rejects.toThrow(/UNIQUE constraint failed/u);
  });

  it("enforces active normalized category name uniqueness without losing history", async () => {
    const firstId = crypto.randomUUID();
    await insertCategory(firstId);

    await expect(insertCategory(crypto.randomUUID())).rejects.toThrow(/UNIQUE constraint failed/u);

    await env.DB.prepare(
      "UPDATE feed_categories SET deleted_at=?,is_enabled=0,updated_at=? WHERE id=?"
    ).bind(now, now, firstId).run();
    await insertCategory(crypto.randomUUID());

    const counts = await env.DB.prepare(
      "SELECT COUNT(*) AS total,SUM(CASE WHEN deleted_at IS NULL THEN 1 ELSE 0 END) AS active FROM feed_categories"
    ).first<{ total: number; active: number }>();
    expect(counts).toEqual({ total: 2, active: 1 });
  });

  it("allows a normalized URL to be re-created only after the old Feed is soft-deleted", async () => {
    const firstId = crypto.randomUUID();
    const normalizedUrl = "https://example.com/feed.xml?lang=zh";
    await insertFeed({ id: firstId, normalizedUrl });
    await env.DB.prepare(
      "UPDATE managed_feeds SET deleted_at=?,is_enabled=0,updated_at=? WHERE id=?"
    ).bind(now, now, firstId).run();

    await insertFeed({ id: crypto.randomUUID(), normalizedUrl });

    const counts = await env.DB.prepare(
      "SELECT COUNT(*) AS total,SUM(CASE WHEN deleted_at IS NULL THEN 1 ELSE 0 END) AS active FROM managed_feeds"
    ).first<{ total: number; active: number }>();
    expect(counts).toEqual({ total: 2, active: 1 });
  });

  it("restricts hard category deletion and preserves its Feed history", async () => {
    const categoryId = crypto.randomUUID();
    const feedId = crypto.randomUUID();

    await expect(insertFeed({
      id: crypto.randomUUID(),
      normalizedUrl: "https://example.com/orphan-category",
      categoryId
    })).rejects.toThrow(/FOREIGN KEY constraint failed/u);

    await insertCategory(categoryId);
    await insertFeed({
      id: feedId,
      normalizedUrl: "https://example.com/category-history",
      categoryId
    });

    await expect(
      env.DB.prepare("DELETE FROM feed_categories WHERE id=?").bind(categoryId).run()
    ).rejects.toThrow(/FOREIGN KEY constraint failed/u);

    await env.DB.prepare(
      "UPDATE feed_categories SET deleted_at=?,is_enabled=0,updated_at=? WHERE id=?"
    ).bind(now, now, categoryId).run();
    const feed = await env.DB.prepare(
      "SELECT id,category_id FROM managed_feeds WHERE id=?"
    ).bind(feedId).first<{ id: string; category_id: string }>();
    expect(feed).toEqual({ id: feedId, category_id: categoryId });
  });

  it("enforces catalog enum, range, boolean, URL, and singleton constraints", async () => {
    await expect(
      env.DB.prepare(
        "INSERT INTO feed_categories(id,name,name_norm,sort_order,is_enabled,version,created_at,updated_at) " +
        "VALUES(NULL,?,?,?,?,?,?,?)"
      ).bind("Invalid", "invalid-null-id", 100, 1, 0, now, now).run()
    ).rejects.toThrow(/NOT NULL constraint failed/u);
    await expect(
      env.DB.prepare(
        "INSERT INTO feed_categories(id,name,name_norm,sort_order,is_enabled,version,created_at,updated_at) " +
        "VALUES(?,?,?,?,?,?,?,?)"
      ).bind(crypto.randomUUID(), "   ", "invalid", 100, 1, 0, now, now).run()
    ).rejects.toThrow(/CHECK constraint failed/u);
    await expect(
      env.DB.prepare(
        "INSERT INTO feed_categories(id,name,name_norm,sort_order,is_enabled,version,created_at,updated_at) " +
        "VALUES(?,?,?,?,?,?,?,?)"
      ).bind(crypto.randomUUID(), "Invalid", "invalid", -1, 1, 0, now, now).run()
    ).rejects.toThrow(/CHECK constraint failed/u);
    await expect(insertFeed({
      id: crypto.randomUUID(),
      normalizedUrl: "https://example.com/invalid-kind",
      viewKind: "UNKNOWN"
    })).rejects.toThrow(/CHECK constraint failed/u);
    await expect(insertFeed({
      id: crypto.randomUUID(),
      normalizedUrl: "https://example.com/invalid-interval",
      refreshIntervalMinutes: 4
    })).rejects.toThrow(/CHECK constraint failed/u);
    await expect(insertFeed({
      id: crypto.randomUUID(),
      normalizedUrl: "https://example.com/invalid-enabled",
      isEnabled: 2
    })).rejects.toThrow(/CHECK constraint failed/u);
    await expect(insertFeed({
      id: crypto.randomUUID(),
      originalUrl: "http://example.com/feed.xml",
      normalizedUrl: "http://example.com/feed.xml"
    })).rejects.toThrow(/CHECK constraint failed/u);
    await expect(
      env.DB.prepare("INSERT INTO feed_catalog_state(singleton_id,catalog_version,updated_at) VALUES(2,0,?)")
        .bind(now).run()
    ).rejects.toThrow(/CHECK constraint failed/u);
    await expect(
      env.DB.prepare("UPDATE feed_catalog_state SET catalog_version=-1 WHERE singleton_id=1").run()
    ).rejects.toThrow(/CHECK constraint failed/u);
  });

  it("rolls back a catalog batch when a later constrained write fails", async () => {
    const categoryId = crypto.randomUUID();
    const feedId = crypto.randomUUID();

    await expect(env.DB.batch([
      categoryStatement(categoryId),
      feedStatement({
        id: feedId,
        normalizedUrl: "https://example.com/rolled-back",
        categoryId,
        refreshIntervalMinutes: 1
      })
    ])).rejects.toThrow(/CHECK constraint failed/u);

    const category = await env.DB.prepare(
      "SELECT id FROM feed_categories WHERE id=?"
    ).bind(categoryId).first();
    const feed = await env.DB.prepare(
      "SELECT id FROM managed_feeds WHERE id=?"
    ).bind(feedId).first();
    expect(category).toBeNull();
    expect(feed).toBeNull();
  });

  it("keeps shared configuration and mutation metadata within their privacy allowlists", async () => {
    const categoryColumns = await tableColumns("feed_categories");
    const feedColumns = await tableColumns("managed_feeds");
    const stateColumns = await tableColumns("feed_catalog_state");
    const idempotencyColumns = await tableColumns("catalog_idempotency");
    const guardColumns = await tableColumns("catalog_mutation_guards");
    const auditColumns = await tableColumns("audit_events");
    const automationStateColumns = await tableColumns("automation_rule_state");
    const automationRuleColumns = await tableColumns("automation_rules");
    const automationVersionColumns = await tableColumns("automation_rule_versions");

    expect(categoryColumns).toEqual([
      "id", "name", "name_norm", "sort_order", "is_enabled", "deleted_at", "version", "created_at", "updated_at",
      "ai_manual_summary_policy", "ai_auto_summary_policy", "ai_auto_translation_policy",
      "ai_translation_target_language", "ai_daily_entry_limit", "ai_max_concurrency"
    ]);
    expect(feedColumns).toEqual([
      "id", "original_url", "normalized_url", "display_name", "site_url", "category_id", "view_kind",
      "refresh_interval_minutes", "sort_order", "is_enabled", "deleted_at", "version", "created_at", "updated_at",
      "full_text_policy", "ai_manual_summary_policy", "ai_auto_summary_policy", "ai_auto_translation_policy",
      "ai_translation_target_language", "ai_daily_entry_limit", "ai_max_concurrency", "view_kind_explicit"
    ]);
    expect(stateColumns).toEqual(["singleton_id", "catalog_version", "updated_at", "last_mutation_id"]);
    expect(idempotencyColumns).toEqual([
      "actor_user_id", "http_method", "normalized_path", "idempotency_key", "request_hash",
      "status_code", "response_body", "created_at", "expires_at"
    ]);
    expect(guardColumns).toEqual(["mutation_id", "valid"]);
    expect(auditColumns).toContain("catalog_version");
    expect(automationStateColumns).toEqual([
      "singleton_id", "rule_set_version", "updated_at", "last_mutation_id"
    ]);
    expect(automationRuleColumns).toEqual([
      "id", "current_version", "name", "priority", "conflict_order", "is_enabled", "match_mode",
      "conditions_json", "actions_json", "created_by", "updated_by", "created_at", "updated_at",
      "last_mutation_id"
    ]);
    expect(automationVersionColumns).toEqual([
      "rule_id", "version", "snapshot_json", "published_by", "published_at"
    ]);

    const allColumns = [
      ...categoryColumns,
      ...feedColumns,
      ...stateColumns,
      ...automationStateColumns,
      ...automationRuleColumns,
      ...automationVersionColumns
    ].join(" ");
    expect(allColumns).not.toMatch(
      /article|body|content|summary_text|translation_text|result_text|prompt|path|file|user_state|secret|credential/iu
    );
    expect(idempotencyColumns.join(" ")).not.toMatch(/request_body|password|secret|token_hash|credential/iu);
  });
});

function categoryStatement(id: string): D1PreparedStatement {
  return env.DB.prepare(
    "INSERT INTO feed_categories(id,name,name_norm,sort_order,is_enabled,version,created_at,updated_at) " +
    "VALUES(?,?,?,?,?,?,?,?)"
  ).bind(id, "Technology", "technology", 100, 1, 0, now, now);
}

async function insertCategory(id: string): Promise<void> {
  await categoryStatement(id).run();
}

interface FeedInput {
  id: string;
  originalUrl?: string;
  normalizedUrl: string;
  categoryId?: string | null;
  viewKind?: string;
  refreshIntervalMinutes?: number;
  isEnabled?: number;
}

function feedStatement(input: FeedInput): D1PreparedStatement {
  return env.DB.prepare(
    "INSERT INTO managed_feeds(" +
    "id,original_url,normalized_url,display_name,site_url,category_id,view_kind,refresh_interval_minutes," +
    "sort_order,is_enabled,version,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)"
  ).bind(
    input.id,
    input.originalUrl ?? input.normalizedUrl,
    input.normalizedUrl,
    "Example Feed",
    "https://example.com/",
    input.categoryId ?? null,
    input.viewKind ?? "ARTICLE",
    input.refreshIntervalMinutes ?? 60,
    100,
    input.isEnabled ?? 1,
    0,
    now,
    now
  );
}

async function insertFeed(input: FeedInput): Promise<void> {
  await feedStatement(input).run();
}

async function tableColumns(table: string): Promise<string[]> {
  const result = await env.DB.prepare(`PRAGMA table_info(${table})`).all<{ name: string }>();
  return result.results.map(row => row.name);
}
