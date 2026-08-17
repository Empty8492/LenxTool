export interface CatalogAuthContext {
  userId: string;
  role: "user" | "admin";
  requestId: string;
}

export class CatalogApiError extends Error {
  constructor(
    public status: number,
    public code: string,
    public userMessage: string,
    public details?: Record<string, unknown>,
    public isRetryable = false,
    public retryAfterSeconds?: number
  ) {
    super(userMessage);
  }
}

export interface CategoryRow {
  id: string;
  name: string;
  name_norm: string;
  sort_order: number;
  is_enabled: number;
  ai_manual_summary_policy: AiPolicySwitch;
  ai_auto_summary_policy: AiPolicySwitch;
  ai_auto_translation_policy: AiPolicySwitch;
  ai_translation_target_language: AiTranslationTargetLanguage | null;
  ai_daily_entry_limit: number | null;
  ai_max_concurrency: number | null;
  version: number;
  created_at: string;
  updated_at: string;
}

export interface FeedRow {
  id: string;
  original_url: string;
  normalized_url: string;
  display_name: string;
  site_url: string | null;
  category_id: string | null;
  view_kind: ViewKind;
  view_kind_explicit: number;
  full_text_policy: FullTextPolicy;
  refresh_interval_minutes: number;
  sort_order: number;
  is_enabled: number;
  ai_manual_summary_policy: AiPolicySwitch;
  ai_auto_summary_policy: AiPolicySwitch;
  ai_auto_translation_policy: AiPolicySwitch;
  ai_translation_target_language: AiTranslationTargetLanguage | null;
  ai_daily_entry_limit: number | null;
  ai_max_concurrency: number | null;
  version: number;
  created_at: string;
  updated_at: string;
}

interface CatalogStateRow {
  catalog_version: number;
  updated_at: string;
}

export interface IdempotencyRow {
  request_hash: string;
  status_code: number;
  response_body: string;
}

export interface PreparedMutation {
  actorUserId: string;
  method: "POST" | "PATCH" | "DELETE";
  path: string;
  key: string;
  requestHash: string;
  expectedVersion: number;
  newVersion: number;
  requestId: string;
}

interface CommitMutationSpec {
  mutation: PreparedMutation;
  request: Request;
  status: 200 | 201;
  responseBody: string;
  targetType: "feed_category" | "feed";
  targetId: string;
  action: string;
  successTable: "feed_categories" | "managed_feeds";
  duplicateCode?: "DUPLICATE_CATEGORY" | "DUPLICATE_FEED";
  businessStatement: (mutationId: string) => D1PreparedStatement;
}

export type ViewKind = "ARTICLE" | "PICTURE" | "AUDIO" | "VIDEO" | "NOTIFICATION";
export type FullTextPolicy = "NONE" | "ON_OPEN" | "BACKGROUND";
export type AiPolicySwitch = "INHERIT" | "ENABLED" | "DISABLED";
export type AiTranslationTargetLanguage = "zh-Hans" | "en" | "ja" | "ko";

export interface AiPolicyFields {
  manualSummary: AiPolicySwitch;
  autoSummary: AiPolicySwitch;
  autoTranslation: AiPolicySwitch;
  translationTargetLanguage: AiTranslationTargetLanguage | null;
  dailyEntryLimit: number | null;
  maxConcurrency: number | null;
}

const idempotencyKeyPattern = /^[A-Za-z0-9._:-]{16,128}$/u;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu;
const viewKinds = new Set<ViewKind>(["ARTICLE", "PICTURE", "AUDIO", "VIDEO", "NOTIFICATION"]);
const fullTextPolicies = new Set<FullTextPolicy>(["NONE", "ON_OPEN", "BACKGROUND"]);
const aiPolicySwitches = new Set<AiPolicySwitch>(["INHERIT", "ENABLED", "DISABLED"]);
const aiTranslationTargetLanguages = new Set<AiTranslationTargetLanguage>(["zh-Hans", "en", "ja", "ko"]);
const encoder = new TextEncoder();
const catalogResponseLimit = 10 * 1024 * 1024;
const inheritedAiPolicy: AiPolicyFields = Object.freeze({
  manualSummary: "INHERIT",
  autoSummary: "INHERIT",
  autoTranslation: "INHERIT",
  translationTargetLanguage: null,
  dailyEntryLimit: null,
  maxConcurrency: null
});
const aiPolicyDefaults = Object.freeze({
  manualSummary: "ENABLED",
  autoSummary: "DISABLED",
  autoTranslation: "DISABLED",
  translationTargetLanguage: "zh-Hans",
  dailyEntryLimit: 20,
  maxConcurrency: 1
});

