import { env, exports } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";

const baseUrl = "https://worker.test";
const now = "2026-07-28T09:00:00.000Z";
const feedId = "20000000-0000-4000-8000-000000000001";
const categoryId = "10000000-0000-4000-8000-000000000001";

interface Session {
  userId: string;
  accessToken: string;
}

interface SmartView {
  id: string;
  version: number;
  name: string;
  isEnabled: boolean;
  sortOrder: number;
  filter: Record<string, unknown>;
}

beforeEach(async () => {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM smart_view_versions"),
    env.DB.prepare("DELETE FROM smart_views"),
    env.DB.prepare(
      "UPDATE smart_view_state SET view_set_version=0,updated_at=?,last_mutation_id=NULL " +
      "WHERE singleton_id=1"
    ).bind(now),
    env.DB.prepare("DELETE FROM catalog_idempotency"),
    env.DB.prepare("DELETE FROM catalog_mutation_guards"),
    env.DB.prepare("DELETE FROM audit_events"),
    env.DB.prepare("DELETE FROM daily_usage"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM invites"),
    env.DB.prepare("DELETE FROM auth_attempts"),
    env.DB.prepare("DELETE FROM users")
  ]);
});

describe("Worker shared smart views", () => {
  it("migrates bounded filter-only tables without content columns", async () => {
    const columns = await tableColumns("smart_views");
    const state = await env.DB.prepare(
      "SELECT singleton_id,view_set_version,last_mutation_id FROM smart_view_state"
    ).first<Record<string, unknown>>();

    expect(state).toEqual({
      singleton_id: 1,
      view_set_version: 0,
      last_mutation_id: null
    });
    expect(columns).toEqual([
      "id", "current_version", "name", "sort_order", "is_enabled", "feed_id",
      "category_id", "view_kind", "read_filter", "favorites_only", "search_text",
      "published_within_days", "created_by", "updated_by", "created_at", "updated_at",
      "last_mutation_id"
    ]);
    expect(columns.join(" ")).not.toMatch(
      /content|summary|translation|token|url|read_at/iu
    );
  });

  it("publishes versions while ordinary users receive enabled read-only views", async () => {
    const admin = await seedSession("admin");
    const user = await seedSession("user");
    const createdResponse = await mutate(
      admin,
      "/v1/admin/smart-views",
      "POST",
      0,
      "smart-view-create-0001",
      validInput()
    );
    const createdBody = await createdResponse.clone().json<{
      viewSetVersion: number;
      view: SmartView;
    }>();

    expect(createdResponse.status).toBe(201);
    expect(createdBody).toMatchObject({
      viewSetVersion: 1,
      view: {
        version: 1,
        name: "AI 视频收藏",
        isEnabled: true,
        sortOrder: 20,
        filter: {
          feedId,
          categoryId,
          viewKind: "VIDEO",
          readFilter: "UNREAD",
          favoritesOnly: true,
          searchText: "release",
          publishedWithinDays: 30
        }
      }
    });

    const active = await readViews(user, "ACTIVE");
    expect(active.status).toBe(200);
    expect(active.headers.get("etag")).toBe('"smart-views-active-1"');
    expect(active.headers.get("cache-control")).toBe(
      "no-store, no-transform"
    );
    expect((await active.json<{ views: SmartView[] }>()).views).toHaveLength(1);

    const disabled = await mutate(
      admin,
      `/v1/admin/smart-views/${createdBody.view.id}`,
      "PATCH",
      1,
      "smart-view-disable-0001",
      { ...validInput(), isEnabled: false }
    );
    expect(disabled.status).toBe(200);
    expect((await readViews(user, "ACTIVE").then(response => response.json<{
      views: SmartView[];
    }>())).views).toEqual([]);
    expect((await readViews(admin, "ALL").then(response => response.json<{
      views: SmartView[];
    }>())).views).toMatchObject([
      { id: createdBody.view.id, version: 2, isEnabled: false }
    ]);

    const deleted = await mutate(
      admin,
      `/v1/admin/smart-views/${createdBody.view.id}`,
      "DELETE",
      2,
      "smart-view-delete-0001"
    );
    expect(deleted.status).toBe(200);
    expect(await scalar(
      "SELECT view_set_version AS value FROM smart_view_state"
    )).toBe(3);
    expect(await scalar(
      "SELECT COUNT(*) AS value FROM smart_view_versions WHERE view_id=?",
      createdBody.view.id
    )).toBe(2);
    expect(await scalar(
      "SELECT COUNT(*) AS value FROM audit_events WHERE target_type='smart_view'"
    )).toBe(3);
  });

  it("enforces RBAC, optimistic concurrency and request idempotency", async () => {
    const admin = await seedSession("admin");
    const user = await seedSession("user");
    const denied = await mutate(
      user,
      "/v1/admin/smart-views",
      "POST",
      0,
      "smart-view-user-denied",
      validInput()
    );
    const accepted = await mutate(
      admin,
      "/v1/admin/smart-views",
      "POST",
      0,
      "smart-view-version-one",
      validInput()
    );
    const replay = await mutate(
      admin,
      "/v1/admin/smart-views",
      "POST",
      0,
      "smart-view-version-one",
      validInput()
    );
    const stale = await mutate(
      admin,
      "/v1/admin/smart-views",
      "POST",
      0,
      "smart-view-version-stale",
      { ...validInput(), name: "另一个视图" }
    );

    expect(denied.status).toBe(403);
    expect(await errorCode(denied)).toBe("ADMIN_REQUIRED");
    expect(accepted.status).toBe(201);
    expect(replay.status).toBe(201);
    expect(await replay.text()).toBe(await accepted.clone().text());
    expect(stale.status).toBe(409);
    expect(await errorCode(stale)).toBe("SMART_VIEW_VERSION_CONFLICT");
    expect((await readViews(user, "ALL")).status).toBe(403);
    expect(await scalar("SELECT COUNT(*) AS value FROM smart_views")).toBe(1);
  });

  it("rejects scripts, URLs, unknown fields and out-of-range filters before writes", async () => {
    const admin = await seedSession("admin");
    const invalid = [
      { ...validInput(), script: "SELECT * FROM feed_entries" },
      { ...validInput(), filter: { ...validFilter(), url: "https://evil.example" } },
      { ...validInput(), filter: { ...validFilter(), feedId: "not-a-guid" } },
      { ...validInput(), filter: { ...validFilter(), viewKind: "WEB" } },
      { ...validInput(), filter: { ...validFilter(), searchText: "x".repeat(201) } },
      { ...validInput(), filter: { ...validFilter(), publishedWithinDays: 366 } }
    ];
    for (const [index, input] of invalid.entries()) {
      const response = await mutate(
        admin,
        "/v1/admin/smart-views",
        "POST",
        0,
        `smart-view-invalid-${index}`,
        input
      );
      expect(response.status, `invalid ${index}`).toBe(400);
      expect(await errorCode(response)).toBe("VALIDATION_ERROR");
    }
    expect(await scalar(
      "SELECT view_set_version AS value FROM smart_view_state"
    )).toBe(0);
    expect(await scalar("SELECT COUNT(*) AS value FROM smart_views")).toBe(0);
  });

  it("rejects a DELETE body before changing the view-set version", async () => {
    const admin = await seedSession("admin");
    const response = await mutate(
      admin,
      `/v1/admin/smart-views/${crypto.randomUUID()}`,
      "DELETE",
      0,
      "smart-view-delete-body",
      validInput()
    );

    expect(response.status).toBe(400);
    expect(await errorCode(response)).toBe("VALIDATION_ERROR");
    expect(await scalar(
      "SELECT view_set_version AS value FROM smart_view_state"
    )).toBe(0);
    expect(await scalar("SELECT COUNT(*) AS value FROM smart_views")).toBe(0);
  });

  it("supports conditional reads and rejects a client version ahead of the server", async () => {
    const admin = await seedSession("admin");
    const user = await seedSession("user");
    await mutate(
      admin,
      "/v1/admin/smart-views",
      "POST",
      0,
      "smart-view-cache-one",
      validInput()
    );

    const unchanged = await workerRequest(
      "/v1/smart-views?scope=ACTIVE&afterVersion=1",
      { headers: { authorization: `Bearer ${user.accessToken}` } }
    );
    const ahead = await workerRequest(
      "/v1/smart-views?scope=ACTIVE&afterVersion=2",
      { headers: { authorization: `Bearer ${user.accessToken}` } }
    );

    expect(unchanged.status).toBe(304);
    expect(unchanged.headers.get("cache-control")).toBe(
      "no-store, no-transform"
    );
    expect(await unchanged.text()).toBe("");
    expect(ahead.status).toBe(409);
    expect(await errorCode(ahead)).toBe("SMART_VIEW_VERSION_AHEAD");
  });
});

