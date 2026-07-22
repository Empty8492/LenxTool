import { env, exports } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";

const baseUrl = "https://worker.test";

interface Session {
  userId: string;
  accessToken: string;
}

interface CategoryMutation {
  catalogVersion: number;
  category: {
    id: string;
    name: string;
    sortOrder: number;
    isEnabled: boolean;
    version: number;
  };
}

interface FeedMutation {
  catalogVersion: number;
  feed: {
    id: string;
    originalUrl: string;
    normalizedUrl: string;
    displayName: string;
    categoryId: string | null;
    viewKind: string;
    refreshIntervalMinutes: number;
    sortOrder: number;
    isEnabled: boolean;
    version: number;
  };
}

interface CatalogBatchMutation {
  catalogVersion: number;
  results: Array<{
    operationId: string;
    resourceType: "FEED_CATEGORY" | "FEED";
    resourceId: string;
  }>;
}

beforeEach(async () => {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM managed_feeds"),
    env.DB.prepare("DELETE FROM feed_categories"),
    env.DB.prepare("UPDATE feed_catalog_state SET catalog_version=0,updated_at=? WHERE singleton_id=1")
      .bind("2026-07-22T00:00:00.000Z"),
    env.DB.prepare("DELETE FROM audit_events"),
    env.DB.prepare("DELETE FROM daily_usage"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM invites"),
    env.DB.prepare("DELETE FROM auth_attempts"),
    env.DB.prepare("DELETE FROM users")
  ]);
});