export async function handleCatalogReadRequest(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  url: URL
): Promise<Response | null> {
  if (request.method !== "GET" || url.pathname !== "/v1/feeds/catalog") return null;
  if (encoder.encode(request.url).byteLength > 2048) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "目录查询地址过长");
  }

  const conditions = parseCatalogReadConditions(request, url);
  if (conditions.scope === "ALL" && auth.role !== "admin") {
    throw new CatalogApiError(403, "ADMIN_REQUIRED", "需要管理员权限");
  }

  const activeOnly = conditions.scope === "ACTIVE";
  const categorySql =
    "SELECT id,name,name_norm,sort_order,is_enabled,ai_manual_summary_policy,ai_auto_summary_policy," +
    "ai_auto_translation_policy,ai_translation_target_language,ai_daily_entry_limit,ai_max_concurrency," +
    "version,created_at,updated_at " +
    "FROM feed_categories WHERE deleted_at IS NULL" +
    (activeOnly ? " AND is_enabled=1" : "") +
    " ORDER BY sort_order,name COLLATE BINARY,id";
  const feedSql =
    "SELECT f.id,f.original_url,f.normalized_url,f.display_name,f.site_url,f.category_id,f.view_kind,f.view_kind_explicit,f.full_text_policy," +
    "f.refresh_interval_minutes,f.sort_order,f.is_enabled,f.ai_manual_summary_policy,f.ai_auto_summary_policy," +
    "f.ai_auto_translation_policy,f.ai_translation_target_language,f.ai_daily_entry_limit,f.ai_max_concurrency," +
    "f.version,f.created_at,f.updated_at " +
    "FROM managed_feeds f LEFT JOIN feed_categories c ON c.id=f.category_id AND c.deleted_at IS NULL " +
    "WHERE f.deleted_at IS NULL AND (f.category_id IS NULL OR c.id IS NOT NULL)" +
    (activeOnly ? " AND f.is_enabled=1 AND (f.category_id IS NULL OR c.is_enabled=1)" : "") +
    " ORDER BY CASE WHEN f.category_id IS NULL THEN 1 ELSE 0 END," +
    "c.sort_order,c.name COLLATE BINARY,c.id,f.sort_order,f.display_name COLLATE BINARY,f.id";
  const results = await db.batch<CatalogStateRow | CategoryRow | FeedRow>([
    db.prepare("SELECT catalog_version,updated_at FROM feed_catalog_state WHERE singleton_id=1"),
    db.prepare(categorySql),
    db.prepare(feedSql)
  ]);
  const state = results[0]?.results[0] as CatalogStateRow | undefined;
  if (!state || !Number.isSafeInteger(state.catalog_version) || state.catalog_version < 0) {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "共享目录状态不可用");
  }

  const etag = catalogEtag(conditions.scope, state.catalog_version);
  const clientVersion = conditions.afterVersion && conditions.afterVersion > 0
    ? conditions.afterVersion
    : conditions.etagVersion;
  if (clientVersion !== undefined && clientVersion > state.catalog_version) {
    throw new CatalogApiError(
      409,
      "CATALOG_VERSION_AHEAD",
      "客户端目录版本高于服务端版本",
      { currentCatalogVersion: state.catalog_version },
      true
    );
  }
  if (clientVersion !== undefined && clientVersion === state.catalog_version) {
    return catalogNotModified(etag, auth.requestId);
  }

  const categories = (results[1]?.results ?? []) as CategoryRow[];
  const feeds = (results[2]?.results ?? []) as FeedRow[];
  const body = JSON.stringify({
    catalogVersion: state.catalog_version,
    scope: conditions.scope,
    generatedAt: state.updated_at,
    aiPolicyDefaults,
    categories: categories.map(toCatalogCategory),
    feeds: feeds.map(toCatalogFeed)
  });
  if (encoder.encode(body).byteLength > catalogResponseLimit) {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "共享目录快照超过发布上限");
  }
  return catalogJson(body, etag, auth.requestId);
}

export async function handleCatalogAdminRequest(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  url: URL
): Promise<Response | null> {
  const categoryMatch = /^\/v1\/admin\/feed-categories\/([^/]+)$/u.exec(url.pathname);
  const feedMatch = /^\/v1\/admin\/feeds\/([^/]+)$/u.exec(url.pathname);
  const isCategoryCollection = url.pathname === "/v1/admin/feed-categories";
  const isFeedCollection = url.pathname === "/v1/admin/feeds";
  const isCatalogRoute = isCategoryCollection || isFeedCollection || categoryMatch !== null || feedMatch !== null;
  if (!isCatalogRoute) return null;

  if (auth.role !== "admin") {
    throw new CatalogApiError(403, "ADMIN_REQUIRED", "需要管理员权限");
  }
  if (url.search !== "") {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "目录写入不接受查询参数");
  }

  if (isCategoryCollection && request.method === "POST") {
    return createCategory(request, db, auth, url.pathname);
  }
  if (categoryMatch && request.method === "PATCH") {
    const categoryId = requireUuid(categoryMatch[1], "分类 ID");
    return patchCategory(request, db, auth, `/v1/admin/feed-categories/${categoryId}`, categoryId);
  }
  if (categoryMatch && request.method === "DELETE") {
    const categoryId = requireUuid(categoryMatch[1], "分类 ID");
    return deleteCategory(request, db, auth, `/v1/admin/feed-categories/${categoryId}`, categoryId);
  }
  if (isFeedCollection && request.method === "POST") {
    return createFeed(request, db, auth, url.pathname);
  }
  if (feedMatch && request.method === "PATCH") {
    const feedId = requireUuid(feedMatch[1], "Feed ID");
    return patchFeed(request, db, auth, `/v1/admin/feeds/${feedId}`, feedId);
  }
  if (feedMatch && request.method === "DELETE") {
    const feedId = requireUuid(feedMatch[1], "Feed ID");
    return deleteFeed(request, db, auth, `/v1/admin/feeds/${feedId}`, feedId);
  }
  return null;
}

function parseCatalogReadConditions(request: Request, url: URL): {
  scope: "ACTIVE" | "ALL";
  afterVersion?: number;
  etagVersion?: number;
} {
  for (const key of url.searchParams.keys()) {
    if (key !== "afterVersion" && key !== "scope") {
      throw new CatalogApiError(400, "VALIDATION_ERROR", "目录查询包含未知参数");
    }
  }
  if (url.searchParams.getAll("afterVersion").length > 1 || url.searchParams.getAll("scope").length > 1) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "目录查询参数不能重复");
  }

  const scopeValue = url.searchParams.get("scope") ?? "ACTIVE";
  if (scopeValue !== "ACTIVE" && scopeValue !== "ALL") {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "目录范围无效");
  }
  const afterValue = url.searchParams.get("afterVersion");
  const afterVersion = afterValue === null ? undefined : parseCatalogVersion(afterValue, "afterVersion");

  const ifNoneMatch = request.headers.get("if-none-match");
  let etagVersion: number | undefined;
  if (ifNoneMatch !== null) {
    const match = /^"catalog-(active|all)-(0|[1-9][0-9]*)"$/u.exec(ifNoneMatch);
    if (!match || match[1]?.toUpperCase() !== scopeValue) {
      throw new CatalogApiError(400, "VALIDATION_ERROR", "If-None-Match 与目录范围不一致");
    }
    etagVersion = parseCatalogVersion(match[2]!, "If-None-Match");
    if (afterVersion === 0 || (afterVersion !== undefined && afterVersion !== etagVersion)) {
      throw new CatalogApiError(400, "VALIDATION_ERROR", "目录缓存条件互相矛盾");
    }
  }
  return { scope: scopeValue, afterVersion, etagVersion };
}

