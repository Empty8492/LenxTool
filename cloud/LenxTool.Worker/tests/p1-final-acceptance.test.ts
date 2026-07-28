import { env, exports } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";

const baseUrl = "https://worker.test";
const now = "2026-07-27T00:00:00.000Z";
const privatePayloads = {
  articleBody: "P1_PRIVATE_ARTICLE_BODY_SENTINEL",
  summaryText: "P1_PRIVATE_SUMMARY_SENTINEL",
  translationText: "P1_PRIVATE_TRANSLATION_SENTINEL",
  subtitleText: "P1_PRIVATE_SUBTITLE_SENTINEL",
  localFilePath: "C:\\private\\p1-article.html"
} as const;

interface Session {
  userId: string;
  accessToken: string;
}

interface AiPolicy {
  manualSummary: "INHERIT" | "ENABLED" | "DISABLED";
  autoSummary: "INHERIT" | "ENABLED" | "DISABLED";
  autoTranslation: "INHERIT" | "ENABLED" | "DISABLED";
  translationTargetLanguage: "zh-Hans" | "en" | "ja" | "ko" | null;
  dailyEntryLimit: number | null;
  maxConcurrency: number | null;
}

interface CategoryMutation {
  catalogVersion: number;
  category: { id: string; aiPolicy: AiPolicy };
}

interface FeedMutation {
  catalogVersion: number;
  feed: { id: string; categoryId: string | null; isEnabled: boolean };
}

interface RuleMutation {
  ruleSetVersion: number;
  rule: { id: string; version: number; isEnabled: boolean };
}

beforeEach(async () => {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM automation_rule_versions"),
    env.DB.prepare("DELETE FROM automation_rules"),
    env.DB.prepare(
      "UPDATE automation_rule_state SET rule_set_version=0,updated_at=?,last_mutation_id=NULL " +
      "WHERE singleton_id=1"
    ).bind(now),
    env.DB.prepare("DELETE FROM catalog_idempotency"),
    env.DB.prepare("DELETE FROM catalog_mutation_guards"),
    env.DB.prepare("DELETE FROM managed_feeds"),
    env.DB.prepare("DELETE FROM feed_categories"),
    env.DB.prepare(
      "UPDATE feed_catalog_state SET catalog_version=0,updated_at=?,last_mutation_id=NULL " +
      "WHERE singleton_id=1"
    ).bind(now),
    env.DB.prepare("DELETE FROM audit_events"),
    env.DB.prepare("DELETE FROM daily_usage"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM invites"),
    env.DB.prepare("DELETE FROM auth_attempts"),
    env.DB.prepare("DELETE FROM users")
  ]);
});

