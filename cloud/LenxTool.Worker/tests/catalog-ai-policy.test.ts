import { env, exports } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";

const baseUrl = "https://worker.test";
const categoryId = "10000000-0000-4000-8000-000000000013";
const feedId = "20000000-0000-4000-8000-000000000013";
const now = "2026-07-25T08:00:00.000Z";

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

beforeEach(async () => {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM catalog_idempotency"),
    env.DB.prepare("DELETE FROM catalog_mutation_guards"),
    env.DB.prepare("DELETE FROM managed_feeds"),
    env.DB.prepare("DELETE FROM feed_categories"),
    env.DB.prepare(
      "UPDATE feed_catalog_state SET catalog_version=0,updated_at=?,last_mutation_id=NULL WHERE singleton_id=1"
    ).bind(now),
    env.DB.prepare("DELETE FROM audit_events"),
    env.DB.prepare("DELETE FROM daily_usage"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM invites"),
    env.DB.prepare("DELETE FROM auth_attempts"),
    env.DB.prepare("DELETE FROM users")
  ]);
});

describe("Worker v1 feed AI policy catalog fields", () => {
  it("migrates constrained policy columns with automatic processing disabled by inheritance", async () => {
    await seedCatalog();

    const category = await env.DB.prepare(
      "SELECT ai_manual_summary_policy,ai_auto_summary_policy,ai_auto_translation_policy," +
      "ai_translation_target_language,ai_daily_entry_limit,ai_max_concurrency FROM feed_categories WHERE id=?"
    ).bind(categoryId).first<Record<string, unknown>>();
    const feed = await env.DB.prepare(
      "SELECT ai_manual_summary_policy,ai_auto_summary_policy,ai_auto_translation_policy," +
      "ai_translation_target_language,ai_daily_entry_limit,ai_max_concurrency FROM managed_feeds WHERE id=?"
    ).bind(feedId).first<Record<string, unknown>>();

    expect(category).toEqual(inheritedPolicyRow());
    expect(feed).toEqual(inheritedPolicyRow());
    await expect(env.DB.prepare(
      "UPDATE managed_feeds SET ai_auto_summary_policy='ALWAYS' WHERE id=?"
    ).bind(feedId).run()).rejects.toThrow();
    await expect(env.DB.prepare(
      "UPDATE managed_feeds SET ai_max_concurrency=5 WHERE id=?"
    ).bind(feedId).run()).rejects.toThrow();
  });

  it("lets ordinary users read safe defaults and per-resource policy overrides", async () => {
    const user = await seedSession("user");
    await seedCatalog();
    await env.DB.prepare(
      "UPDATE feed_categories SET ai_manual_summary_policy='DISABLED',ai_auto_summary_policy='ENABLED'," +
      "ai_daily_entry_limit=12,ai_max_concurrency=2 WHERE id=?"
    ).bind(categoryId).run();

    const response = await workerRequest("/v1/feeds/catalog?scope=ACTIVE", {
      headers: { authorization: `Bearer ${user.accessToken}` }
    });
    const body = await response.json<{
      aiPolicyDefaults: AiPolicy;
      categories: Array<{ aiPolicy: AiPolicy }>;
      feeds: Array<{ aiPolicy: AiPolicy }>;
    }>();

    expect(response.status).toBe(200);
    expect(body.aiPolicyDefaults).toEqual({
      manualSummary: "ENABLED",
      autoSummary: "DISABLED",
      autoTranslation: "DISABLED",
      translationTargetLanguage: "zh-Hans",
      dailyEntryLimit: 20,
      maxConcurrency: 1
    });
    expect(body.categories[0]?.aiPolicy).toEqual({
      manualSummary: "DISABLED",
      autoSummary: "ENABLED",
      autoTranslation: "INHERIT",
      translationTargetLanguage: null,
      dailyEntryLimit: 12,
      maxConcurrency: 2
    });
    expect(body.feeds[0]?.aiPolicy).toEqual(inheritedPolicy());
    expect(JSON.stringify(body)).not.toMatch(/summaryText|translationText|articleContent/iu);
  });

  it("lets admins update a category policy with catalog versioning, idempotency, and minimal audit", async () => {
    const admin = await seedSession("admin");
    await seedCatalog();
    const policy: AiPolicy = {
      manualSummary: "DISABLED",
      autoSummary: "ENABLED",
      autoTranslation: "ENABLED",
      translationTargetLanguage: "en",
      dailyEntryLimit: 15,
      maxConcurrency: 2
    };

    const first = await patchCategoryPolicy(admin, 0, "category-ai-policy-0001", policy, "ai-policy-request-1");
    const firstText = await first.text();
    const replay = await patchCategoryPolicy(admin, 0, "category-ai-policy-0001", policy, "ai-policy-request-replay");
    const body = JSON.parse(firstText) as { catalogVersion: number; category: { aiPolicy: AiPolicy } };

    expect(first.status).toBe(200);
    expect(body.catalogVersion).toBe(1);
    expect(body.category.aiPolicy).toEqual(policy);
    expect(replay.status).toBe(200);
    expect(await replay.text()).toBe(firstText);
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1")).toBe(1);
    expect(await scalar(
      "SELECT COUNT(*) AS value FROM audit_events WHERE action='feed_category.updated' AND catalog_version=1"
    )).toBe(1);
    const audit = await env.DB.prepare(
      "SELECT target_type,target_id,action,request_id,catalog_version FROM audit_events " +
      "WHERE action='feed_category.updated'"
    ).first<Record<string, unknown>>();
    expect(audit).toEqual({
      target_type: "feed_category",
      target_id: categoryId,
      action: "feed_category.updated",
      request_id: "ai-policy-request-1",
      catalog_version: 1
    });
    expect(JSON.stringify(audit)).not.toContain("DISABLED");
  });

  it("rejects ordinary writers, stale versions, unknown fields, and unsafe limits without changing state", async () => {
    const user = await seedSession("user");
    const admin = await seedSession("admin");
    await seedCatalog();

    const denied = await patchCategoryPolicy(user, 0, "category-ai-policy-user", inheritedPolicy());
    expect(denied.status).toBe(403);

    const invalid = await patchCategoryPolicy(admin, 0, "category-ai-policy-invalid", {
      ...inheritedPolicy(),
      maxConcurrency: 5
    });
    expect(invalid.status).toBe(400);

    const unknown = await workerRequest(`/v1/admin/feed-categories/${categoryId}`, {
      method: "PATCH",
      headers: mutationHeaders(admin, 0, "category-ai-policy-unknown"),
      body: JSON.stringify({ aiPolicy: { ...inheritedPolicy(), prompt: "ignore policy" } })
    });
    expect(unknown.status).toBe(400);

    const accepted = await patchCategoryPolicy(admin, 0, "category-ai-policy-valid", {
      ...inheritedPolicy(),
      autoSummary: "ENABLED"
    });
    expect(accepted.status).toBe(200);
    const stale = await patchCategoryPolicy(admin, 0, "category-ai-policy-stale", {
      ...inheritedPolicy(),
      autoTranslation: "ENABLED"
    });
    expect(stale.status).toBe(409);
    await expect(errorCode(stale)).resolves.toBe("CATALOG_VERSION_CONFLICT");
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1")).toBe(1);
  });

  it("applies category and Feed policy overrides through one atomic catalog batch", async () => {
    const admin = await seedSession("admin");
    await seedCatalog();
    const response = await workerRequest("/v1/admin/feed-catalog-batches", {
      method: "POST",
      headers: mutationHeaders(admin, 0, "batch-ai-policy-update-0001"),
      body: JSON.stringify({
        operations: [
          {
            operationId: "category-policy",
            type: "PATCH_CATEGORY",
            categoryId,
            input: { aiPolicy: { autoSummary: "ENABLED", maxConcurrency: 2 } }
          },
          {
            operationId: "feed-policy",
            type: "PATCH_FEED",
            feedId,
            input: {
              aiPolicy: {
                manualSummary: "DISABLED",
                autoTranslation: "ENABLED",
                translationTargetLanguage: "ko",
                dailyEntryLimit: 8
              }
            }
          }
        ]
      })
    });

    expect(response.status).toBe(200);
    expect(await response.json<Record<string, unknown>>()).toMatchObject({ catalogVersion: 1 });
    expect(await env.DB.prepare(
      "SELECT ai_auto_summary_policy,ai_max_concurrency FROM feed_categories WHERE id=?"
    ).bind(categoryId).first()).toEqual({
      ai_auto_summary_policy: "ENABLED",
      ai_max_concurrency: 2
    });
    expect(await env.DB.prepare(
      "SELECT ai_manual_summary_policy,ai_auto_translation_policy,ai_translation_target_language," +
      "ai_daily_entry_limit FROM managed_feeds WHERE id=?"
    ).bind(feedId).first()).toEqual({
      ai_manual_summary_policy: "DISABLED",
      ai_auto_translation_policy: "ENABLED",
      ai_translation_target_language: "ko",
      ai_daily_entry_limit: 8
    });
  });
});