function parseCatalogVersion(value: string, label: string): number {
  if (!/^(0|[1-9][0-9]*)$/u.test(value)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label} 格式无效`);
  }
  const version = Number(value);
  if (!Number.isSafeInteger(version) || version < 0) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label} 超出范围`);
  }
  return version;
}

function catalogEtag(scope: "ACTIVE" | "ALL", version: number): string {
  return `"catalog-${scope.toLocaleLowerCase("en-US")}-${version}"`;
}

function toCatalogCategory(row: CategoryRow) {
  return {
    id: row.id,
    name: row.name,
    sortOrder: row.sort_order,
    isEnabled: row.is_enabled === 1,
    aiPolicy: aiPolicyFromRow(row),
    version: row.version,
    createdAt: row.created_at,
    updatedAt: row.updated_at
  };
}

function toCatalogFeed(row: FeedRow) {
  return {
    id: row.id,
    originalUrl: row.original_url,
    normalizedUrl: row.normalized_url,
    displayName: row.display_name,
    siteUrl: row.site_url,
    categoryId: row.category_id,
    viewKind: row.view_kind,
    isViewKindExplicit: row.view_kind_explicit === 1,
    fullTextPolicy: row.full_text_policy,
    refreshIntervalMinutes: row.refresh_interval_minutes,
    sortOrder: row.sort_order,
    isEnabled: row.is_enabled === 1,
    aiPolicy: aiPolicyFromRow(row),
    version: row.version,
    createdAt: row.created_at,
    updatedAt: row.updated_at
  };
}

async function createCategory(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string
): Promise<Response> {
  const body = await readJson(request, 8192);
  const prepared = await prepareMutation(request, db, auth, path, body);
  if (prepared instanceof Response) return prepared;
  assertOnlyFields(body, ["name", "sortOrder", "isEnabled", "aiPolicy"]);
  const name = requireTrimmedString(body.name, "分类名称", 80);
  const nameNorm = normalizeCategoryName(name);
  const sortOrder = requireInteger(body.sortOrder ?? 0, 0, 1_000_000, "分类排序");
  const isEnabled = requireBoolean(body.isEnabled ?? true, "分类启用状态");
  const aiPolicy = body.aiPolicy === undefined
    ? inheritedAiPolicy
    : requireAiPolicyFields(body.aiPolicy, inheritedAiPolicy);

  if (await categoryNameExists(db, nameNorm)) {
    throw new CatalogApiError(409, "DUPLICATE_CATEGORY", "已存在同名分类");
  }
  if (await countRows(db, "feed_categories") >= 200) {
    throw new CatalogApiError(409, "CATALOG_CAPACITY_EXCEEDED", "共享分类数量已达到上限");
  }

  const id = crypto.randomUUID();
  const now = nowIso();
  const category = {
    id,
    name,
    sortOrder,
    isEnabled,
    aiPolicy,
    version: prepared.newVersion,
    createdAt: now,
    updatedAt: now
  };
  const responseBody = JSON.stringify({ catalogVersion: prepared.newVersion, category });
  return commitMutation(db, {
    mutation: prepared,
    request,
    status: 201,
    responseBody,
    targetType: "feed_category",
    targetId: id,
    action: "feed_category.created",
    successTable: "feed_categories",
    duplicateCode: "DUPLICATE_CATEGORY",
    businessStatement: mutationId => db.prepare(
      "INSERT INTO feed_categories(id,name,name_norm,sort_order,is_enabled,ai_manual_summary_policy," +
      "ai_auto_summary_policy,ai_auto_translation_policy,ai_translation_target_language,ai_daily_entry_limit," +
      "ai_max_concurrency,version,created_at,updated_at) SELECT ?,?,?,?,?,?,?,?,?,?,?,?,?,? WHERE EXISTS " +
      "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
    ).bind(
      id,
      name,
      nameNorm,
      sortOrder,
      isEnabled ? 1 : 0,
      ...aiPolicyDatabaseValues(aiPolicy),
      prepared.newVersion,
      now,
      now,
      mutationId
    )
  });
}

async function patchCategory(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string,
  categoryId: string
): Promise<Response> {
  const body = await readJson(request, 8192);
  const prepared = await prepareMutation(request, db, auth, path, body);
  if (prepared instanceof Response) return prepared;
  assertOnlyFields(body, ["name", "sortOrder", "isEnabled", "aiPolicy"]);
  assertHasFields(body, "分类更新至少需要一个字段");
  const current = await getCategory(db, categoryId);
  const name = body.name === undefined ? current.name : requireTrimmedString(body.name, "分类名称", 80);
  const nameNorm = normalizeCategoryName(name);
  const sortOrder = body.sortOrder === undefined
    ? current.sort_order
    : requireInteger(body.sortOrder, 0, 1_000_000, "分类排序");
  const isEnabled = body.isEnabled === undefined
    ? current.is_enabled === 1
    : requireBoolean(body.isEnabled, "分类启用状态");
  const aiPolicy = body.aiPolicy === undefined
    ? aiPolicyFromRow(current)
    : requireAiPolicyFields(body.aiPolicy, aiPolicyFromRow(current));

  if (await categoryNameExists(db, nameNorm, categoryId)) {
    throw new CatalogApiError(409, "DUPLICATE_CATEGORY", "已存在同名分类");
  }

  const now = nowIso();
  const category = {
    id: categoryId,
    name,
    sortOrder,
    isEnabled,
    aiPolicy,
    version: prepared.newVersion,
    createdAt: current.created_at,
    updatedAt: now
  };
  const responseBody = JSON.stringify({ catalogVersion: prepared.newVersion, category });
  return commitMutation(db, {
    mutation: prepared,
    request,
    status: 200,
    responseBody,
    targetType: "feed_category",
    targetId: categoryId,
    action: "feed_category.updated",
    successTable: "feed_categories",
    duplicateCode: "DUPLICATE_CATEGORY",
    businessStatement: mutationId => db.prepare(
      "UPDATE feed_categories SET name=?,name_norm=?,sort_order=?,is_enabled=?,ai_manual_summary_policy=?," +
      "ai_auto_summary_policy=?,ai_auto_translation_policy=?,ai_translation_target_language=?," +
      "ai_daily_entry_limit=?,ai_max_concurrency=?,version=?,updated_at=? " +
      "WHERE id=? AND deleted_at IS NULL AND EXISTS " +
      "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
    ).bind(
      name,
      nameNorm,
      sortOrder,
      isEnabled ? 1 : 0,
      ...aiPolicyDatabaseValues(aiPolicy),
      prepared.newVersion,
      now,
      categoryId,
      mutationId
    )
  });
}

