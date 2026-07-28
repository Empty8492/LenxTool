import { env, exports } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";

const baseUrl = "https://worker.test";
const generatedAt = "2026-07-27T12:00:00.000Z";
const categoryIds = {
  technology: "71000000-0000-4000-8000-000000000001",
  disabled: "71000000-0000-4000-8000-000000000002"
} as const;
const feedIds = {
  exactTitle: "72000000-0000-4000-8000-000000000001",
  titlePrefix: "72000000-0000-4000-8000-000000000002",
  titleContains: "72000000-0000-4000-8000-000000000003",
  categoryMatch: "72000000-0000-4000-8000-000000000004",
  disabled: "72000000-0000-4000-8000-000000000005",
  disabledCategory: "72000000-0000-4000-8000-000000000006",
  deleted: "72000000-0000-4000-8000-000000000007",
  exactUrl: "72000000-0000-4000-8000-000000000008"
} as const;

interface Session {
  accessToken: string;
}

interface DiscoveryEvidence {
  sourceId: string;
  sourceKind: "KNOWN_CATALOG";
  matchKind: "EXACT_FEED_URL" | "EXACT_SITE_URL" | "EXACT_TITLE" | "KEYWORD";
  confidence: "EXACT" | "HIGH" | "MEDIUM";
}

interface DiscoveryItem {
  normalizedFeedUrl: string;
  title: string;
  siteUrl: string | null;
  documentKind: null;
  lastUpdatedAt: string;
  health: "UNKNOWN";
  evidence: DiscoveryEvidence[];
  warnings: [];
  catalog: {
    feedId: string;
    categoryId: string | null;
    categoryName: string | null;
    viewKind: string;
    isEnabled: boolean;
  };
}

interface DiscoveryPage {
  catalogVersion: number;
  query: string;
  scope: "ACTIVE" | "ALL";
  items: DiscoveryItem[];
  pagination: {
    pageSize: number;
    totalItems: number;
    nextCursor: string | null;
  };
}