describe("Worker v1 administrator feed catalog routes", () => {
  it("creates a category with a new catalog version and a minimal audit event", async () => {
    const admin = await seedSession("admin");

    const response = await mutationRequest("/v1/admin/feed-categories", admin, 0, "category-create-0001", {
      method: "POST",
      requestId: "request-category-create",
      body: { name: "  技术  ", sortOrder: 100, isEnabled: true }
    });
    const body = await response.json<CategoryMutation>();

    expect(response.status).toBe(201);
    expect(body).toMatchObject({
      catalogVersion: 1,
      category: {
        id: expect.any(String),
        name: "技术",
        sortOrder: 100,
        isEnabled: true,
        version: 1
      }
    });
    const stored = await env.DB.prepare(
      "SELECT name,name_norm,sort_order,is_enabled,version,deleted_at FROM feed_categories WHERE id=?"
    ).bind(body.category.id).first<Record<string, unknown>>();
    expect(stored).toEqual({
      name: "技术",
      name_norm: "技术",
      sort_order: 100,
      is_enabled: 1,
      version: 1,
      deleted_at: null
    });
    const audit = await env.DB.prepare(
      "SELECT target_type,target_id,action,request_id,catalog_version FROM audit_events WHERE action='feed_category.created'"
    ).first<Record<string, unknown>>();
    expect(audit).toEqual({
      target_type: "feed_category",
      target_id: body.category.id,
      action: "feed_category.created",
      request_id: "request-category-create",
      catalog_version: 1
    });
    expect(JSON.stringify(audit)).not.toContain("技术");
  });

  it("updates and soft-deletes a category without losing its history", async () => {
    const admin = await seedSession("admin");
    const created = await createCategory(admin, 0, "category-lifecycle-create", {
      name: "技术",
      sortOrder: 100,
      isEnabled: true
    });

    const patchedResponse = await mutationRequest(
      `/v1/admin/feed-categories/${created.category.id}`,
      admin,
      1,
      "category-lifecycle-patch",
      {
        method: "PATCH",
        body: { name: "工程", sortOrder: 200, isEnabled: false }
      }
    );
    const patched = await patchedResponse.json<CategoryMutation>();
    expect(patchedResponse.status).toBe(200);
    expect(patched).toMatchObject({
      catalogVersion: 2,
      category: { id: created.category.id, name: "工程", sortOrder: 200, isEnabled: false, version: 2 }
    });

    const deletedResponse = await mutationRequest(
      `/v1/admin/feed-categories/${created.category.id}`,
      admin,
      2,
      "category-lifecycle-delete",
      { method: "DELETE" }
    );
    expect(deletedResponse.status).toBe(200);
    await expect(deletedResponse.json()).resolves.toEqual({
      catalogVersion: 3,
      deletedId: created.category.id,
      resourceType: "FEED_CATEGORY"
    });
    const stored = await env.DB.prepare(
      "SELECT name,is_enabled,version,deleted_at FROM feed_categories WHERE id=?"
    ).bind(created.category.id).first<{ name: string; is_enabled: number; version: number; deleted_at: string | null }>();
    expect(stored).toMatchObject({ name: "工程", is_enabled: 0, version: 3 });
    expect(stored?.deleted_at).toEqual(expect.any(String));
  });

  it("rejects anonymous and ordinary users before exposing catalog state", async () => {
    const user = await seedSession("user");
    const missingId = crypto.randomUUID();
    const routes: Array<{
      path: string;
      method: "POST" | "PATCH" | "DELETE";
      body?: Record<string, unknown>;
    }> = [
      { path: "/v1/admin/feed-categories", method: "POST", body: { name: "Forbidden" } },
      { path: `/v1/admin/feed-categories/${missingId}`, method: "PATCH", body: { name: "Forbidden" } },
      { path: `/v1/admin/feed-categories/${missingId}`, method: "DELETE" },
      { path: "/v1/admin/feeds", method: "POST", body: { originalUrl: "https://example.com/feed.xml" } },
      { path: `/v1/admin/feeds/${missingId}`, method: "PATCH", body: { displayName: "Forbidden" } },
      { path: `/v1/admin/feeds/${missingId}`, method: "DELETE" }
    ];

    for (const [index, route] of routes.entries()) {
      const userResponse = await mutationRequest(route.path, user, 999, `catalog-user-denied-${index}`, {
        method: route.method,
        body: route.body
      });
      expect(userResponse.status).toBe(403);
      const userError = await errorBody(userResponse);
      expect(userError).toMatchObject({ code: "ADMIN_REQUIRED" });
      expect(userError).not.toHaveProperty("details");

      const anonymousResponse = await workerRequest(route.path, {
        method: route.method,
        headers: catalogHeaders(999, `catalog-anonymous-${index}`),
        body: route.body === undefined ? undefined : JSON.stringify(route.body)
      });
      expect(anonymousResponse.status).toBe(401);
      const anonymousError = await errorBody(anonymousResponse);
      expect(anonymousError).toMatchObject({ code: "AUTH_REQUIRED" });
      expect(anonymousError).not.toHaveProperty("details");
    }
    const categoryCount = await scalar("SELECT COUNT(*) AS value FROM feed_categories");
    const feedCount = await scalar("SELECT COUNT(*) AS value FROM managed_feeds");
    const catalogVersion = await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1");
    expect(categoryCount).toBe(0);
    expect(feedCount).toBe(0);
    expect(catalogVersion).toBe(0);
    expect(await scalar("SELECT COUNT(*) AS value FROM audit_events WHERE action LIKE 'feed%'")).toBe(0);
  });

  it("replays the same idempotent result and rejects key reuse or a stale version", async () => {
    const admin = await seedSession("admin");
    const request = {
      method: "POST" as const,
      body: { name: "Technology", sortOrder: 10, isEnabled: true }
    };

    const first = await mutationRequest(
      "/v1/admin/feed-categories",
      admin,
      0,
      "category-idempotent-0001",
      { ...request, requestId: "idempotency-first" }
    );
    const firstText = await first.text();
    const replay = await mutationRequest(
      "/v1/admin/feed-categories",
      admin,
      0,
      "category-idempotent-0001",
      {
        method: "POST",
        body: { isEnabled: true, name: "Technology", sortOrder: 10 },
        requestId: "idempotency-replay"
      }
    );
    const reused = await mutationRequest(
      "/v1/admin/feed-categories",
      admin,
      0,
      "category-idempotent-0001",
      { method: "POST", body: { name: "Different", sortOrder: 10, isEnabled: true } }
    );
    const stale = await mutationRequest(
      "/v1/admin/feed-categories",
      admin,
      0,
      "category-stale-version",
      { method: "POST", body: { name: "Stale", sortOrder: 10, isEnabled: true } }
    );

    expect(first.status).toBe(201);
    expect(replay.status).toBe(201);
    expect(await replay.text()).toBe(firstText);
    expect(reused.status).toBe(409);
    await expect(errorBody(reused)).resolves.toMatchObject({ code: "IDEMPOTENCY_KEY_REUSED" });
    expect(stale.status).toBe(409);
    await expect(errorBody(stale)).resolves.toMatchObject({
      code: "CATALOG_VERSION_CONFLICT",
      isRetryable: true,
      details: { currentCatalogVersion: 1 }
    });
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1")).toBe(1);
    expect(await scalar("SELECT COUNT(*) AS value FROM feed_categories")).toBe(1);
    expect(await scalar("SELECT COUNT(*) AS value FROM audit_events WHERE action='feed_category.created'")).toBe(1);
  });

  it("creates, moves, disables and soft-deletes a Feed while preserving meaningful query parameters", async () => {
    const admin = await seedSession("admin");
    const category = await createCategory(admin, 0, "feed-category-create", {
      name: "News",
      sortOrder: 10,
      isEnabled: true
    });
    const createResponse = await mutationRequest("/v1/admin/feeds", admin, 1, "feed-lifecycle-create", {
      method: "POST",
      body: {
        originalUrl: "https://EXAMPLE.com:443/feed.xml?lang=zh&edition=full",
        displayName: "Example",
        siteUrl: "https://example.com/",
        categoryId: category.category.id,
        viewKind: "ARTICLE",
        refreshIntervalMinutes: 60,
        sortOrder: 100,
        isEnabled: true
      }
    });
    const created = await createResponse.json<FeedMutation>();
    expect(createResponse.status).toBe(201);
    expect(created).toMatchObject({
      catalogVersion: 2,
      feed: {
        originalUrl: "https://EXAMPLE.com:443/feed.xml?lang=zh&edition=full",
        normalizedUrl: "https://example.com/feed.xml?lang=zh&edition=full",
        categoryId: category.category.id,
        viewKind: "ARTICLE",
        refreshIntervalMinutes: 60,
        version: 2
      }
    });

    const patchResponse = await mutationRequest(
      `/v1/admin/feeds/${created.feed.id}`,
      admin,
      2,
      "feed-lifecycle-patch",
      {
        method: "PATCH",
        body: { displayName: "Example Updated", categoryId: null, sortOrder: 200, isEnabled: false }
      }
    );
    const patched = await patchResponse.json<FeedMutation>();
    expect(patchResponse.status).toBe(200);
    expect(patched).toMatchObject({
      catalogVersion: 3,
      feed: {
        id: created.feed.id,
        displayName: "Example Updated",
        categoryId: null,
        sortOrder: 200,
        isEnabled: false,
        version: 3
      }
    });

    const deleteResponse = await mutationRequest(
      `/v1/admin/feeds/${created.feed.id}`,
      admin,
      3,
      "feed-lifecycle-delete",
      { method: "DELETE" }
    );
    expect(deleteResponse.status).toBe(200);
    await expect(deleteResponse.json()).resolves.toEqual({
      catalogVersion: 4,
      deletedId: created.feed.id,
      resourceType: "FEED"
    });
    const stored = await env.DB.prepare(
      "SELECT normalized_url,display_name,category_id,is_enabled,version,deleted_at FROM managed_feeds WHERE id=?"
    ).bind(created.feed.id).first<Record<string, unknown>>();
    expect(stored).toMatchObject({
      normalized_url: "https://example.com/feed.xml?lang=zh&edition=full",
      display_name: "Example Updated",
      category_id: null,
      is_enabled: 0,
      version: 4,
      deleted_at: expect.any(String)
    });
    const audits = await env.DB.prepare(
      "SELECT action,target_id,request_id,catalog_version FROM audit_events WHERE target_type='feed' ORDER BY catalog_version"
    ).all<Record<string, unknown>>();
    expect(audits.results).toEqual([
      { action: "feed.created", target_id: created.feed.id, request_id: expect.any(String), catalog_version: 2 },
      { action: "feed.updated", target_id: created.feed.id, request_id: expect.any(String), catalog_version: 3 },
      { action: "feed.deleted", target_id: created.feed.id, request_id: expect.any(String), catalog_version: 4 }
    ]);
  });

  it("rejects duplicate names, unsafe Feed input and deleting a non-empty category without advancing the version", async () => {
    const admin = await seedSession("admin");
    const category = await createCategory(admin, 0, "conflict-category-create", {
      name: "Ｔｅｃｈ",
      sortOrder: 10,
      isEnabled: true
    });
    const duplicateCategory = await mutationRequest(
      "/v1/admin/feed-categories",
      admin,
      1,
      "conflict-category-duplicate",
      { method: "POST", body: { name: "tech", sortOrder: 20, isEnabled: true } }
    );
    expect(duplicateCategory.status).toBe(409);
    await expect(errorBody(duplicateCategory)).resolves.toMatchObject({ code: "DUPLICATE_CATEGORY" });

    const controlCharacterName = await mutationRequest(
      "/v1/admin/feed-categories",
      admin,
      1,
      "conflict-category-control",
      { method: "POST", body: { name: "Bad\nName", sortOrder: 20, isEnabled: true } }
    );
    expect(controlCharacterName.status).toBe(400);
    await expect(errorBody(controlCharacterName)).resolves.toMatchObject({ code: "VALIDATION_ERROR" });

    const unsafeFeed = await mutationRequest("/v1/admin/feeds", admin, 1, "conflict-feed-unsafe", {
      method: "POST",
      body: {
        originalUrl: "https://user:secret@example.com/feed.xml#private",
        displayName: "Unsafe",
        categoryId: category.category.id,
        viewKind: "ARTICLE",
        refreshIntervalMinutes: 60,
        sortOrder: 0,
        isEnabled: true
      }
    });
    expect(unsafeFeed.status).toBe(400);
    await expect(errorBody(unsafeFeed)).resolves.toMatchObject({ code: "VALIDATION_ERROR" });

    const feedResponse = await mutationRequest("/v1/admin/feeds", admin, 1, "conflict-feed-create", {
      method: "POST",
      body: {
        originalUrl: "https://example.com/feed.xml",
        displayName: "Example",
        categoryId: category.category.id,
        viewKind: "ARTICLE",
        refreshIntervalMinutes: 60,
        sortOrder: 0,
        isEnabled: true
      }
    });
    expect(feedResponse.status).toBe(201);

    const duplicateFeed = await mutationRequest("/v1/admin/feeds", admin, 2, "conflict-feed-duplicate", {
      method: "POST",
      body: {
        originalUrl: "https://EXAMPLE.com:443/feed.xml",
        displayName: "Duplicate",
        categoryId: null,
        viewKind: "ARTICLE",
        refreshIntervalMinutes: 60,
        sortOrder: 1,
        isEnabled: true
      }
    });
    expect(duplicateFeed.status).toBe(409);
    await expect(errorBody(duplicateFeed)).resolves.toMatchObject({ code: "DUPLICATE_FEED" });

    const deleteCategory = await mutationRequest(
      `/v1/admin/feed-categories/${category.category.id}`,
      admin,
      2,
      "conflict-category-nonempty",
      { method: "DELETE" }
    );
    expect(deleteCategory.status).toBe(409);
    await expect(errorBody(deleteCategory)).resolves.toMatchObject({ code: "CATEGORY_NOT_EMPTY" });
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1")).toBe(2);

    const disabledCategory = await createCategory(admin, 2, "conflict-disabled-category", {
      name: "Disabled",
      sortOrder: 20,
      isEnabled: false
    });
    const enabledUnderDisabled = await mutationRequest("/v1/admin/feeds", admin, 3, "conflict-disabled-feed", {
      method: "POST",
      body: {
        originalUrl: "https://example.com/disabled.xml",
        displayName: "Disabled Feed",
        categoryId: disabledCategory.category.id,
        viewKind: "ARTICLE",
        refreshIntervalMinutes: 60,
        sortOrder: 0,
        isEnabled: true
      }
    });
    expect(enabledUnderDisabled.status).toBe(400);
    await expect(errorBody(enabledUnderDisabled)).resolves.toMatchObject({ code: "VALIDATION_ERROR" });
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1")).toBe(3);
  });

  it("allows only one concurrent writer for the same expected catalog version", async () => {
    const admin = await seedSession("admin");
    const responses = await Promise.all([
      mutationRequest("/v1/admin/feed-categories", admin, 0, "concurrent-category-one", {
        method: "POST",
        body: { name: "One", sortOrder: 1, isEnabled: true }
      }),
      mutationRequest("/v1/admin/feed-categories", admin, 0, "concurrent-category-two", {
        method: "POST",
        body: { name: "Two", sortOrder: 2, isEnabled: true }
      })
    ]);

    expect(responses.map(response => response.status).sort()).toEqual([201, 409]);
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1")).toBe(1);
    expect(await scalar("SELECT COUNT(*) AS value FROM feed_categories")).toBe(1);
    expect(await scalar("SELECT COUNT(*) AS value FROM audit_events WHERE action='feed_category.created'")).toBe(1);
  });

  it("atomically creates referenced categories and feeds with one version and replays the batch", async () => {
    const admin = await seedSession("admin");
    const body = {
      operations: [
        {
          operationId: "category-tech",
          type: "CREATE_CATEGORY",
          input: { name: "技术", sortOrder: 100, isEnabled: true }
        },
        {
          operationId: "feed-tech",
          type: "CREATE_FEED",
          input: {
            originalUrl: "https://example.com/feed.xml",
            displayName: "Example",
            categoryRef: { operationId: "category-tech" },
            viewKind: "ARTICLE",
            refreshIntervalMinutes: 60,
            sortOrder: 100,
            isEnabled: true
          }
        }
      ]
    };

    const response = await mutationRequest(
      "/v1/admin/feed-catalog-batches",
      admin,
      0,
      "catalog-batch-create-0001",
      { method: "POST", requestId: "request-catalog-batch", body }
    );
    const result = await response.json<CatalogBatchMutation>();

    expect(response.status).toBe(200);
    expect(result.catalogVersion).toBe(1);
    expect(result.results).toEqual([
      { operationId: "category-tech", resourceType: "FEED_CATEGORY", resourceId: expect.any(String) },
      { operationId: "feed-tech", resourceType: "FEED", resourceId: expect.any(String) }
    ]);
    expect(await scalar("SELECT COUNT(*) AS value FROM feed_categories WHERE version=1")).toBe(1);
    expect(await scalar("SELECT COUNT(*) AS value FROM managed_feeds WHERE version=1")).toBe(1);
    const storedFeed = await env.DB.prepare(
      "SELECT category_id FROM managed_feeds WHERE id=?"
    ).bind(result.results[1]!.resourceId).first<{ category_id: string }>();
    expect(storedFeed?.category_id).toBe(result.results[0]!.resourceId);
    expect(await scalar("SELECT COUNT(*) AS value FROM audit_events WHERE request_id='request-catalog-batch'")).toBe(3);

    const replay = await mutationRequest(
      "/v1/admin/feed-catalog-batches",
      admin,
      0,
      "catalog-batch-create-0001",
      { method: "POST", body }
    );
    expect(replay.status).toBe(200);
    await expect(replay.json<CatalogBatchMutation>()).resolves.toEqual(result);
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1")).toBe(1);
  });

  it("rolls back every batch operation and reports the failing item", async () => {
    const admin = await seedSession("admin");
    const response = await mutationRequest(
      "/v1/admin/feed-catalog-batches",
      admin,
      0,
      "catalog-batch-failure-0001",
      {
        method: "POST",
        body: {
          operations: [
            {
              operationId: "category-one",
              type: "CREATE_CATEGORY",
              input: { name: "Ｔｅｃｈ", sortOrder: 100, isEnabled: true }
            },
            {
              operationId: "category-two",
              type: "CREATE_CATEGORY",
              input: { name: "tech", sortOrder: 200, isEnabled: true }
            }
          ]
        }
      }
    );

    expect(response.status).toBe(409);
    await expect(errorBody(response)).resolves.toMatchObject({
      code: "BATCH_OPERATION_FAILED",
      details: {
        operationIndex: 1,
        operationId: "category-two",
        innerCode: "DUPLICATE_CATEGORY"
      }
    });
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1")).toBe(0);
    expect(await scalar("SELECT COUNT(*) AS value FROM feed_categories")).toBe(0);
    expect(await scalar("SELECT COUNT(*) AS value FROM audit_events")).toBe(0);
    expect(await scalar("SELECT COUNT(*) AS value FROM catalog_idempotency")).toBe(0);
  });

  it("applies patch and delete operations in order while advancing the catalog once", async () => {
    const admin = await seedSession("admin");
    const category = await createCategory(admin, 0, "batch-lifecycle-category", {
      name: "技术",
      sortOrder: 100,
      isEnabled: true
    });
    const createdFeedResponse = await mutationRequest(
      "/v1/admin/feeds",
      admin,
      1,
      "batch-lifecycle-feed",
      {
        method: "POST",
        body: {
          originalUrl: "https://example.com/lifecycle.xml",
          displayName: "Lifecycle",
          categoryId: category.category.id,
          viewKind: "ARTICLE",
          refreshIntervalMinutes: 60,
          sortOrder: 100,
          isEnabled: true
        }
      }
    );
    const feed = await createdFeedResponse.json<FeedMutation>();

    const response = await mutationRequest(
      "/v1/admin/feed-catalog-batches",
      admin,
      2,
      "catalog-batch-lifecycle-0001",
      {
        method: "POST",
        body: {
          operations: [
            {
              operationId: "feed-patch",
              type: "PATCH_FEED",
              feedId: feed.feed.id,
              input: { displayName: "Updated", isEnabled: false }
            },
            {
              operationId: "category-patch",
              type: "PATCH_CATEGORY",
              categoryId: category.category.id,
              input: { name: "工程", isEnabled: false }
            },
            { operationId: "feed-delete", type: "DELETE_FEED", feedId: feed.feed.id },
            { operationId: "category-delete", type: "DELETE_CATEGORY", categoryId: category.category.id }
          ]
        }
      }
    );
    const result = await response.json<CatalogBatchMutation>();

    expect(response.status).toBe(200);
    expect(result.catalogVersion).toBe(3);
    expect(result.results.map(item => item.operationId)).toEqual([
      "feed-patch",
      "category-patch",
      "feed-delete",
      "category-delete"
    ]);
    expect(await scalar("SELECT COUNT(*) AS value FROM managed_feeds WHERE deleted_at IS NOT NULL AND version=3")).toBe(1);
    expect(await scalar("SELECT COUNT(*) AS value FROM feed_categories WHERE deleted_at IS NOT NULL AND version=3")).toBe(1);
    expect(await scalar("SELECT COUNT(*) AS value FROM audit_events WHERE catalog_version=3")).toBe(5);
  });

  it("rejects non-admin batches and operation counts above the contract limit", async () => {
    const user = await seedSession("user");
    const forbidden = await mutationRequest(
      "/v1/admin/feed-catalog-batches",
      user,
      0,
      "catalog-batch-user-0001",
      {
        method: "POST",
        body: {
          operations: [
            {
              operationId: "category-one",
              type: "CREATE_CATEGORY",
              input: { name: "技术", sortOrder: 100, isEnabled: true }
            }
          ]
        }
      }
    );
    expect(forbidden.status).toBe(403);

    const admin = await seedSession("admin");
    const tooMany = await mutationRequest(
      "/v1/admin/feed-catalog-batches",
      admin,
      0,
      "catalog-batch-limit-0001",
      {
        method: "POST",
        body: {
          operations: Array.from({ length: 101 }, (_, index) => ({
            operationId: `category-${index}`,
            type: "CREATE_CATEGORY",
            input: { name: `Category ${index}`, sortOrder: index, isEnabled: true }
          }))
        }
      }
    );
    expect(tooMany.status).toBe(400);
    await expect(errorBody(tooMany)).resolves.toMatchObject({ code: "VALIDATION_ERROR" });
    expect(await scalar("SELECT catalog_version AS value FROM feed_catalog_state WHERE singleton_id=1")).toBe(0);

    const maximum = await mutationRequest(
      "/v1/admin/feed-catalog-batches",
      admin,
      0,
      "catalog-batch-maximum-0001",
      {
        method: "POST",
        body: {
          operations: Array.from({ length: 100 }, (_, index) => ({
            operationId: `category-${index}`,
            type: "CREATE_CATEGORY",
            input: { name: `Category ${index}`, sortOrder: index, isEnabled: true }
          }))
        }
      }
    );
    const maximumResult = await maximum.json<CatalogBatchMutation>();
    expect(maximum.status).toBe(200);
    expect(maximumResult.results).toHaveLength(100);
    expect(await scalar("SELECT COUNT(*) AS value FROM feed_categories WHERE version=1")).toBe(100);
    expect(await scalar("SELECT COUNT(*) AS value FROM audit_events WHERE catalog_version=1")).toBe(101);
  });
});