async function deleteCategory(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string,
  categoryId: string
): Promise<Response> {
  await requireEmptyBody(request);
  const prepared = await prepareMutation(request, db, auth, path, null);
  if (prepared instanceof Response) return prepared;
  await getCategory(db, categoryId);
  const feedCount = await db.prepare(
    "SELECT COUNT(*) AS count FROM managed_feeds WHERE category_id=? AND deleted_at IS NULL"
  ).bind(categoryId).first<{ count: number }>();
  if ((feedCount?.count ?? 0) !== 0) {
    throw new CatalogApiError(409, "CATEGORY_NOT_EMPTY", "分类中仍有 Feed，不能删除");
  }

  const now = nowIso();
  const responseBody = JSON.stringify({
    catalogVersion: prepared.newVersion,
    deletedId: categoryId,
    resourceType: "FEED_CATEGORY"
  });
  return commitMutation(db, {
    mutation: prepared,
    request,
    status: 200,
    responseBody,
    targetType: "feed_category",
    targetId: categoryId,
    action: "feed_category.deleted",
    successTable: "feed_categories",
    businessStatement: mutationId => db.prepare(
      "UPDATE feed_categories SET is_enabled=0,deleted_at=?,version=?,updated_at=? " +
      "WHERE id=? AND deleted_at IS NULL AND EXISTS " +
      "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
    ).bind(now, prepared.newVersion, now, categoryId, mutationId)
  });
}

async function createFeed(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string
): Promise<Response> {
  const body = await readJson(request, 16_384);
  const prepared = await prepareMutation(request, db, auth, path, body);
  if (prepared instanceof Response) return prepared;
  assertOnlyFields(body, [
    "originalUrl", "displayName", "siteUrl", "categoryId", "viewKind", "isViewKindExplicit", "fullTextPolicy",
    "refreshIntervalMinutes", "sortOrder", "isEnabled", "aiPolicy"
  ]);
  const originalUrl = requireOriginalFeedUrl(body.originalUrl);
  const normalizedUrl = normalizeHttpsUrl(originalUrl, "Feed URL");
  const displayName = requireTrimmedString(body.displayName, "Feed 名称", 160);
  const siteUrl = body.siteUrl === undefined || body.siteUrl === null
    ? null
    : normalizeHttpsUrl(requireTrimmedString(body.siteUrl, "站点 URL", 2048), "站点 URL");
  const categoryId = body.categoryId === undefined || body.categoryId === null
    ? null
    : requireUuid(body.categoryId, "分类 ID");
  const viewKind = body.viewKind === undefined
    ? "ARTICLE"
    : requireViewKind(body.viewKind);
  const isViewKindExplicit = body.isViewKindExplicit === undefined
    ? body.viewKind !== undefined
    : requireBoolean(body.isViewKindExplicit, "Feed 视图覆盖状态");
  const fullTextPolicy = requireFullTextPolicy(body.fullTextPolicy ?? "NONE");
  const refreshIntervalMinutes = requireInteger(body.refreshIntervalMinutes ?? 60, 5, 1440, "刷新间隔");
  const sortOrder = requireInteger(body.sortOrder ?? 0, 0, 1_000_000, "Feed 排序");
  const isEnabled = requireBoolean(body.isEnabled ?? true, "Feed 启用状态");
  const aiPolicy = body.aiPolicy === undefined
    ? inheritedAiPolicy
    : requireAiPolicyFields(body.aiPolicy, inheritedAiPolicy);

  const category = categoryId === null ? null : await getCategory(db, categoryId);
  if (isEnabled && category?.is_enabled === 0) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "不能在停用分类下启用 Feed");
  }
  if (await feedUrlExists(db, normalizedUrl)) {
    throw new CatalogApiError(409, "DUPLICATE_FEED", "该 Feed 已存在");
  }
  if (await countRows(db, "managed_feeds") >= 5000) {
    throw new CatalogApiError(409, "CATALOG_CAPACITY_EXCEEDED", "共享 Feed 数量已达到上限");
  }

  const id = crypto.randomUUID();
  const now = nowIso();
  const feed = {
    id,
    originalUrl,
    normalizedUrl,
    displayName,
    siteUrl,
    categoryId,
    viewKind,
    isViewKindExplicit,
    fullTextPolicy,
    refreshIntervalMinutes,
    sortOrder,
    isEnabled,
    aiPolicy,
    version: prepared.newVersion,
    createdAt: now,
    updatedAt: now
  };
  const responseBody = JSON.stringify({ catalogVersion: prepared.newVersion, feed });
  return commitMutation(db, {
    mutation: prepared,
    request,
    status: 201,
    responseBody,
    targetType: "feed",
    targetId: id,
    action: "feed.created",
    successTable: "managed_feeds",
    duplicateCode: "DUPLICATE_FEED",
    businessStatement: mutationId => db.prepare(
      "INSERT INTO managed_feeds(id,original_url,normalized_url,display_name,site_url,category_id,view_kind,view_kind_explicit,full_text_policy," +
      "refresh_interval_minutes,sort_order,is_enabled,ai_manual_summary_policy,ai_auto_summary_policy," +
      "ai_auto_translation_policy,ai_translation_target_language,ai_daily_entry_limit,ai_max_concurrency," +
      "version,created_at,updated_at) SELECT ?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,? WHERE EXISTS " +
      "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
    ).bind(
      id, originalUrl, normalizedUrl, displayName, siteUrl, categoryId, viewKind, isViewKindExplicit ? 1 : 0, fullTextPolicy,
      refreshIntervalMinutes, sortOrder, isEnabled ? 1 : 0, ...aiPolicyDatabaseValues(aiPolicy),
      prepared.newVersion, now, now, mutationId
    )
  });
}