describe("P1 final Worker/D1 acceptance", () => {
  it("lets administrators publish shared policy while ordinary users only consume ACTIVE snapshots", async () => {
    const admin = await seedSession("admin");
    const user = await seedSession("user");

    const categoryResponse = await catalogMutation(
      "/v1/admin/feed-categories",
      "POST",
      admin,
      0,
      "p1-category-create-0001",
      { name: "P1 Intelligence", sortOrder: 10, isEnabled: true }
    );
    const category = await expectJson<CategoryMutation>(categoryResponse, 201);

    const feedResponse = await catalogMutation(
      "/v1/admin/feeds",
      "POST",
      admin,
      1,
      "p1-feed-create-000001",
      {
        originalUrl: "https://example.com/p1.xml",
        displayName: "P1 Feed",
        siteUrl: "https://example.com",
        categoryId: category.category.id,
        viewKind: "ARTICLE",
        fullTextPolicy: "BACKGROUND",
        refreshIntervalMinutes: 60,
        sortOrder: 10,
        isEnabled: true
      }
    );
    const feed = await expectJson<FeedMutation>(feedResponse, 201);

    const publishedPolicy: AiPolicy = {
      manualSummary: "ENABLED",
      autoSummary: "ENABLED",
      autoTranslation: "ENABLED",
      translationTargetLanguage: "zh-Hans",
      dailyEntryLimit: 25,
      maxConcurrency: 2
    };
    const policyResponse = await catalogMutation(
      `/v1/admin/feed-categories/${category.category.id}`,
      "PATCH",
      admin,
      2,
      "p1-ai-policy-publish-01",
      { aiPolicy: publishedPolicy }
    );
    const policy = await expectJson<CategoryMutation>(policyResponse, 200);

    const ruleResponse = await automationMutation(
      "/v1/admin/automation-rules",
      "POST",
      admin,
      0,
      "p1-rule-publish-000001",
      validRuleInput()
    );
    const rule = await expectJson<RuleMutation>(ruleResponse, 201);

    expect(category.catalogVersion).toBe(1);
    expect(feed).toMatchObject({
      catalogVersion: 2,
      feed: { categoryId: category.category.id, isEnabled: true }
    });
    expect(policy).toMatchObject({ catalogVersion: 3, category: { aiPolicy: publishedPolicy } });
    expect(rule).toMatchObject({
      ruleSetVersion: 1,
      rule: { id: expect.any(String), version: 1, isEnabled: true }
    });

    const deniedCatalog = await catalogMutation(
      "/v1/admin/feed-categories",
      "POST",
      user,
      3,
      "p1-user-catalog-denied",
      { name: "Denied", articleBody: privatePayloads.articleBody }
    );
    const deniedPolicy = await catalogMutation(
      `/v1/admin/feed-categories/${category.category.id}`,
      "PATCH",
      user,
      3,
      "p1-user-policy-denied-01",
      {
        aiPolicy: { ...publishedPolicy, dailyEntryLimit: 999 },
        summaryText: privatePayloads.summaryText,
        translationText: privatePayloads.translationText
      }
    );
    const deniedRule = await automationMutation(
      "/v1/admin/automation-rules",
      "POST",
      user,
      1,
      "p1-user-rule-denied-0001",
      {
        ...validRuleInput(),
        subtitleText: privatePayloads.subtitleText,
        localFilePath: privatePayloads.localFilePath
      }
    );

    for (const response of [deniedCatalog, deniedPolicy, deniedRule]) {
      expect(response.status).toBe(403);
      await expect(errorCode(response)).resolves.toBe("ADMIN_REQUIRED");
    }
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state")).toBe(3);
    expect(await scalar("SELECT rule_set_version AS value FROM automation_rule_state")).toBe(1);

    const activeCatalogResponse = await workerRequest("/v1/feeds/catalog?scope=ACTIVE", {
      headers: { authorization: `Bearer ${user.accessToken}` }
    });
    const activeCatalog = await expectJson<{
      catalogVersion: number;
      scope: string;
      categories: Array<{ id: string; aiPolicy: AiPolicy }>;
      feeds: Array<{ id: string; categoryId: string | null; isEnabled: boolean }>;
    }>(activeCatalogResponse, 200);
    expect(activeCatalog).toMatchObject({
      catalogVersion: 3,
      scope: "ACTIVE",
      categories: [{ id: category.category.id, aiPolicy: publishedPolicy }],
      feeds: [{ id: feed.feed.id, categoryId: category.category.id, isEnabled: true }]
    });

    const activeRulesResponse = await workerRequest("/v1/automation-rules?scope=ACTIVE", {
      headers: { authorization: `Bearer ${user.accessToken}` }
    });
    const activeRules = await expectJson<{
      ruleSetVersion: number;
      scope: string;
      rules: Array<{ id: string; version: number; isEnabled: boolean }>;
    }>(activeRulesResponse, 200);
    expect(activeRules).toMatchObject({
      ruleSetVersion: 1,
      scope: "ACTIVE",
      rules: [{ id: rule.rule.id, version: 1, isEnabled: true }]
    });

    const catalogAll = await workerRequest("/v1/feeds/catalog?scope=ALL", {
      headers: { authorization: `Bearer ${user.accessToken}` }
    });
    const rulesAll = await workerRequest("/v1/automation-rules?scope=ALL", {
      headers: { authorization: `Bearer ${user.accessToken}` }
    });
    expect(catalogAll.status).toBe(403);
    expect(rulesAll.status).toBe(403);
  });

  it("keeps D1 schema and rows free of article, AI-result, subtitle, and local-file payloads", async () => {
    const admin = await seedSession("admin");
    const user = await seedSession("user");

    const categoryResponse = await catalogMutation(
      "/v1/admin/feed-categories",
      "POST",
      admin,
      0,
      "p1-privacy-category-01",
      { name: "Privacy", sortOrder: 0, isEnabled: true }
    );
    const category = await expectJson<CategoryMutation>(categoryResponse, 201);

    const denied = await catalogMutation(
      `/v1/admin/feed-categories/${category.category.id}`,
      "PATCH",
      user,
      1,
      "p1-private-data-denied",
      privatePayloads
    );
    expect(denied.status).toBe(403);

    const tables = await env.DB.prepare(
      "SELECT name FROM sqlite_master WHERE type='table' " +
      "AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '_cf_%' ORDER BY name"
    ).all<{ name: string }>();
    const tableNames = tables.results.map(row => row.name);
    expect(tableNames).toEqual([
      "audit_events",
      "auth_attempts",
      "automation_rule_state",
      "automation_rule_versions",
      "automation_rules",
      "catalog_idempotency",
      "catalog_mutation_guards",
      "d1_migrations",
      "daily_usage",
      "feed_catalog_state",
      "feed_categories",
      "feed_discovery_index",
      "feed_discovery_rate_limits",
      "invites",
      "managed_feeds",
      "refresh_tokens",
      "smart_view_state",
      "smart_view_versions",
      "smart_views",
      "users"
    ]);

    const forbiddenPayloadColumn = /(^|_)(article_(body|content)|body_(html|text|markdown)|content_(html|text|markdown)|summary_(text|content|result)|translation_(text|content|result)|translated_(text|content)|subtitle(s)?(_(text|content|path))?|local_(file|path)|file_(path|content)|asset_path)(_|$)/iu;
    const persistedRows: Record<string, unknown[]> = {};
    for (const tableName of tableNames) {
      const identifier = quoteIdentifier(tableName);
      const columns = await env.DB.prepare(`PRAGMA table_info(${identifier})`).all<{ name: string }>();
      expect(columns.results.map(column => column.name).join(" ")).not.toMatch(forbiddenPayloadColumn);
      const rows = await env.DB.prepare(`SELECT * FROM ${identifier}`).all<Record<string, unknown>>();
      persistedRows[tableName] = rows.results;
    }

    const persistedJson = JSON.stringify(persistedRows);
    for (const sentinel of Object.values(privatePayloads)) {
      expect(persistedJson).not.toContain(sentinel);
    }
  });
});