function validInput(): Record<string, unknown> {
  return {
    name: "  AI 视频收藏  ",
    sortOrder: 20,
    isEnabled: true,
    filter: validFilter()
  };
}

function validFilter(): Record<string, unknown> {
  return {
    feedId,
    categoryId,
    viewKind: "VIDEO",
    readFilter: "UNREAD",
    favoritesOnly: true,
    searchText: "  release  ",
    publishedWithinDays: 30
  };
}

function mutate(
  session: Session,
  path: string,
  method: "POST" | "PATCH" | "DELETE",
  version: number,
  key: string,
  body?: Record<string, unknown>
): Promise<Response> {
  const headers = new Headers({
    authorization: `Bearer ${session.accessToken}`,
    "if-match": `"smart-views-all-${version}"`,
    "idempotency-key": key
  });
  if (body) headers.set("content-type", "application/json");
  return workerRequest(path, {
    method,
    headers,
    body: body ? JSON.stringify(body) : undefined
  });
}

function readViews(
  session: Session,
  scope: "ACTIVE" | "ALL"
): Promise<Response> {
  return workerRequest(`/v1/smart-views?scope=${scope}`, {
    headers: { authorization: `Bearer ${session.accessToken}` }
  });
}

function workerRequest(path: string, init?: RequestInit): Promise<Response> {
  return exports.default.fetch(new Request(`${baseUrl}${path}`, init));
}