async function createCategory(
  session: Session,
  version: number,
  key: string,
  body: { name: string; sortOrder: number; isEnabled: boolean }
): Promise<CategoryMutation> {
  const response = await mutationRequest("/v1/admin/feed-categories", session, version, key, {
    method: "POST",
    body
  });
  expect(response.status).toBe(201);
  return response.json<CategoryMutation>();
}

async function mutationRequest(
  path: string,
  session: Session,
  version: number,
  key: string,
  options: {
    method: "POST" | "PATCH" | "DELETE";
    body?: Record<string, unknown>;
    requestId?: string;
  }
): Promise<Response> {
  const headers = new Headers(catalogHeaders(version, key));
  headers.set("authorization", `Bearer ${session.accessToken}`);
  if (options.requestId) headers.set("x-request-id", options.requestId);
  return workerRequest(path, {
    method: options.method,
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body)
  });
}

function catalogHeaders(version: number, key: string): HeadersInit {
  return {
    "content-type": "application/json",
    "if-match": `"catalog-all-${version}"`,
    "idempotency-key": key
  };
}

async function seedSession(role: "user" | "admin"): Promise<Session> {
  const userId = crypto.randomUUID();
  const now = new Date().toISOString();
  await env.DB.prepare(
    "INSERT INTO users(id,username,username_norm,password_salt,password_hash,role,ai_daily_limit,speech_daily_seconds,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?)"
  ).bind(userId, `${role}-${userId}`, `${role}-${userId}`, "unused-salt", "unused-hash", role, 0, 0, now, now).run();
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

async function errorBody(response: Response): Promise<Record<string, unknown>> {
  const body = await response.clone().json<{ error: Record<string, unknown> }>();
  return body.error;
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