function validRuleInput(): Record<string, unknown> {
  return {
    name: "P1 release intelligence",
    priority: 100,
    conflictOrder: 10,
    isEnabled: true,
    matchMode: "ALL",
    conditions: [{ field: "TITLE", operator: "CONTAINS", value: "release" }],
    actions: [
      { type: "ADD_TAG", order: 10, value: "P1" },
      { type: "GENERATE_SUMMARY", order: 20 },
      { type: "NOTIFY", order: 30 }
    ]
  };
}

function catalogMutation(
  path: string,
  method: "POST" | "PATCH",
  session: Session,
  version: number,
  idempotencyKey: string,
  body: Record<string, unknown>
): Promise<Response> {
  return workerRequest(path, {
    method,
    headers: {
      authorization: `Bearer ${session.accessToken}`,
      "content-type": "application/json",
      "if-match": `"catalog-all-${version}"`,
      "idempotency-key": idempotencyKey
    },
    body: JSON.stringify(body)
  });
}

function automationMutation(
  path: string,
  method: "POST" | "PATCH",
  session: Session,
  version: number,
  idempotencyKey: string,
  body: Record<string, unknown>
): Promise<Response> {
  return workerRequest(path, {
    method,
    headers: {
      authorization: `Bearer ${session.accessToken}`,
      "content-type": "application/json",
      "if-match": `"automation-all-${version}"`,
      "idempotency-key": idempotencyKey
    },
    body: JSON.stringify(body)
  });
}

function workerRequest(path: string, init?: RequestInit): Promise<Response> {
  return exports.default.fetch(new Request(`${baseUrl}${path}`, init));
}

async function expectJson<T>(response: Response, status: number): Promise<T> {
  const text = await response.text();
  expect(response.status, text).toBe(status);
  return JSON.parse(text) as T;
}

async function errorCode(response: Response): Promise<string> {
  const body = await response.clone().json<{ error: { code: string } }>();
  return body.error.code;
}

async function scalar(query: string): Promise<number> {
  const row = await env.DB.prepare(query).first<{ value: number }>();
  if (!row) throw new Error(`Expected scalar query result: ${query}`);
  return row.value;
}

function quoteIdentifier(value: string): string {
  return `"${value.replaceAll('"', '""')}"`;
}

async function seedSession(role: "user" | "admin"): Promise<Session> {
  const userId = crypto.randomUUID();
  const createdAt = new Date().toISOString();
  await env.DB.prepare(
    "INSERT INTO users(id,username,username_norm,password_salt,password_hash,role,ai_daily_limit," +
    "speech_daily_seconds,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?)"
  ).bind(
    userId,
    `${role}-${userId}`,
    `${role}-${userId}`,
    "unused-salt",
    "unused-hash",
    role,
    20,
    0,
    createdAt,
    createdAt
  ).run();
  const issuedAt = Math.floor(Date.now() / 1000);
  return {
    userId,
    accessToken: await signAccessToken({
      sub: userId,
      role,
      iat: issuedAt,
      exp: issuedAt + 900,
      jti: crypto.randomUUID()
    })
  };
}

async function signAccessToken(claims: Record<string, unknown>): Promise<string> {
  const encoder = new TextEncoder();
  const header = toBase64Url(encoder.encode(JSON.stringify({ alg: "HS256", typ: "JWT" })));
  const payload = toBase64Url(encoder.encode(JSON.stringify(claims)));
  const key = await crypto.subtle.importKey(
    "raw",
    encoder.encode(env.TOKEN_SECRET),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"]
  );
  const signature = new Uint8Array(
    await crypto.subtle.sign("HMAC", key, encoder.encode(`${header}.${payload}`))
  );
  return `${header}.${payload}.${toBase64Url(signature)}`;
}

function toBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}