async function errorCode(response: Response): Promise<string> {
  return (await response.clone().json<{
    error: { code: string };
  }>()).error.code;
}

async function scalar(
  query: string,
  ...parameters: unknown[]
): Promise<number> {
  const row = await env.DB.prepare(query)
    .bind(...parameters)
    .first<{ value: number }>();
  if (!row) throw new Error(`Missing scalar: ${query}`);
  return row.value;
}

async function tableColumns(table: string): Promise<string[]> {
  const result = await env.DB.prepare(
    `PRAGMA table_info(${table})`
  ).all<{ name: string }>();
  return result.results.map(row => row.name);
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

async function signAccessToken(
  claims: Record<string, unknown>
): Promise<string> {
  const encoder = new TextEncoder();
  const header = toBase64Url(
    encoder.encode(JSON.stringify({ alg: "HS256", typ: "JWT" }))
  );
  const payload = toBase64Url(
    encoder.encode(JSON.stringify(claims))
  );
  const key = await crypto.subtle.importKey(
    "raw",
    encoder.encode(env.TOKEN_SECRET),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"]
  );
  const signature = new Uint8Array(
    await crypto.subtle.sign(
      "HMAC",
      key,
      encoder.encode(`${header}.${payload}`)
    )
  );
  return `${header}.${payload}.${toBase64Url(signature)}`;
}

function toBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary)
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replace(/=+$/u, "");
}