async function patchFeed(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string,
  feedId: string
): Promise<Response> {
  const body = await readJson(request, 16_384);
  const prepared = await prepareMutation(request, db, auth, path, body);
  if (prepared instanceof Response) return prepared;
  assertOnlyFields(body, [
    "originalUrl", "displayName", "siteUrl", "categoryId", "viewKind", "isViewKindExplicit", "fullTextPolicy",
    "refreshIntervalMinutes", "sortOrder", "isEnabled", "aiPolicy"
  ]);
  assertHasFields(body, "Feed 更新至少需要一个字段");
  const current = await getFeed(db, feedId);
  const originalUrl = body.originalUrl === undefined
    ? current.original_url
    : requireOriginalFeedUrl(body.originalUrl);
  const normalizedUrl = body.originalUrl === undefined
    ? current.normalized_url
    : normalizeHttpsUrl(originalUrl, "Feed URL");
  const displayName = body.displayName === undefined
    ? current.display_name
    : requireTrimmedString(body.displayName, "Feed 名称", 160);
  const siteUrl = body.siteUrl === undefined
    ? current.site_url
    : body.siteUrl === null
      ? null
      : normalizeHttpsUrl(requireTrimmedString(body.siteUrl, "站点 URL", 2048), "站点 URL");
  const categoryId = body.categoryId === undefined
    ? current.category_id
    : body.categoryId === null
      ? null
      : requireUuid(body.categoryId, "分类 ID");
  const viewKind = body.viewKind === undefined ? current.view_kind : requireViewKind(body.viewKind);
  const isViewKindExplicit = body.isViewKindExplicit !== undefined
    ? requireBoolean(body.isViewKindExplicit, "Feed 视图覆盖状态")
    : body.viewKind !== undefined
      ? true
      : current.view_kind_explicit === 1;
  const fullTextPolicy = body.fullTextPolicy === undefined
    ? current.full_text_policy
    : requireFullTextPolicy(body.fullTextPolicy);
  const refreshIntervalMinutes = body.refreshIntervalMinutes === undefined
    ? current.refresh_interval_minutes
    : requireInteger(body.refreshIntervalMinutes, 5, 1440, "刷新间隔");
  const sortOrder = body.sortOrder === undefined
    ? current.sort_order
    : requireInteger(body.sortOrder, 0, 1_000_000, "Feed 排序");
  const isEnabled = body.isEnabled === undefined
    ? current.is_enabled === 1
    : requireBoolean(body.isEnabled, "Feed 启用状态");
  const aiPolicy = body.aiPolicy === undefined
    ? aiPolicyFromRow(current)
    : requireAiPolicyFields(body.aiPolicy, aiPolicyFromRow(current));

  const category = categoryId === null ? null : await getCategory(db, categoryId);
  if (isEnabled && category?.is_enabled === 0 && (body.isEnabled === true || body.categoryId !== undefined)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "不能在停用分类下启用 Feed");
  }
  if (await feedUrlExists(db, normalizedUrl, feedId)) {
    throw new CatalogApiError(409, "DUPLICATE_FEED", "该 Feed 已存在");
  }

  const now = nowIso();
  const feed = {
    id: feedId,
    originalUrl,
    normalizedUrl,
    displayName,
    siteUrl,
    categoryId,
    viewKind,
    isViewKindExplicit,
    fullTextPolicy,
    refreshIntervalMinutes,
    sortOrder,
    isEnabled,
    aiPolicy,
    version: prepared.newVersion,
    createdAt: current.created_at,
    updatedAt: now
  };
  const responseBody = JSON.stringify({ catalogVersion: prepared.newVersion, feed });
  return commitMutation(db, {
    mutation: prepared,
    request,
    status: 200,
    responseBody,
    targetType: "feed",
    targetId: feedId,
    action: "feed.updated",
    successTable: "managed_feeds",
    duplicateCode: "DUPLICATE_FEED",
    businessStatement: mutationId => db.prepare(
      "UPDATE managed_feeds SET original_url=?,normalized_url=?,display_name=?,site_url=?,category_id=?,view_kind=?,view_kind_explicit=?,full_text_policy=?," +
      "refresh_interval_minutes=?,sort_order=?,is_enabled=?,ai_manual_summary_policy=?,ai_auto_summary_policy=?," +
      "ai_auto_translation_policy=?,ai_translation_target_language=?,ai_daily_entry_limit=?,ai_max_concurrency=?," +
      "version=?,updated_at=? " +
      "WHERE id=? AND deleted_at IS NULL AND EXISTS " +
      "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
    ).bind(
      originalUrl, normalizedUrl, displayName, siteUrl, categoryId, viewKind, isViewKindExplicit ? 1 : 0, fullTextPolicy,
      refreshIntervalMinutes, sortOrder, isEnabled ? 1 : 0, ...aiPolicyDatabaseValues(aiPolicy),
      prepared.newVersion, now, feedId, mutationId
    )
  });
}

async function deleteFeed(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string,
  feedId: string
): Promise<Response> {
  await requireEmptyBody(request);
  const prepared = await prepareMutation(request, db, auth, path, null);
  if (prepared instanceof Response) return prepared;
  await getFeed(db, feedId);
  const now = nowIso();
  const responseBody = JSON.stringify({
    catalogVersion: prepared.newVersion,
    deletedId: feedId,
    resourceType: "FEED"
  });
  return commitMutation(db, {
    mutation: prepared,
    request,
    status: 200,
    responseBody,
    targetType: "feed",
    targetId: feedId,
    action: "feed.deleted",
    successTable: "managed_feeds",
    businessStatement: mutationId => db.prepare(
      "UPDATE managed_feeds SET is_enabled=0,deleted_at=?,version=?,updated_at=? " +
      "WHERE id=? AND deleted_at IS NULL AND EXISTS " +
      "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
    ).bind(now, prepared.newVersion, now, feedId, mutationId)
  });
}

