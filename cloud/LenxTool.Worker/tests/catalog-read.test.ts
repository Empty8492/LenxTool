import { env, exports } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";

const baseUrl = "https://worker.test";
const generatedAt = "2026-07-22T08:00:00.000Z";
const categoryIds = {
  beta: "10000000-0000-4000-8000-000000000001",
  alpha: "10000000-0000-4000-8000-000000000002",
  disabled: "10000000-0000-4000-8000-000000000003",
  deleted: "10000000-0000-4000-8000-000000000004"
} as const;
const feedIds = {
  zebra: "20000000-0000-4000-8000-000000000001",
  alpha: "20000000-0000-4000-8000-000000000002",
  beta: "20000000-0000-4000-8000-000000000003",
  disabledCategory: "20000000-0000-4000-8000-000000000004",
  disabled: "20000000-0000-4000-8000-000000000005",
  uncategorized: "20000000-0000-4000-8000-000000000006",
  deleted: "20000000-0000-4000-8000-000000000007"
} as const;

interface Session {
  accessToken: string;
}

interface CatalogCategory {
  id: string;
  name: string;
  sortOrder: number;
  isEnabled: boolean;
  aiPolicy: AiPolicy;
  version: number;
  createdAt: string;
  updatedAt: string;
}

interface CatalogFeed {
  id: string;
  originalUrl: string;
  normalizedUrl: string;
  displayName: string;
  siteUrl: string | null;
  categoryId: string | null;
  viewKind: string;
  isViewKindExplicit: boolean;
  fullTextPolicy: string;
  refreshIntervalMinutes: number;
  sortOrder: number;
  isEnabled: boolean;
  aiPolicy: AiPolicy;
  version: number;
  createdAt: string;
  updatedAt: string;
}

interface CatalogSnapshot {
  catalogVersion: number;
  scope: "ACTIVE" | "ALL";
  generatedAt: string;
  aiPolicyDefaults: AiPolicy;
  categories: CatalogCategory[];
  feeds: CatalogFeed[];
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
    ).bind(generatedAt),
    env.DB.prepare("DELETE FROM audit_events"),
    env.DB.prepare("DELETE FROM daily_usage"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM invites"),
    env.DB.prepare("DELETE FROM auth_attempts"),
    env.DB.prepare("DELETE FROM users")
  ]);
});

