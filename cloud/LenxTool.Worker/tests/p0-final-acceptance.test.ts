import { env, exports } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";

const baseUrl = "https://worker.test";
const testPassword = "correct horse battery staple";
const inviteCode = "p0-final-acceptance-invite";

interface Session {
  user: { id: string; username: string; role: string };
  accessToken: string;
  refreshToken: string;
}

interface CategoryMutation {
  catalogVersion: number;
  category: {
    id: string;
    name: string;
    version: number;
  };
}

interface FeedMutation {
  catalogVersion: number;
  feed: {
    id: string;
    originalUrl: string;
    categoryId: string | null;
    isEnabled: boolean;
    version: number;
  };
}

interface CatalogSnapshot {
  catalogVersion: number;
  scope: "ACTIVE" | "ALL";
  categories: Array<{ id: string; name: string; isEnabled: boolean }>;
  feeds: Array<{
    id: string;
    originalUrl: string;
    categoryId: string | null;
    isEnabled: boolean;
  }>;
}

beforeEach(async () => {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM catalog_idempotency"),
    env.DB.prepare("DELETE FROM catalog_mutation_guards"),
    env.DB.prepare("DELETE FROM managed_feeds"),
    env.DB.prepare("DELETE FROM feed_categories"),
    env.DB.prepare(
      "UPDATE feed_catalog_state SET catalog_version=0,updated_at=?,last_mutation_id=NULL WHERE singleton_id=1"
    ).bind("2026-07-23T00:00:00.000Z"),
    env.DB.prepare("DELETE FROM audit_events"),
    env.DB.prepare("DELETE FROM daily_usage"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM invites"),
    env.DB.prepare("DELETE FROM auth_attempts"),
    env.DB.prepare("DELETE FROM users")
  ]);
});

describe("P0 final administrator/read-only acceptance", () => {
  it("completes login, publish, refresh, read, disable and audit with user write isolation", async () => {
    const admin = await bootstrapAndLogin();

    const categoryResponse = await adminMutation("/v1/admin/feed-categories", admin, 0, "p0-category-create", {
      method: "POST",
      body: { name: "Technology", sortOrder: 10, isEnabled: true }
    });
    expect(categoryResponse.status).toBe(201);
    const category = await categoryResponse.json<CategoryMutation>();

    const feedResponse = await adminMutation("/v1/admin/feeds", admin, 1, "p0-feed-create-0001", {
      method: "POST",
      body: {
        originalUrl: "https://example.com/p0.xml",
        displayName: "P0 Example Feed",
        siteUrl: "https://example.com",
        categoryId: category.category.id,
        viewKind: "ARTICLE",
        refreshIntervalMinutes: 60,
        sortOrder: 10,
        isEnabled: true
      }
    });
    const feedResponseText = await feedResponse.text();
    expect(feedResponse.status, feedResponseText).toBe(201);
    const feed = JSON.parse(feedResponseText) as FeedMutation;
    expect(feed.catalogVersion).toBe(2);
    expect(feed.feed.categoryId).toBe(category.category.id);

    const refreshedAdminCatalog = await readCatalog(admin, "ALL", "p0-admin-refresh");
    expect(refreshedAdminCatalog).toMatchObject({
      catalogVersion: 2,
      scope: "ALL",
      categories: [{ id: category.category.id, name: "Technology", isEnabled: true }],
      feeds: [
        {
          id: feed.feed.id,
          originalUrl: "https://example.com/p0.xml",
          categoryId: category.category.id,
          isEnabled: true
        }
      ]
    });

    const user = await registerReader();
    const userCatalog = await readCatalog(user, "ACTIVE", "p0-user-read");
    expect(userCatalog).toMatchObject({
      catalogVersion: 2,
      scope: "ACTIVE",
      categories: [{ id: category.category.id, name: "Technology", isEnabled: true }],
      feeds: [
        {
          id: feed.feed.id,
          originalUrl: "https://example.com/p0.xml",
          categoryId: category.category.id,
          isEnabled: true
        }
      ]
    });

    const missingId = crypto.randomUUID();
    const deniedRoutes: Array<{
      path: string;
      method: "POST" | "PATCH" | "DELETE";
      body?: Record<string, unknown>;
    }> = [
      {
        path: "/v1/admin/feed-categories",
        method: "POST",
        body: { name: "Denied", sortOrder: 20, isEnabled: true }
      },
      {
        path: `/v1/admin/feed-categories/${missingId}`,
        method: "PATCH",
        body: { name: "Denied" }
      },
      { path: `/v1/admin/feed-categories/${missingId}`, method: "DELETE" },
      {
        path: "/v1/admin/feeds",
        method: "POST",
        body: { originalUrl: "https://example.com/denied.xml", displayName: "Denied" }
      },
      {
        path: `/v1/admin/feeds/${missingId}`,
        method: "PATCH",
        body: { displayName: "Denied" }
      },
      { path: `/v1/admin/feeds/${missingId}`, method: "DELETE" }
    ];

    for (const [index, route] of deniedRoutes.entries()) {
      const response = await adminMutation(route.path, user, 2, `p0-user-write-000${index}`, route);
      expect(response.status).toBe(403);
      await expect(errorBody(response)).resolves.toMatchObject({ code: "ADMIN_REQUIRED" });
    }
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1")).toBe(2);
    expect(await scalar("SELECT COUNT(*) AS value FROM managed_feeds WHERE deleted_at IS NULL")).toBe(1);
    expect(await scalar("SELECT COUNT(*) AS value FROM feed_categories WHERE deleted_at IS NULL")).toBe(1);

    const disableResponse = await adminMutation(
      `/v1/admin/feeds/${feed.feed.id}`,
      admin,
      2,
      "p0-feed-disable-0001",
      { method: "PATCH", body: { isEnabled: false } }
    );
    expect(disableResponse.status).toBe(200);
    const disabled = await disableResponse.json<FeedMutation>();
    expect(disabled.catalogVersion).toBe(3);
    expect(disabled.feed.isEnabled).toBe(false);

    const activeAfterDisable = await readCatalog(user, "ACTIVE", "p0-user-refresh-disabled");
    expect(activeAfterDisable.catalogVersion).toBe(3);
    expect(activeAfterDisable.feeds).toEqual([]);

    const allAfterDisable = await readCatalog(admin, "ALL", "p0-admin-audit-refresh");
    expect(allAfterDisable.feeds).toMatchObject([
      {
        id: feed.feed.id,
        isEnabled: false
      }
    ]);

    const audits = await env.DB.prepare(
      "SELECT action,target_type,target_id,catalog_version,request_id FROM audit_events " +
        "WHERE target_id IN (?,?) ORDER BY catalog_version,action"
    ).bind(category.category.id, feed.feed.id).all<{
      action: string;
      target_type: string;
      target_id: string;
      catalog_version: number;
      request_id: string;
    }>();
    expect(audits.results).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          action: "feed_category.created",
          target_type: "feed_category",
          target_id: category.category.id,
          catalog_version: 1,
          request_id: expect.any(String)
        }),
        expect.objectContaining({
          action: "feed.created",
          target_type: "feed",
          target_id: feed.feed.id,
          catalog_version: 2,
          request_id: expect.any(String)
        }),
        expect.objectContaining({
          action: "feed.updated",
          target_type: "feed",
          target_id: feed.feed.id,
          catalog_version: 3,
          request_id: expect.any(String)
        })
      ])
    );
    expect(JSON.stringify(audits.results)).not.toMatch(/password|token|originalUrl|response/iu);
  });
});