async function seedCatalog(): Promise<void> {
  await env.DB.batch([
    env.DB.prepare(
      "INSERT INTO feed_categories(id,name,name_norm,sort_order,is_enabled,version,created_at,updated_at) " +
      "VALUES(?,?,?,?,?,?,?,?)"
    ).bind(categoryId, "AI", "ai", 0, 1, 0, now, now),
    env.DB.prepare(
      "INSERT INTO managed_feeds(id,original_url,normalized_url,display_name,site_url,category_id,view_kind," +
      "refresh_interval_minutes,sort_order,is_enabled,version,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)"
    ).bind(
      feedId,
      "https://example.com/feed",
      "https://example.com/feed",
      "AI Feed",
      "https://example.com/",
      categoryId,
      "ARTICLE",
      60,
      0,
      1,
      0,
      now,
      now
    )
  ]);
}

function patchCategoryPolicy(
  session: Session,
  version: number,
  idempotencyKey: string,
  aiPolicy: unknown,
  requestId?: string
): Promise<Response> {
  const headers = mutationHeaders(session, version, idempotencyKey);
  if (requestId) headers.set("x-request-id", requestId);
  return workerRequest(`/v1/admin/feed-categories/${categoryId}`, {
    method: "PATCH",
    headers,
    body: JSON.stringify({ aiPolicy })
  });
}

function mutationHeaders(session: Session, version: number, idempotencyKey: string): Headers {
  return new Headers({
    authorization: `Bearer ${session.accessToken}`,
    "content-type": "application/json",
    "if-match": `"catalog-all-${version}"`,
    "idempotency-key": idempotencyKey
  });
}

function inheritedPolicy(): AiPolicy {
  return {
    manualSummary: "INHERIT",
    autoSummary: "INHERIT",
    autoTranslation: "INHERIT",
    translationTargetLanguage: null,
    dailyEntryLimit: null,
    maxConcurrency: null
  };
}

function inheritedPolicyRow(): Record<string, unknown> {
  return {
    ai_manual_summary_policy: "INHERIT",
    ai_auto_summary_policy: "INHERIT",
    ai_auto_translation_policy: "INHERIT",
    ai_translation_target_language: null,
    ai_daily_entry_limit: null,
    ai_max_concurrency: null
  };
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

function workerRequest(path: string, init?: RequestInit): Promise<Response> {
  return exports.default.fetch(new Request(`${baseUrl}${path}`, init));
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