describe("Worker v1 feed catalog read route", () => {
  it("publishes a stable ACTIVE snapshot with only enabled non-deleted resources", async () => {
    const user = await seedSession("user");
    await seedCatalog();

    const first = await catalogRequest(user, "?scope=ACTIVE", "catalog-active-request");
    const firstText = await first.text();
    const snapshot = JSON.parse(firstText) as CatalogSnapshot;
    const second = await catalogRequest(user, "?scope=ACTIVE");

    expect(first.status).toBe(200);
    expect(first.headers.get("etag")).toBe('"catalog-active-7"');
    expect(first.headers.get("cache-control")).toBe("private, no-cache");
    expect(first.headers.get("vary")).toBe("Authorization");
    expect(first.headers.get("x-request-id")).toBe("catalog-active-request");
    expect(snapshot).toEqual({
      catalogVersion: 7,
      scope: "ACTIVE",
      generatedAt,
      aiPolicyDefaults: defaultAiPolicy(),
      categories: [categoryDto(categoryIds.alpha, "Alpha", 10, true), categoryDto(categoryIds.beta, "Beta", 10, true)],
      feeds: [
        feedDto(feedIds.alpha, "Alpha Feed", categoryIds.alpha, 10, true),
        feedDto(feedIds.zebra, "Zebra Feed", categoryIds.alpha, 10, true),
        feedDto(feedIds.beta, "Beta Feed", categoryIds.beta, 1, true),
        feedDto(feedIds.uncategorized, "Uncategorized", null, 0, true)
      ]
    });
    expect(await second.text()).toBe(firstText);
    expect(firstText).not.toMatch(/name_norm|deleted_at|last_mutation|password|token/iu);
  });

  it("lets admins read ALL while excluding soft-deleted rows", async () => {
    const admin = await seedSession("admin");
    await seedCatalog();

    const response = await catalogRequest(admin, "?scope=ALL");
    const snapshot = await response.json<CatalogSnapshot>();

    expect(response.status).toBe(200);
    expect(response.headers.get("etag")).toBe('"catalog-all-7"');
    expect(snapshot.categories.map(category => category.id)).toEqual([
      categoryIds.disabled,
      categoryIds.alpha,
      categoryIds.beta
    ]);
    expect(snapshot.feeds.map(feed => feed.id)).toEqual([
      feedIds.disabledCategory,
      feedIds.disabled,
      feedIds.alpha,
      feedIds.zebra,
      feedIds.beta,
      feedIds.uncategorized
    ]);
    expect(snapshot.categories.some(category => !category.isEnabled)).toBe(true);
    expect(snapshot.feeds.some(feed => !feed.isEnabled)).toBe(true);
    expect(JSON.stringify(snapshot)).not.toContain(categoryIds.deleted);
    expect(JSON.stringify(snapshot)).not.toContain(feedIds.deleted);
  });

  it("rejects anonymous access and prevents users from reading ALL", async () => {
    const user = await seedSession("user");
    await seedCatalog();

    const anonymous = await workerRequest("/v1/feeds/catalog");
    const forbidden = await catalogRequest(user, "?scope=ALL");

    expect(anonymous.status).toBe(401);
    expect(await errorCode(anonymous)).toBe("AUTH_REQUIRED");
    expect(forbidden.status).toBe(403);
    const forbiddenBody = await forbidden.json<{ error: Record<string, unknown> }>();
    expect(forbiddenBody.error.code).toBe("ADMIN_REQUIRED");
    expect(forbiddenBody.error).not.toHaveProperty("details");
  });

  it("returns 304 for an unchanged positive afterVersion and a full snapshot for an older version", async () => {
    const user = await seedSession("user");
    await seedCatalog();

    const unchanged = await catalogRequest(user, "?afterVersion=7", "catalog-unchanged");
    const older = await catalogRequest(user, "?afterVersion=6");

    expect(unchanged.status).toBe(304);
    expect(await unchanged.text()).toBe("");
    expect(unchanged.headers.get("etag")).toBe('"catalog-active-7"');
    expect(unchanged.headers.get("x-request-id")).toBe("catalog-unchanged");
    expect(older.status).toBe(200);
    expect((await older.json<CatalogSnapshot>()).catalogVersion).toBe(7);
  });

  it("honors a matching ETag and rejects contradictory cache conditions", async () => {
    const admin = await seedSession("admin");
    await seedCatalog();

    const matching = await catalogRequest(admin, "?scope=ALL", undefined, '"catalog-all-7"');
    const wrongScope = await catalogRequest(admin, "?scope=ALL", undefined, '"catalog-active-7"');
    const wrongVersion = await catalogRequest(admin, "?scope=ALL&afterVersion=7", undefined, '"catalog-all-6"');

    expect(matching.status).toBe(304);
    expect(matching.headers.get("etag")).toBe('"catalog-all-7"');
    expect(wrongScope.status).toBe(400);
    expect(await errorCode(wrongScope)).toBe("VALIDATION_ERROR");
    expect(wrongVersion.status).toBe(400);
    expect(await errorCode(wrongVersion)).toBe("VALIDATION_ERROR");
  });

  it("rejects an ahead version and validates every query parameter", async () => {
    const user = await seedSession("user");
    await seedCatalog();

    const ahead = await catalogRequest(user, "?afterVersion=8");
    const aheadBody = await ahead.json<{ error: { code: string; details: Record<string, unknown> } }>();
    const negative = await catalogRequest(user, "?afterVersion=-1");
    const unsafe = await catalogRequest(user, "?afterVersion=9007199254740992");
    const duplicate = await catalogRequest(user, "?scope=ACTIVE&scope=ACTIVE");
    const unknown = await catalogRequest(user, "?page=1");

    expect(ahead.status).toBe(409);
    expect(aheadBody.error).toMatchObject({
      code: "CATALOG_VERSION_AHEAD",
      details: { currentCatalogVersion: 7 }
    });
    for (const response of [negative, unsafe, duplicate, unknown]) {
      expect(response.status).toBe(400);
      expect(await errorCode(response)).toBe("VALIDATION_ERROR");
    }
  });

  it("returns the full empty snapshot when afterVersion is zero", async () => {
    const user = await seedSession("user");

    const response = await catalogRequest(user, "?afterVersion=0");
    const snapshot = await response.json<CatalogSnapshot>();

    expect(response.status).toBe(200);
    expect(response.headers.get("etag")).toBe('"catalog-active-0"');
    expect(snapshot).toEqual({
      catalogVersion: 0,
      scope: "ACTIVE",
      generatedAt,
      aiPolicyDefaults: defaultAiPolicy(),
      categories: [],
      feeds: []
    });
  });
});

async function seedCatalog(): Promise<void> {
  await env.DB.batch([
    categoryStatement(categoryIds.beta, "Beta", 10, 1),
    categoryStatement(categoryIds.alpha, "Alpha", 10, 1),
    categoryStatement(categoryIds.disabled, "Disabled", 0, 0),
    categoryStatement(categoryIds.deleted, "Deleted", 20, 1, generatedAt),
    feedStatement(feedIds.zebra, "Zebra Feed", categoryIds.alpha, 10, 1),
    feedStatement(feedIds.alpha, "Alpha Feed", categoryIds.alpha, 10, 1),
    feedStatement(feedIds.beta, "Beta Feed", categoryIds.beta, 1, 1),
    feedStatement(feedIds.disabledCategory, "Disabled Category Feed", categoryIds.disabled, 0, 0),
    feedStatement(feedIds.disabled, "Disabled Feed", categoryIds.alpha, 5, 0),
    feedStatement(feedIds.uncategorized, "Uncategorized", null, 0, 1),
    feedStatement(feedIds.deleted, "Deleted Feed", categoryIds.alpha, 0, 1, generatedAt),
    env.DB.prepare(
      "UPDATE feed_catalog_state SET catalog_version=7,updated_at=?,last_mutation_id=NULL WHERE singleton_id=1"
    ).bind(generatedAt)
  ]);
}