export async function prepareMutation(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string,
  body: unknown
): Promise<PreparedMutation | Response> {
  const method = request.method;
  if (method !== "POST" && method !== "PATCH" && method !== "DELETE") {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "目录写入方法无效");
  }
  const key = request.headers.get("idempotency-key") ?? "";
  if (!idempotencyKeyPattern.test(key)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "Idempotency-Key 格式无效");
  }
  const ifMatch = request.headers.get("if-match") ?? "";
  const match = /^"catalog-all-(0|[1-9][0-9]*)"$/u.exec(ifMatch);
  if (!match) throw new CatalogApiError(400, "VALIDATION_ERROR", "If-Match 格式无效");
  const expectedVersion = Number(match[1]);
  if (!Number.isSafeInteger(expectedVersion)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "目录版本超出支持范围");
  }
  const requestHash = await sha256(`${method}\n${path}\n${ifMatch}\n${canonicalJson(body)}`);
  const now = nowIso();
  await db.prepare(
    "DELETE FROM catalog_idempotency WHERE actor_user_id=? AND http_method=? AND normalized_path=? " +
    "AND idempotency_key=? AND expires_at<=?"
  ).bind(auth.userId, method, path, key, now).run();
  const stored = await findIdempotency(db, auth.userId, method, path, key, now);
  if (stored) return replayOrReject(stored, requestHash, auth.requestId);

  const currentVersion = await getCatalogVersion(db);
  if (currentVersion !== expectedVersion) {
    throw versionConflict(currentVersion);
  }
  if (currentVersion >= Number.MAX_SAFE_INTEGER) {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "共享目录版本已达到服务上限");
  }
  return {
    actorUserId: auth.userId,
    method,
    path,
    key,
    requestHash,
    expectedVersion,
    newVersion: currentVersion + 1,
    requestId: auth.requestId
  };
}

async function commitMutation(db: D1Database, spec: CommitMutationSpec): Promise<Response> {
  const mutationId = crypto.randomUUID();
  const now = nowIso();
  const expiresAt = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
  const ip = spec.request.headers.get("cf-connecting-ip");
  const ipHash = ip ? await sha256(ip) : null;
  const successCondition = `EXISTS (SELECT 1 FROM ${spec.successTable} WHERE id=? AND version=?)`;
  const auditStatement = db.prepare(
    "INSERT INTO audit_events(id,actor_user_id,target_type,target_id,action,request_id,ip_hash,created_at,catalog_version) " +
    `SELECT ?,?,?,?,?,?,?,?,? WHERE EXISTS (SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?) AND ${successCondition}`
  ).bind(
    crypto.randomUUID(), spec.mutation.actorUserId, spec.targetType, spec.targetId, spec.action,
    spec.mutation.requestId, ipHash, now, spec.mutation.newVersion, mutationId,
    spec.targetId, spec.mutation.newVersion
  );
  const idempotencyStatement = db.prepare(
    "INSERT INTO catalog_idempotency(actor_user_id,http_method,normalized_path,idempotency_key,request_hash," +
    "status_code,response_body,created_at,expires_at) " +
    `SELECT ?,?,?,?,?,?,?,?,? WHERE EXISTS (SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?) AND ${successCondition}`
  ).bind(
    spec.mutation.actorUserId, spec.mutation.method, spec.mutation.path, spec.mutation.key,
    spec.mutation.requestHash, spec.status, spec.responseBody, now, expiresAt, mutationId,
    spec.targetId, spec.mutation.newVersion
  );
  const guardStatement = db.prepare(
    "INSERT INTO catalog_mutation_guards(mutation_id,valid) VALUES(?,CASE WHEN EXISTS (" +
    "SELECT 1 FROM catalog_idempotency WHERE actor_user_id=? AND http_method=? AND normalized_path=? " +
    "AND idempotency_key=? AND request_hash=?) THEN 1 ELSE 0 END)"
  ).bind(
    mutationId, spec.mutation.actorUserId, spec.mutation.method, spec.mutation.path,
    spec.mutation.key, spec.mutation.requestHash
  );

  try {
    await db.batch([
      db.prepare(
        "UPDATE feed_catalog_state SET catalog_version=?,updated_at=?,last_mutation_id=? " +
        "WHERE singleton_id=1 AND catalog_version=?"
      ).bind(spec.mutation.newVersion, now, mutationId, spec.mutation.expectedVersion),
      spec.businessStatement(mutationId),
      auditStatement,
      idempotencyStatement,
      guardStatement,
      db.prepare("DELETE FROM catalog_mutation_guards WHERE mutation_id=?").bind(mutationId)
    ]);
  } catch (error) {
    const stored = await findIdempotency(
      db,
      spec.mutation.actorUserId,
      spec.mutation.method,
      spec.mutation.path,
      spec.mutation.key,
      nowIso()
    );
    if (stored) return replayOrReject(stored, spec.mutation.requestHash, spec.mutation.requestId);
    const currentVersion = await getCatalogVersion(db);
    if (currentVersion !== spec.mutation.expectedVersion) throw versionConflict(currentVersion);
    if (spec.duplicateCode && isUniqueConstraintError(error)) {
      throw new CatalogApiError(
        409,
        spec.duplicateCode,
        spec.duplicateCode === "DUPLICATE_CATEGORY" ? "已存在同名分类" : "该 Feed 已存在"
      );
    }
    throw new CatalogApiError(500, "INTERNAL_ERROR", "目录更新失败，请稍后重试");
  }

  return jsonText(spec.responseBody, spec.status, spec.mutation.requestId);
}

export async function findIdempotency(
  db: D1Database,
  actorUserId: string,
  method: string,
  path: string,
  key: string,
  now: string
): Promise<IdempotencyRow | null> {
  return db.prepare(
    "SELECT request_hash,status_code,response_body FROM catalog_idempotency " +
    "WHERE actor_user_id=? AND http_method=? AND normalized_path=? AND idempotency_key=? AND expires_at>?"
  ).bind(actorUserId, method, path, key, now).first<IdempotencyRow>();
}

export function replayOrReject(stored: IdempotencyRow, requestHash: string, requestId: string): Response {
  if (!timingSafeEqual(stored.request_hash, requestHash)) {
    throw new CatalogApiError(409, "IDEMPOTENCY_KEY_REUSED", "Idempotency-Key 已用于不同请求");
  }
  return jsonText(stored.response_body, stored.status_code, requestId);
}