beforeEach(async () => {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM feed_discovery_rate_limits"),
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

describe("Worker v1 known feed discovery route", () => {
  it("returns stable ranked cursor pages with typed source evidence and cache validators", async () => {
    const user = await seedSession("user");
    await seedDiscoveryCatalog();

    const first = await discoveryRequest(
      user,
      "query=tech&pageSize=2",
      "discovery-page-one"
    );
    const firstText = await first.text();
    const firstPage = JSON.parse(firstText) as DiscoveryPage;
    const repeated = await discoveryRequest(user, "query=tech&pageSize=2");
    const second = await discoveryRequest(
      user,
      `query=tech&pageSize=2&cursor=${encodeURIComponent(firstPage.pagination.nextCursor!)}`
    );
    const secondPage = await second.json<DiscoveryPage>();

    expect(first.status).toBe(200);
    expect(first.headers.get("cache-control")).toBe("private, max-age=60");
    expect(first.headers.get("vary")).toBe("Authorization");
    expect(first.headers.get("etag")).toMatch(/^"feed-discovery-12-[A-Za-z0-9_-]{16}"$/u);
    expect(first.headers.get("x-request-id")).toBe("discovery-page-one");
    expect(firstPage).toMatchObject({
      catalogVersion: 12,
      query: "tech",
      scope: "ACTIVE",
      pagination: {
        pageSize: 2,
        totalItems: 4
      }
    });
    expect(firstPage.items.map(item => item.catalog.feedId)).toEqual([
      feedIds.exactTitle,
      feedIds.titlePrefix
    ]);
    expect(firstPage.items[0]).toMatchObject({
      title: "Tech",
      health: "UNKNOWN",
      documentKind: null,
      evidence: [{
        sourceId: "worker:known-catalog",
        sourceKind: "KNOWN_CATALOG",
        matchKind: "EXACT_TITLE",
        confidence: "HIGH"
      }],
      warnings: [],
      catalog: {
        feedId: feedIds.exactTitle,
        categoryName: "Technology",
        viewKind: "ARTICLE",
        isEnabled: true
      }
    });
    expect(firstPage.pagination.nextCursor).toMatch(/^[A-Za-z0-9_-]+$/u);
    expect(await repeated.text()).toBe(firstText);
    expect(secondPage.items.map(item => item.catalog.feedId)).toEqual([
      feedIds.titleContains,
      feedIds.categoryMatch
    ]);
    expect(secondPage.pagination).toEqual({
      pageSize: 2,
      totalItems: 4,
      nextCursor: null
    });

    const notModified = await discoveryRequest(
      user,
      "query=tech&pageSize=2",
      undefined,
      first.headers.get("etag")!
    );
    expect(notModified.status).toBe(304);
    expect(await notModified.text()).toBe("");
  });

  it("returns a typed empty page when the known catalog has no matches", async () => {
    const user = await seedSession("user");

    const response = await discoveryRequest(user, "query=missing");

    expect(response.status).toBe(200);
    expect(await response.json<DiscoveryPage>()).toEqual({
      catalogVersion: 0,
      query: "missing",
      scope: "ACTIVE",
      items: [],
      pagination: {
        pageSize: 20,
        totalItems: 0,
        nextCursor: null
      }
    });
  });

  it("ranks exact normalized and site URLs ahead of keyword matches", async () => {
    const user = await seedSession("user");
    await seedDiscoveryCatalog();

    const feedUrl = await discoveryRequest(
      user,
      `query=${encodeURIComponent("https://feeds.example.com/exact.xml")}`
    );
    const siteUrl = await discoveryRequest(
      user,
      `query=${encodeURIComponent("https://exact.example.com/")}`
    );
    const feedItem = (await feedUrl.json<DiscoveryPage>()).items[0]!;
    const siteItem = (await siteUrl.json<DiscoveryPage>()).items[0]!;

    expect(feedItem.catalog.feedId).toBe(feedIds.exactUrl);
    expect(feedItem.evidence[0]).toMatchObject({
      matchKind: "EXACT_FEED_URL",
      confidence: "EXACT"
    });
    expect(siteItem.catalog.feedId).toBe(feedIds.exactUrl);
    expect(siteItem.evidence[0]).toMatchObject({
      matchKind: "EXACT_SITE_URL",
      confidence: "EXACT"
    });
  });

  it("enforces ACTIVE scope for readers while allowing admins to inspect disabled catalog matches", async () => {
    const user = await seedSession("user");
    const admin = await seedSession("admin");
    await seedDiscoveryCatalog();

    const active = await discoveryRequest(user, "query=tech");
    const forbidden = await discoveryRequest(user, "query=tech&scope=ALL");
    const all = await discoveryRequest(admin, "query=tech&scope=ALL");
    const activePage = await active.json<DiscoveryPage>();
    const allPage = await all.json<DiscoveryPage>();

    expect(activePage.items.map(item => item.catalog.feedId)).not.toContain(feedIds.disabled);
    expect(activePage.items.map(item => item.catalog.feedId)).not.toContain(feedIds.disabledCategory);
    expect(forbidden.status).toBe(403);
    expect(await errorCode(forbidden)).toBe("ADMIN_REQUIRED");
    expect(allPage.items.map(item => item.catalog.feedId)).toEqual(
      expect.arrayContaining([feedIds.disabled, feedIds.disabledCategory])
    );
    expect(allPage.items.find(item => item.catalog.feedId === feedIds.disabled)?.catalog.isEnabled)
      .toBe(false);
    expect(JSON.stringify(allPage)).not.toContain(feedIds.deleted);
  });

  it("rejects anonymous, malformed, duplicated, oversized and cursor-mismatched queries", async () => {
    const user = await seedSession("user");
    await seedDiscoveryCatalog();

    const anonymous = await workerRequest("/v1/feeds/discoveries?query=tech");
    const valid = await discoveryRequest(user, "query=tech&pageSize=1");
    const cursor = (await valid.json<DiscoveryPage>()).pagination.nextCursor!;
    const literalWildcard = await discoveryRequest(user, "query=%25");
    const injectionAttempt = await discoveryRequest(
      user,
      `query=${encodeURIComponent("' OR 1=1 --")}`
    );
    const invalidRequests = [
      discoveryRequest(user, ""),
      discoveryRequest(user, "query="),
      discoveryRequest(user, `query=${"x".repeat(201)}`),
      discoveryRequest(user, "query=tech&query=other"),
      discoveryRequest(user, "query=tech&pageSize=0"),
      discoveryRequest(user, "query=tech&pageSize=51"),
      discoveryRequest(user, "query=tech&pageSize=01"),
      discoveryRequest(user, "query=tech&scope=UNKNOWN"),
      discoveryRequest(user, "query=tech&unknown=1"),
      discoveryRequest(user, "query=tech&cursor=not-base64"),
      discoveryRequest(
        user,
        `query=other&cursor=${encodeURIComponent(cursor)}`
      )
    ];

    expect(anonymous.status).toBe(401);
    expect(await errorCode(anonymous)).toBe("AUTH_REQUIRED");
    expect(await literalWildcard.json<DiscoveryPage>()).toMatchObject({
      items: [],
      pagination: { totalItems: 0 }
    });
    expect(await injectionAttempt.json<DiscoveryPage>()).toMatchObject({
      items: [],
      pagination: { totalItems: 0 }
    });
    for (const response of await Promise.all(invalidRequests)) {
      expect(response.status).toBe(400);
      expect(await errorCode(response)).toBe("VALIDATION_ERROR");
    }
  });

  it("rate limits each authenticated reader without mutating catalog state", async () => {
    const user = await seedSession("user");
    await seedDiscoveryCatalog();

    for (let index = 0; index < 60; index++) {
      const response = await discoveryRequest(user, "query=tech");
      expect(response.status).toBe(200);
    }
    const limited = await discoveryRequest(user, "query=tech");
    const writeAttempt = await workerRequest("/v1/feeds/discoveries", {
      method: "POST",
      headers: {
        authorization: `Bearer ${user.accessToken}`,
        "content-type": "application/json"
      },
      body: JSON.stringify({ normalizedFeedUrl: "https://attacker.example/feed.xml" })
    });

    expect(limited.status).toBe(429);
    expect(limited.headers.get("retry-after")).toBe("60");
    expect(await errorCode(limited)).toBe("RATE_LIMITED");
    expect(writeAttempt.status).toBe(404);
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1"))
      .toBe(12);
    expect(await scalar("SELECT COUNT(*) AS value FROM managed_feeds")).toBe(8);
  });

  it("returns the first keyword page within budget at the catalog capacity limit", async () => {
    const user = await seedSession("user");
    await seedCapacityCatalog();

    const startedAt = performance.now();
    const response = await discoveryRequest(
      user,
      "query=checkpoint&pageSize=50"
    );
    const page = await response.json<DiscoveryPage>();
    const elapsedMilliseconds = performance.now() - startedAt;

    expect(response.status).toBe(200);
    expect(page.pagination).toMatchObject({
      pageSize: 50,
      totalItems: 5000
    });
    expect(page.items).toHaveLength(50);
    expect(page.items.every(item => item.title.startsWith("Checkpoint Feed ")))
      .toBe(true);
    expect(elapsedMilliseconds).toBeLessThan(2000);
  });

  it("returns only the explicit discovery metadata allowlist", async () => {
    const user = await seedSession("user");
    await seedDiscoveryCatalog();

    const response = await discoveryRequest(user, "query=tech&scope=ACTIVE");
    const body = await response.text();

    expect(response.status).toBe(200);
    expect(body).not.toMatch(
      /"(?:originalUrl|nameNorm|deletedAt|aiPolicy|article|body|content|summaryText|translationText|prompt|localPath|password|token|userState)"\s*:/iu
    );
  });
});

async function seedDiscoveryCatalog(): Promise<void> {
  await env.DB.batch([
    categoryStatement(categoryIds.technology, "Technology", 1),
    categoryStatement(categoryIds.disabled, "Tech Disabled Category", 0),
    feedStatement(feedIds.exactTitle, "Tech", categoryIds.technology, 1, "2026-07-27T11:00:00.000Z"),
    feedStatement(feedIds.titlePrefix, "Tech Today", categoryIds.technology, 1, "2026-07-27T10:00:00.000Z"),
    feedStatement(feedIds.titleContains, "Daily Tech Brief", categoryIds.technology, 1, "2026-07-27T09:00:00.000Z"),
    feedStatement(feedIds.categoryMatch, "Engineering Dispatch", categoryIds.technology, 1, "2026-07-27T08:00:00.000Z"),
    feedStatement(feedIds.disabled, "Tech Disabled", categoryIds.technology, 0, "2026-07-27T07:00:00.000Z"),
    feedStatement(feedIds.disabledCategory, "Tech Hidden Category", categoryIds.disabled, 1, "2026-07-27T06:00:00.000Z"),
    feedStatement(feedIds.deleted, "Tech Deleted", categoryIds.technology, 1, "2026-07-27T05:00:00.000Z", generatedAt),
    feedStatement(
      feedIds.exactUrl,
      "Platform Exact",
      null,
      1,
      "2026-07-27T04:00:00.000Z",
      null,
      "https://feeds.example.com/exact.xml",
      "https://exact.example.com/"
    ),
    env.DB.prepare(
      "UPDATE feed_catalog_state SET catalog_version=12,updated_at=?,last_mutation_id=NULL WHERE singleton_id=1"
    ).bind(generatedAt)
  ]);
}

async function seedCapacityCatalog(): Promise<void> {
  await env.DB.prepare(
    "WITH digits(d) AS (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)), " +
      "numbers(n) AS (" +
      "SELECT ones.d + tens.d*10 + hundreds.d*100 + thousands.d*1000 + 1 " +
      "FROM digits ones CROSS JOIN digits tens CROSS JOIN digits hundreds CROSS JOIN digits thousands" +
      ") " +
      "INSERT INTO managed_feeds(" +
      "id,original_url,normalized_url,display_name,site_url,category_id,view_kind," +
      "refresh_interval_minutes,sort_order,is_enabled,deleted_at,version,created_at,updated_at" +
      ") " +
      "SELECT " +
      "'73000000-0000-4000-8000-' || printf('%012d', n)," +
      "'https://checkpoint-' || n || '.example/rss'," +
      "'https://checkpoint-' || n || '.example/rss'," +
      "'Checkpoint Feed ' || printf('%04d', n)," +
      "'https://checkpoint-' || n || '.example/'," +
      "NULL,'ARTICLE',60,n,1,NULL,1,?,? " +
      "FROM numbers WHERE n<=5000"
  ).bind(generatedAt, generatedAt).run();
  await env.DB.prepare(
    "UPDATE feed_catalog_state SET catalog_version=1,updated_at=?,last_mutation_id=NULL WHERE singleton_id=1"
  ).bind(generatedAt).run();
}

function categoryStatement(
  id: string,
  name: string,
  isEnabled: number
): D1PreparedStatement {
  return env.DB.prepare(
    "INSERT INTO feed_categories(id,name,name_norm,sort_order,is_enabled,version,created_at,updated_at) " +
      "VALUES(?,?,?,?,?,?,?,?)"
  ).bind(id, name, name.toLocaleLowerCase("en-US"), 10, isEnabled, 12, generatedAt, generatedAt);
}

function feedStatement(
  id: string,
  displayName: string,
  categoryId: string | null,
  isEnabled: number,
  updatedAt: string,
  deletedAt: string | null = null,
  normalizedUrl = `https://${id}.example.com/rss`,
  siteUrl = `https://${id}.example.com/`
): D1PreparedStatement {
  return env.DB.prepare(
    "INSERT INTO managed_feeds(id,original_url,normalized_url,display_name,site_url,category_id,view_kind," +
      "refresh_interval_minutes,sort_order,is_enabled,deleted_at,version,created_at,updated_at) " +
      "VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)"
  ).bind(
    id,
    normalizedUrl,
    normalizedUrl,
    displayName,
    siteUrl,
    categoryId,
    "ARTICLE",
    60,
    10,
    isEnabled,
    deletedAt,
    12,
    generatedAt,
    updatedAt
  );
}

async function seedSession(role: "user" | "admin"): Promise<Session> {
  const userId = crypto.randomUUID();
  const now = new Date().toISOString();
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
    0,
    0,
    now,
    now
  ).run();
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

function discoveryRequest(
  session: Session,
  query: string,
  requestId?: string,
  etag?: string
): Promise<Response> {
  const headers = new Headers({ authorization: `Bearer ${session.accessToken}` });
  if (requestId) headers.set("x-request-id", requestId);
  if (etag) headers.set("if-none-match", etag);
  const suffix = query.length === 0 ? "" : `?${query}`;
  return workerRequest(`/v1/feeds/discoveries${suffix}`, { headers });
}

function workerRequest(path: string, init?: RequestInit): Promise<Response> {
  return exports.default.fetch(new Request(`${baseUrl}${path}`, init));
}

async function errorCode(response: Response): Promise<string> {
  const body = await response.clone().json<{ error: { code: string } }>();
  return body.error.code;
}

async function scalar(sql: string): Promise<number> {
  const row = await env.DB.prepare(sql).first<{ value: number }>();
  if (!row) throw new Error("Expected scalar row");
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