function categoryStatement(
  id: string,
  name: string,
  sortOrder: number,
  isEnabled: number,
  deletedAt: string | null = null
): D1PreparedStatement {
  return env.DB.prepare(
    "INSERT INTO feed_categories(id,name,name_norm,sort_order,is_enabled,deleted_at,version,created_at,updated_at) " +
    "VALUES(?,?,?,?,?,?,?,?,?)"
  ).bind(id, name, name.toLocaleLowerCase("en-US"), sortOrder, isEnabled, deletedAt, 7, generatedAt, generatedAt);
}

function feedStatement(
  id: string,
  displayName: string,
  categoryId: string | null,
  sortOrder: number,
  isEnabled: number,
  deletedAt: string | null = null
): D1PreparedStatement {
  const slug = id.at(-1);
  const url = `https://feed${slug}.example.com/rss`;
  return env.DB.prepare(
    "INSERT INTO managed_feeds(id,original_url,normalized_url,display_name,site_url,category_id,view_kind," +
    "refresh_interval_minutes,sort_order,is_enabled,deleted_at,version,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)"
  ).bind(
    id,
    url,
    url,
    displayName,
    `https://feed${slug}.example.com/`,
    categoryId,
    "ARTICLE",
    60,
    sortOrder,
    isEnabled,
    deletedAt,
    7,
    generatedAt,
    generatedAt
  );
}

function categoryDto(id: string, name: string, sortOrder: number, isEnabled: boolean): CatalogCategory {
  return {
    id,
    name,
    sortOrder,
    isEnabled,
    aiPolicy: inheritedAiPolicy(),
    version: 7,
    createdAt: generatedAt,
    updatedAt: generatedAt
  };
}

function feedDto(
  id: string,
  displayName: string,
  categoryId: string | null,
  sortOrder: number,
  isEnabled: boolean
): CatalogFeed {
  const slug = id.at(-1);
  const url = `https://feed${slug}.example.com/rss`;
  return {
    id,
    originalUrl: url,
    normalizedUrl: url,
    displayName,
    siteUrl: `https://feed${slug}.example.com/`,
    categoryId,
    viewKind: "ARTICLE",
    isViewKindExplicit: false,
    fullTextPolicy: "NONE",
    refreshIntervalMinutes: 60,
    sortOrder,
    isEnabled,
    aiPolicy: inheritedAiPolicy(),
    version: 7,
    createdAt: generatedAt,
    updatedAt: generatedAt
  };
}

function inheritedAiPolicy(): AiPolicy {
  return {
    manualSummary: "INHERIT",
    autoSummary: "INHERIT",
    autoTranslation: "INHERIT",
    translationTargetLanguage: null,
    dailyEntryLimit: null,
    maxConcurrency: null
  };
}

function defaultAiPolicy(): AiPolicy {
  return {
    manualSummary: "ENABLED",
    autoSummary: "DISABLED",
    autoTranslation: "DISABLED",
    translationTargetLanguage: "zh-Hans",
    dailyEntryLimit: 20,
    maxConcurrency: 1
  };
}

async function seedSession(role: "user" | "admin"): Promise<Session> {
  const userId = crypto.randomUUID();
  const now = new Date().toISOString();
  await env.DB.prepare(
    "INSERT INTO users(id,username,username_norm,password_salt,password_hash,role,ai_daily_limit,speech_daily_seconds,created_at,updated_at) " +
    "VALUES(?,?,?,?,?,?,?,?,?,?)"
  ).bind(userId, `${role}-${userId}`, `${role}-${userId}`, "unused-salt", "unused-hash", role, 0, 0, now, now).run();
  const issuedAt = Math.floor(Date.now() / 1000);
  return {
    accessToken: await signAccessToken({
      sub: userId,
      role,
      iat: issuedAt,
      exp: issuedAt + 900,
      jti: crypto.randomUUID()
    })
  };
}

function catalogRequest(
  session: Session,
  query = "",
  requestId?: string,
  etag?: string
): Promise<Response> {
  const headers = new Headers({ authorization: `Bearer ${session.accessToken}` });
  if (requestId) headers.set("x-request-id", requestId);
  if (etag) headers.set("if-none-match", etag);
  return workerRequest(`/v1/feeds/catalog${query}`, { headers });
}

function workerRequest(path: string, init?: RequestInit): Promise<Response> {
  return exports.default.fetch(new Request(`${baseUrl}${path}`, init));
}

async function errorCode(response: Response): Promise<string> {
  const body = await response.clone().json<{ error: { code: string } }>();
  return body.error.code;
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