export async function getCatalogVersion(db: D1Database): Promise<number> {
  const state = await db.prepare(
    "SELECT catalog_version FROM feed_catalog_state WHERE singleton_id=1"
  ).first<{ catalog_version: number }>();
  if (!state || !Number.isSafeInteger(state.catalog_version) || state.catalog_version < 0) {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "共享目录状态不可用");
  }
  return state.catalog_version;
}

async function getCategory(db: D1Database, categoryId: string): Promise<CategoryRow> {
  const row = await db.prepare(
    "SELECT id,name,name_norm,sort_order,is_enabled,ai_manual_summary_policy,ai_auto_summary_policy," +
    "ai_auto_translation_policy,ai_translation_target_language,ai_daily_entry_limit,ai_max_concurrency," +
    "version,created_at,updated_at " +
    "FROM feed_categories WHERE id=? AND deleted_at IS NULL"
  ).bind(categoryId).first<CategoryRow>();
  if (!row) throw new CatalogApiError(404, "RESOURCE_NOT_FOUND", "分类不存在");
  return row;
}

async function getFeed(db: D1Database, feedId: string): Promise<FeedRow> {
  const row = await db.prepare(
    "SELECT id,original_url,normalized_url,display_name,site_url,category_id,view_kind,view_kind_explicit,full_text_policy," +
    "refresh_interval_minutes,sort_order,is_enabled,ai_manual_summary_policy,ai_auto_summary_policy," +
    "ai_auto_translation_policy,ai_translation_target_language,ai_daily_entry_limit,ai_max_concurrency," +
    "version,created_at,updated_at " +
    "FROM managed_feeds WHERE id=? AND deleted_at IS NULL"
  ).bind(feedId).first<FeedRow>();
  if (!row) throw new CatalogApiError(404, "RESOURCE_NOT_FOUND", "Feed 不存在");
  return row;
}

async function categoryNameExists(db: D1Database, nameNorm: string, excludedId?: string): Promise<boolean> {
  const row = excludedId
    ? await db.prepare(
      "SELECT 1 AS found FROM feed_categories WHERE name_norm=? AND deleted_at IS NULL AND id<>? LIMIT 1"
    ).bind(nameNorm, excludedId).first<{ found: number }>()
    : await db.prepare(
      "SELECT 1 AS found FROM feed_categories WHERE name_norm=? AND deleted_at IS NULL LIMIT 1"
    ).bind(nameNorm).first<{ found: number }>();
  return row !== null;
}

async function feedUrlExists(db: D1Database, normalizedUrl: string, excludedId?: string): Promise<boolean> {
  const row = excludedId
    ? await db.prepare(
      "SELECT 1 AS found FROM managed_feeds WHERE normalized_url=? AND deleted_at IS NULL AND id<>? LIMIT 1"
    ).bind(normalizedUrl, excludedId).first<{ found: number }>()
    : await db.prepare(
      "SELECT 1 AS found FROM managed_feeds WHERE normalized_url=? AND deleted_at IS NULL LIMIT 1"
    ).bind(normalizedUrl).first<{ found: number }>();
  return row !== null;
}

async function countRows(db: D1Database, table: "feed_categories" | "managed_feeds"): Promise<number> {
  const row = await db.prepare(
    `SELECT COUNT(*) AS count FROM ${table} WHERE deleted_at IS NULL`
  ).first<{ count: number }>();
  return row?.count ?? 0;
}

export function versionConflict(currentCatalogVersion: number): CatalogApiError {
  return new CatalogApiError(
    409,
    "CATALOG_VERSION_CONFLICT",
    "其他管理员已经修改了共享目录",
    { currentCatalogVersion },
    true
  );
}

export function requireOriginalFeedUrl(value: unknown): string {
  const original = requireTrimmedString(value, "Feed URL", 2048);
  normalizeHttpsUrl(original, "Feed URL");
  return original;
}

export function normalizeHttpsUrl(value: string, label: string): string {
  if (/[\u0000-\u001f\u007f]/u.test(value) || value.includes("#")) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label}格式无效`);
  }
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label}格式无效`);
  }
  if (url.protocol !== "https:" || url.username !== "" || url.password !== "" || url.port !== "") {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label}必须是安全的 HTTPS 地址`);
  }
  return url.toString();
}

export function normalizeCategoryName(value: string): string {
  return value.normalize("NFKC").toLocaleLowerCase("und");
}

export function requireTrimmedString(value: unknown, label: string, maxCodePoints: number): string {
  if (typeof value !== "string") {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label}格式无效`);
  }
  const trimmed = value.trim();
  const length = Array.from(trimmed).length;
  if (length < 1 || length > maxCodePoints || /[\u0000-\u001f\u007f]/u.test(trimmed)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label}长度无效`);
  }
  return trimmed;
}

export function requireInteger(value: unknown, min: number, max: number, label: string): number {
  if (typeof value !== "number" || !Number.isInteger(value) || value < min || value > max) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label}超出范围`);
  }
  return value;
}

export function requireBoolean(value: unknown, label: string): boolean {
  if (typeof value !== "boolean") {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label}格式无效`);
  }
  return value;
}

export function requireUuid(value: unknown, label: string): string {
  if (typeof value !== "string" || !uuidPattern.test(value)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label}格式无效`);
  }
  return value.toLowerCase();
}

export function requireViewKind(value: unknown): ViewKind {
  if (typeof value !== "string" || !viewKinds.has(value as ViewKind)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "Feed 视图类型无效");
  }
  return value as ViewKind;
}

export function requireFullTextPolicy(value: unknown): FullTextPolicy {
  if (typeof value !== "string" || !fullTextPolicies.has(value as FullTextPolicy)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "Feed 全文抓取策略无效");
  }
  return value as FullTextPolicy;
}