async function bootstrapAndLogin(): Promise<Session> {
  const bootstrap = await workerRequest("/v1/bootstrap/admin", {
    method: "POST",
    headers: {
      "content-type": "application/json",
      authorization: `Bootstrap ${env.BOOTSTRAP_TOKEN}`
    },
    body: JSON.stringify({ username: "p0-owner", password: testPassword })
  });
  expect(bootstrap.status).toBe(201);

  const login = await workerRequest("/v1/auth/login", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ username: "p0-owner", password: testPassword })
  });
  expect(login.status).toBe(200);
  return login.json<Session>();
}

async function registerReader(): Promise<Session> {
  const owner = await env.DB.prepare("SELECT id FROM users WHERE username_norm=?")
    .bind("p0-owner")
    .first<{ id: string }>();
  if (!owner) throw new Error("Bootstrap owner was not persisted");
  await env.DB.prepare(
    "INSERT INTO invites(id,code_hash,created_by,role,ai_daily_limit,speech_daily_seconds,max_uses,created_at) " +
      "VALUES(?,?,?,?,?,?,?,?)"
  ).bind(
    crypto.randomUUID(),
    await sha256(inviteCode),
    owner.id,
    "user",
    20,
    900,
    1,
    new Date().toISOString()
  ).run();

  const response = await workerRequest("/v1/auth/register", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ username: "p0-reader", password: testPassword, inviteCode })
  });
  expect(response.status).toBe(201);
  return response.json<Session>();
}

async function adminMutation(
  path: string,
  session: Session,
  version: number,
  key: string,
  options: {
    method: "POST" | "PATCH" | "DELETE";
    body?: Record<string, unknown>;
  }
): Promise<Response> {
  const headers = new Headers({
    "content-type": "application/json",
    authorization: `Bearer ${session.accessToken}`,
    "if-match": `"catalog-all-${version}"`,
    "idempotency-key": key
  });
  return workerRequest(path, {
    method: options.method,
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body)
  });
}

async function readCatalog(
  session: Session,
  scope: "ACTIVE" | "ALL",
  requestId: string
): Promise<CatalogSnapshot> {
  const response = await workerRequest(`/v1/feeds/catalog?scope=${scope}`, {
    headers: {
      authorization: `Bearer ${session.accessToken}`,
      "x-request-id": requestId
    }
  });
  expect(response.status).toBe(200);
  expect(response.headers.get("x-request-id")).toBe(requestId);
  return response.json<CatalogSnapshot>();
}

function workerRequest(path: string, init?: RequestInit): Promise<Response> {
  return exports.default.fetch(new Request(`${baseUrl}${path}`, init));
}

async function errorBody(response: Response): Promise<Record<string, unknown>> {
  const body = await response.clone().json<{ error: Record<string, unknown> }>();
  return body.error;
}

async function scalar(query: string): Promise<number> {
  const row = await env.DB.prepare(query).first<{ value: number }>();
  if (!row) throw new Error(`Expected scalar query result: ${query}`);
  return row.value;
}

async function sha256(value: string): Promise<string> {
  const bytes = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value)));
  return toBase64Url(bytes);
}

function toBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}