export function requireAiPolicyFields(value: unknown, current: AiPolicyFields): AiPolicyFields {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "AI 策略格式无效");
  }
  const input = value as Record<string, unknown>;
  assertOnlyFields(input, [
    "manualSummary",
    "autoSummary",
    "autoTranslation",
    "translationTargetLanguage",
    "dailyEntryLimit",
    "maxConcurrency"
  ]);
  assertHasFields(input, "AI 策略更新至少需要一个字段");
  return {
    manualSummary: input.manualSummary === undefined
      ? current.manualSummary
      : requireAiPolicySwitch(input.manualSummary, "手动摘要策略"),
    autoSummary: input.autoSummary === undefined
      ? current.autoSummary
      : requireAiPolicySwitch(input.autoSummary, "自动摘要策略"),
    autoTranslation: input.autoTranslation === undefined
      ? current.autoTranslation
      : requireAiPolicySwitch(input.autoTranslation, "自动翻译策略"),
    translationTargetLanguage: input.translationTargetLanguage === undefined
      ? current.translationTargetLanguage
      : input.translationTargetLanguage === null
        ? null
        : requireAiTranslationTargetLanguage(input.translationTargetLanguage),
    dailyEntryLimit: input.dailyEntryLimit === undefined
      ? current.dailyEntryLimit
      : input.dailyEntryLimit === null
        ? null
        : requireInteger(input.dailyEntryLimit, 1, 1000, "AI 每日条目上限"),
    maxConcurrency: input.maxConcurrency === undefined
      ? current.maxConcurrency
      : input.maxConcurrency === null
        ? null
        : requireInteger(input.maxConcurrency, 1, 4, "AI 并发上限")
  };
}

export function aiPolicyFromRow(row: CategoryRow | FeedRow): AiPolicyFields {
  return {
    manualSummary: row.ai_manual_summary_policy,
    autoSummary: row.ai_auto_summary_policy,
    autoTranslation: row.ai_auto_translation_policy,
    translationTargetLanguage: row.ai_translation_target_language,
    dailyEntryLimit: row.ai_daily_entry_limit,
    maxConcurrency: row.ai_max_concurrency
  };
}

export function aiPolicyDatabaseValues(
  policy: AiPolicyFields
): [
  AiPolicySwitch,
  AiPolicySwitch,
  AiPolicySwitch,
  AiTranslationTargetLanguage | null,
  number | null,
  number | null
] {
  return [
    policy.manualSummary,
    policy.autoSummary,
    policy.autoTranslation,
    policy.translationTargetLanguage,
    policy.dailyEntryLimit,
    policy.maxConcurrency
  ];
}

function requireAiPolicySwitch(value: unknown, label: string): AiPolicySwitch {
  if (typeof value !== "string" || !aiPolicySwitches.has(value as AiPolicySwitch)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label}无效`);
  }
  return value as AiPolicySwitch;
}

function requireAiTranslationTargetLanguage(value: unknown): AiTranslationTargetLanguage {
  if (typeof value !== "string" ||
      !aiTranslationTargetLanguages.has(value as AiTranslationTargetLanguage)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "AI 翻译目标语言无效");
  }
  return value as AiTranslationTargetLanguage;
}

export function assertOnlyFields(body: Record<string, unknown>, allowed: readonly string[]): void {
  const fields = new Set(allowed);
  if (Object.keys(body).some(key => !fields.has(key))) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "请求包含未知字段");
  }
}

export function assertHasFields(body: Record<string, unknown>, message: string): void {
  if (Object.keys(body).length === 0) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", message);
  }
}

async function requireEmptyBody(request: Request): Promise<void> {
  const bytes = await readBodyWithinLimit(request, 8192);
  if (bytes.byteLength !== 0) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "该请求不接受正文");
  }
}

export async function readJson(request: Request, max: number): Promise<Record<string, unknown>> {
  const bytes = await readBodyWithinLimit(request, max);
  let value: unknown;
  try {
    value = JSON.parse(new TextDecoder().decode(bytes));
  } catch {
    throw new CatalogApiError(400, "INVALID_JSON", "请求 JSON 无效");
  }
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "请求必须是 JSON 对象");
  }
  return value as Record<string, unknown>;
}

async function readBodyWithinLimit(request: Request, max: number): Promise<Uint8Array> {
  const lengthHeader = request.headers.get("content-length");
  if (lengthHeader !== null) {
    const length = Number(lengthHeader);
    if (Number.isFinite(length) && length > max) {
      throw new CatalogApiError(413, "PAYLOAD_TOO_LARGE", "请求内容过大");
    }
  }
  if (!request.body) return new Uint8Array();
  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      const result = await reader.read();
      if (result.done) break;
      total += result.value.byteLength;
      if (total > max) {
        await reader.cancel();
        throw new CatalogApiError(413, "PAYLOAD_TOO_LARGE", "请求内容过大");
      }
      chunks.push(result.value);
    }
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return bytes;
}

function canonicalJson(value: unknown): string {
  if (value === null || typeof value !== "object") return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  const object = value as Record<string, unknown>;
  return `{${Object.keys(object).sort().map(key => `${JSON.stringify(key)}:${canonicalJson(object[key])}`).join(",")}}`;
}

export async function sha256(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(value));
  return toBase64Url(new Uint8Array(digest));
}

function toBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

function timingSafeEqual(left: string, right: string): boolean {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++) {
    difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  }
  return difference === 0;
}

export function isUniqueConstraintError(error: unknown): boolean {
  return error instanceof Error && /UNIQUE constraint failed/iu.test(error.message);
}

export function jsonText(body: string, status: number, requestId: string): Response {
  const headers = new Headers({
    "content-type": "application/json; charset=utf-8",
    "cache-control": "no-store",
    "x-request-id": requestId
  });
  return new Response(body, { status, headers });
}

function catalogJson(body: string, etag: string, requestId: string): Response {
  const headers = catalogReadHeaders(etag, requestId);
  headers.set("content-type", "application/json; charset=utf-8");
  return new Response(body, { status: 200, headers });
}

function catalogNotModified(etag: string, requestId: string): Response {
  return new Response(null, { status: 304, headers: catalogReadHeaders(etag, requestId) });
}

function catalogReadHeaders(etag: string, requestId: string): Headers {
  return new Headers({
    "cache-control": "private, no-cache, no-transform",
    "vary": "Authorization",
    "etag": etag,
    "x-request-id": requestId
  });
}

export function nowIso(): string {
  return new Date().toISOString();
}
