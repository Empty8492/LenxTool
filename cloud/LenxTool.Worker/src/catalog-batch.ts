import {
  CatalogApiError,
  type AiPolicyFields,
  type CatalogAuthContext,
  type CategoryRow,
  type FeedRow,
  type PreparedMutation,
  aiPolicyFromRow,
  assertHasFields,
  assertOnlyFields,
  findIdempotency,
  getCatalogVersion,
  isUniqueConstraintError,
  jsonText,
  normalizeCategoryName,
  normalizeHttpsUrl,
  nowIso,
  prepareMutation,
  readJson,
  replayOrReject,
  requireAiPolicyFields,
  requireBoolean,
  requireFullTextPolicy,
  requireInteger,
  requireOriginalFeedUrl,
  requireTrimmedString,
  requireUuid,
  requireViewKind,
  sha256,
  versionConflict
} from "./catalog";

const inheritedAiPolicy: AiPolicyFields = {
  manualSummary: "INHERIT",
  autoSummary: "INHERIT",
  autoTranslation: "INHERIT",
  translationTargetLanguage: null,
  dailyEntryLimit: null,
  maxConcurrency: null
};

type BatchOperationType =
  | "CREATE_CATEGORY"
  | "PATCH_CATEGORY"
  | "DELETE_CATEGORY"
  | "CREATE_FEED"
  | "PATCH_FEED"
  | "DELETE_FEED";

interface BatchResult {
  operationId: string;
  resourceType: "FEED_CATEGORY" | "FEED";
  resourceId: string;
}

interface PreparedBatchOperation extends BatchResult {
  type: BatchOperationType;
  action: string;
  targetType: "feed_category" | "feed";
  row: Record<string, unknown>;
}

interface MutableCatalog {
  categories: Map<string, CategoryRow>;
  feeds: Map<string, FeedRow>;
  categoryReferences: Map<string, string>;
}

const batchPath = "/v1/admin/feed-catalog-batches";
const operationIdPattern = /^[A-Za-z0-9._:-]{1,64}$/u;
const operationTypes = new Set<BatchOperationType>([
  "CREATE_CATEGORY",
  "PATCH_CATEGORY",
  "DELETE_CATEGORY",
  "CREATE_FEED",
  "PATCH_FEED",
  "DELETE_FEED"
]);

export async function handleCatalogBatchRequest(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  url: URL
): Promise<Response | null> {
  if (url.pathname !== batchPath) return null;
  if (auth.role !== "admin") {
    throw new CatalogApiError(403, "ADMIN_REQUIRED", "需要管理员权限");
  }
  if (request.method !== "POST" || url.search !== "") {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "批量目录写入请求无效");
  }

  const body = await readJson(request, 256 * 1024);
  const mutation = await prepareMutation(request, db, auth, batchPath, body);
  if (mutation instanceof Response) return mutation;
  assertOnlyFields(body, ["operations"]);
  if (!Array.isArray(body.operations) || body.operations.length < 1 || body.operations.length > 100) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "批量目录操作数量必须为 1～100");
  }

  const operations = await prepareBatchOperations(db, body.operations, mutation.newVersion);
  return commitBatch(request, db, mutation, operations);
}

async function prepareBatchOperations(
  db: D1Database,
  values: unknown[],
  newVersion: number
): Promise<PreparedBatchOperation[]> {
  const catalog = await loadCatalog(db);
  const prepared: PreparedBatchOperation[] = [];
  const operationIds = new Set<string>();

  for (let index = 0; index < values.length; index++) {
    const operation = requireRecord(values[index], "批量操作");
    const operationId = requireOperationId(operation.operationId);
    if (!operationIds.add(operationId)) {
      throw new CatalogApiError(400, "VALIDATION_ERROR", "批量 operationId 不能重复");
    }
    try {
      const type = requireOperationType(operation.type);
      prepared.push(prepareOperation(catalog, operation, operationId, type, newVersion));
    } catch (error) {
      if (error instanceof CatalogApiError) throw batchFailure(index, operationId, error.code);
      throw error;
    }
  }
  return prepared;
}

function prepareOperation(
  catalog: MutableCatalog,
  operation: Record<string, unknown>,
  operationId: string,
  type: BatchOperationType,
  newVersion: number
): PreparedBatchOperation {
  switch (type) {
    case "CREATE_CATEGORY":
      return prepareCreateCategory(catalog, operation, operationId, newVersion);
    case "PATCH_CATEGORY":
      return preparePatchCategory(catalog, operation, operationId, newVersion);
    case "DELETE_CATEGORY":
      return prepareDeleteCategory(catalog, operation, operationId, newVersion);
    case "CREATE_FEED":
      return prepareCreateFeed(catalog, operation, operationId, newVersion);
    case "PATCH_FEED":
      return preparePatchFeed(catalog, operation, operationId, newVersion);
    case "DELETE_FEED":
      return prepareDeleteFeed(catalog, operation, operationId, newVersion);
  }
}

function prepareCreateCategory(
  catalog: MutableCatalog,
  operation: Record<string, unknown>,
  operationId: string,
  newVersion: number
): PreparedBatchOperation {
  assertOnlyFields(operation, ["operationId", "type", "input"]);
  const input = requireRecord(operation.input, "分类输入");
  assertOnlyFields(input, ["name", "sortOrder", "isEnabled", "aiPolicy"]);
  const name = requireTrimmedString(input.name, "分类名称", 80);
  const nameNorm = normalizeCategoryName(name);
  const sortOrder = requireInteger(input.sortOrder ?? 0, 0, 1_000_000, "分类排序");
  const isEnabled = requireBoolean(input.isEnabled ?? true, "分类启用状态");
  const aiPolicy = input.aiPolicy === undefined
    ? inheritedAiPolicy
    : requireAiPolicyFields(input.aiPolicy, inheritedAiPolicy);
  if (hasCategoryName(catalog, nameNorm)) {
    throw new CatalogApiError(409, "DUPLICATE_CATEGORY", "已存在同名分类");
  }
  if (catalog.categories.size >= 200) {
    throw new CatalogApiError(409, "CATALOG_CAPACITY_EXCEEDED", "共享分类数量已达到上限");
  }

  const id = crypto.randomUUID();
  const now = nowIso();
  catalog.categories.set(id, {
    id,
    name,
    name_norm: nameNorm,
    sort_order: sortOrder,
    is_enabled: isEnabled ? 1 : 0,
    ai_manual_summary_policy: aiPolicy.manualSummary,
    ai_auto_summary_policy: aiPolicy.autoSummary,
    ai_auto_translation_policy: aiPolicy.autoTranslation,
    ai_translation_target_language: aiPolicy.translationTargetLanguage,
    ai_daily_entry_limit: aiPolicy.dailyEntryLimit,
    ai_max_concurrency: aiPolicy.maxConcurrency,
    version: newVersion,
    created_at: now,
    updated_at: now
  });
  catalog.categoryReferences.set(operationId, id);
  return operationSpec(
    operationId,
    "FEED_CATEGORY",
    id,
    "CREATE_CATEGORY",
    "feed_category.created",
    "feed_category",
    { id, name, nameNorm, sortOrder, isEnabled, aiPolicy, version: newVersion, createdAt: now, updatedAt: now }
  );
}

function preparePatchCategory(
  catalog: MutableCatalog,
  operation: Record<string, unknown>,
  operationId: string,
  newVersion: number
): PreparedBatchOperation {
  assertOnlyFields(operation, ["operationId", "type", "categoryId", "input"]);
  const categoryId = requireUuid(operation.categoryId, "分类 ID");
  const current = requireCategory(catalog, categoryId);
  const input = requireRecord(operation.input, "分类输入");
  assertOnlyFields(input, ["name", "sortOrder", "isEnabled", "aiPolicy"]);
  assertHasFields(input, "分类更新至少需要一个字段");
  const name = input.name === undefined ? current.name : requireTrimmedString(input.name, "分类名称", 80);
  const nameNorm = normalizeCategoryName(name);
  const sortOrder = input.sortOrder === undefined
    ? current.sort_order
    : requireInteger(input.sortOrder, 0, 1_000_000, "分类排序");
  const isEnabled = input.isEnabled === undefined
    ? current.is_enabled === 1
    : requireBoolean(input.isEnabled, "分类启用状态");
  const aiPolicy = input.aiPolicy === undefined
    ? aiPolicyFromRow(current)
    : requireAiPolicyFields(input.aiPolicy, aiPolicyFromRow(current));
  if (hasCategoryName(catalog, nameNorm, categoryId)) {
    throw new CatalogApiError(409, "DUPLICATE_CATEGORY", "已存在同名分类");
  }
  const now = nowIso();
  catalog.categories.set(categoryId, {
    ...current,
    name,
    name_norm: nameNorm,
    sort_order: sortOrder,
    is_enabled: isEnabled ? 1 : 0,
    ai_manual_summary_policy: aiPolicy.manualSummary,
    ai_auto_summary_policy: aiPolicy.autoSummary,
    ai_auto_translation_policy: aiPolicy.autoTranslation,
    ai_translation_target_language: aiPolicy.translationTargetLanguage,
    ai_daily_entry_limit: aiPolicy.dailyEntryLimit,
    ai_max_concurrency: aiPolicy.maxConcurrency,
    version: newVersion,
    updated_at: now
  });
  return operationSpec(
    operationId,
    "FEED_CATEGORY",
    categoryId,
    "PATCH_CATEGORY",
    "feed_category.updated",
    "feed_category",
    { id: categoryId, name, nameNorm, sortOrder, isEnabled, aiPolicy, version: newVersion, updatedAt: now }
  );
}

function prepareDeleteCategory(
  catalog: MutableCatalog,
  operation: Record<string, unknown>,
  operationId: string,
  newVersion: number
): PreparedBatchOperation {
  assertOnlyFields(operation, ["operationId", "type", "categoryId"]);
  const categoryId = requireUuid(operation.categoryId, "分类 ID");
  requireCategory(catalog, categoryId);
  if ([...catalog.feeds.values()].some(feed => feed.category_id === categoryId)) {
    throw new CatalogApiError(409, "CATEGORY_NOT_EMPTY", "分类中仍有 Feed，不能删除");
  }
  catalog.categories.delete(categoryId);
  const now = nowIso();
  return operationSpec(
    operationId,
    "FEED_CATEGORY",
    categoryId,
    "DELETE_CATEGORY",
    "feed_category.deleted",
    "feed_category",
    { id: categoryId, deletedAt: now, version: newVersion, updatedAt: now }
  );
}

function prepareCreateFeed(
  catalog: MutableCatalog,
  operation: Record<string, unknown>,
  operationId: string,
  newVersion: number
): PreparedBatchOperation {
  assertOnlyFields(operation, ["operationId", "type", "input"]);
  const input = requireRecord(operation.input, "Feed 输入");
  assertOnlyFields(input, [
    "originalUrl", "displayName", "siteUrl", "categoryId", "categoryRef", "viewKind", "fullTextPolicy",
    "refreshIntervalMinutes", "sortOrder", "isEnabled", "aiPolicy"
  ]);
  const originalUrl = requireOriginalFeedUrl(input.originalUrl);
  const normalizedUrl = normalizeHttpsUrl(originalUrl, "Feed URL");
  const displayName = requireTrimmedString(input.displayName, "Feed 名称", 160);
  const siteUrl = input.siteUrl === undefined || input.siteUrl === null
    ? null
    : normalizeHttpsUrl(requireTrimmedString(input.siteUrl, "站点 URL", 2048), "站点 URL");
  const categoryId = resolveCreateCategoryId(catalog, input);
  const viewKind = requireViewKind(input.viewKind ?? "ARTICLE");
  const fullTextPolicy = requireFullTextPolicy(input.fullTextPolicy ?? "NONE");
  const refreshIntervalMinutes = requireInteger(input.refreshIntervalMinutes ?? 60, 5, 1440, "刷新间隔");
  const sortOrder = requireInteger(input.sortOrder ?? 0, 0, 1_000_000, "Feed 排序");
  const isEnabled = requireBoolean(input.isEnabled ?? true, "Feed 启用状态");
  const aiPolicy = input.aiPolicy === undefined
    ? inheritedAiPolicy
    : requireAiPolicyFields(input.aiPolicy, inheritedAiPolicy);
  const category = categoryId === null ? null : requireCategory(catalog, categoryId);
  if (isEnabled && category?.is_enabled === 0) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "不能在停用分类下启用 Feed");
  }
  if (hasFeedUrl(catalog, normalizedUrl)) {
    throw new CatalogApiError(409, "DUPLICATE_FEED", "该 Feed 已存在");
  }
  if (catalog.feeds.size >= 5000) {
    throw new CatalogApiError(409, "CATALOG_CAPACITY_EXCEEDED", "共享 Feed 数量已达到上限");
  }

  const id = crypto.randomUUID();
  const now = nowIso();
  catalog.feeds.set(id, {
    id,
    original_url: originalUrl,
    normalized_url: normalizedUrl,
    display_name: displayName,
    site_url: siteUrl,
    category_id: categoryId,
    view_kind: viewKind,
    full_text_policy: fullTextPolicy,
    refresh_interval_minutes: refreshIntervalMinutes,
    sort_order: sortOrder,
    is_enabled: isEnabled ? 1 : 0,
    ai_manual_summary_policy: aiPolicy.manualSummary,
    ai_auto_summary_policy: aiPolicy.autoSummary,
    ai_auto_translation_policy: aiPolicy.autoTranslation,
    ai_translation_target_language: aiPolicy.translationTargetLanguage,
    ai_daily_entry_limit: aiPolicy.dailyEntryLimit,
    ai_max_concurrency: aiPolicy.maxConcurrency,
    version: newVersion,
    created_at: now,
    updated_at: now
  });
  return operationSpec(
    operationId,
    "FEED",
    id,
    "CREATE_FEED",
    "feed.created",
    "feed",
    {
      id, originalUrl, normalizedUrl, displayName, siteUrl, categoryId, viewKind, fullTextPolicy,
      refreshIntervalMinutes, sortOrder, isEnabled, aiPolicy, version: newVersion, createdAt: now, updatedAt: now
    }
  );
}

function preparePatchFeed(
  catalog: MutableCatalog,
  operation: Record<string, unknown>,
  operationId: string,
  newVersion: number
): PreparedBatchOperation {
  assertOnlyFields(operation, ["operationId", "type", "feedId", "input"]);
  const feedId = requireUuid(operation.feedId, "Feed ID");
  const current = requireFeed(catalog, feedId);
  const input = requireRecord(operation.input, "Feed 输入");
  assertOnlyFields(input, [
    "originalUrl", "displayName", "siteUrl", "categoryId", "viewKind", "fullTextPolicy",
    "refreshIntervalMinutes", "sortOrder", "isEnabled", "aiPolicy"
  ]);
  assertHasFields(input, "Feed 更新至少需要一个字段");
  const originalUrl = input.originalUrl === undefined
    ? current.original_url
    : requireOriginalFeedUrl(input.originalUrl);
  const normalizedUrl = input.originalUrl === undefined
    ? current.normalized_url
    : normalizeHttpsUrl(originalUrl, "Feed URL");
  const displayName = input.displayName === undefined
    ? current.display_name
    : requireTrimmedString(input.displayName, "Feed 名称", 160);
  const siteUrl = input.siteUrl === undefined
    ? current.site_url
    : input.siteUrl === null
      ? null
      : normalizeHttpsUrl(requireTrimmedString(input.siteUrl, "站点 URL", 2048), "站点 URL");
  const categoryId = input.categoryId === undefined
    ? current.category_id
    : input.categoryId === null
      ? null
      : requireUuid(input.categoryId, "分类 ID");
  const viewKind = input.viewKind === undefined ? current.view_kind : requireViewKind(input.viewKind);
  const fullTextPolicy = input.fullTextPolicy === undefined
    ? current.full_text_policy
    : requireFullTextPolicy(input.fullTextPolicy);
  const refreshIntervalMinutes = input.refreshIntervalMinutes === undefined
    ? current.refresh_interval_minutes
    : requireInteger(input.refreshIntervalMinutes, 5, 1440, "刷新间隔");
  const sortOrder = input.sortOrder === undefined
    ? current.sort_order
    : requireInteger(input.sortOrder, 0, 1_000_000, "Feed 排序");
  const isEnabled = input.isEnabled === undefined
    ? current.is_enabled === 1
    : requireBoolean(input.isEnabled, "Feed 启用状态");
  const aiPolicy = input.aiPolicy === undefined
    ? aiPolicyFromRow(current)
    : requireAiPolicyFields(input.aiPolicy, aiPolicyFromRow(current));
  const category = categoryId === null ? null : requireCategory(catalog, categoryId);
  if (isEnabled && category?.is_enabled === 0 && (input.isEnabled === true || input.categoryId !== undefined)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "不能在停用分类下启用 Feed");
  }
  if (hasFeedUrl(catalog, normalizedUrl, feedId)) {
    throw new CatalogApiError(409, "DUPLICATE_FEED", "该 Feed 已存在");
  }
  const now = nowIso();
  catalog.feeds.set(feedId, {
    ...current,
    original_url: originalUrl,
    normalized_url: normalizedUrl,
    display_name: displayName,
    site_url: siteUrl,
    category_id: categoryId,
    view_kind: viewKind,
    full_text_policy: fullTextPolicy,
    refresh_interval_minutes: refreshIntervalMinutes,
    sort_order: sortOrder,
    is_enabled: isEnabled ? 1 : 0,
    ai_manual_summary_policy: aiPolicy.manualSummary,
    ai_auto_summary_policy: aiPolicy.autoSummary,
    ai_auto_translation_policy: aiPolicy.autoTranslation,
    ai_translation_target_language: aiPolicy.translationTargetLanguage,
    ai_daily_entry_limit: aiPolicy.dailyEntryLimit,
    ai_max_concurrency: aiPolicy.maxConcurrency,
    version: newVersion,
    updated_at: now
  });
  return operationSpec(
    operationId,
    "FEED",
    feedId,
    "PATCH_FEED",
    "feed.updated",
    "feed",
    {
      id: feedId, originalUrl, normalizedUrl, displayName, siteUrl, categoryId, viewKind, fullTextPolicy,
      refreshIntervalMinutes, sortOrder, isEnabled, aiPolicy, version: newVersion, updatedAt: now
    }
  );
}

function prepareDeleteFeed(
  catalog: MutableCatalog,
  operation: Record<string, unknown>,
  operationId: string,
  newVersion: number
): PreparedBatchOperation {
  assertOnlyFields(operation, ["operationId", "type", "feedId"]);
  const feedId = requireUuid(operation.feedId, "Feed ID");
  requireFeed(catalog, feedId);
  catalog.feeds.delete(feedId);
  const now = nowIso();
  return operationSpec(
    operationId,
    "FEED",
    feedId,
    "DELETE_FEED",
    "feed.deleted",
    "feed",
    { id: feedId, deletedAt: now, version: newVersion, updatedAt: now }
  );
}

async function commitBatch(
  request: Request,
  db: D1Database,
  mutation: PreparedMutation,
  operations: PreparedBatchOperation[]
): Promise<Response> {
  const mutationId = crypto.randomUUID();
  const now = nowIso();
  const expiresAt = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
  const ip = request.headers.get("cf-connecting-ip");
  const ipHash = ip ? await sha256(ip) : null;
  const responseBody = JSON.stringify({
    catalogVersion: mutation.newVersion,
    results: operations.map(({ operationId, resourceType, resourceId }) => ({
      operationId,
      resourceType,
      resourceId
    }))
  });
  const finalOperations = new Map<string, PreparedBatchOperation>();
  for (const operation of operations) {
    finalOperations.set(`${operation.targetType}:${operation.resourceId}`, operation);
  }
  const finalStates = [...finalOperations.values()];
  const expectedStates = finalStates.map(operation => ({
    resourceType: operation.resourceType,
    resourceId: operation.resourceId,
    version: mutation.newVersion,
    deleted: operation.type === "DELETE_CATEGORY" || operation.type === "DELETE_FEED"
  }));
  const batchSucceeded = "EXISTS (SELECT 1 FROM catalog_mutation_guards WHERE mutation_id=? AND valid=1)";
  const stateGuard = db.prepare(
    "INSERT INTO catalog_mutation_guards(mutation_id,valid) SELECT ?,CASE WHEN NOT EXISTS (" +
    "SELECT 1 FROM json_each(?) expected WHERE NOT (" +
    "(json_extract(expected.value,'$.resourceType')='FEED_CATEGORY' AND EXISTS (" +
    "SELECT 1 FROM feed_categories category_row WHERE category_row.id=json_extract(expected.value,'$.resourceId') " +
    "AND category_row.version=json_extract(expected.value,'$.version') AND ((json_extract(expected.value,'$.deleted')=1 " +
    "AND category_row.deleted_at IS NOT NULL) OR (json_extract(expected.value,'$.deleted')=0 AND category_row.deleted_at IS NULL)))) " +
    "OR (json_extract(expected.value,'$.resourceType')='FEED' AND EXISTS (" +
    "SELECT 1 FROM managed_feeds feed_row WHERE feed_row.id=json_extract(expected.value,'$.resourceId') " +
    "AND feed_row.version=json_extract(expected.value,'$.version') AND ((json_extract(expected.value,'$.deleted')=1 " +
    "AND feed_row.deleted_at IS NOT NULL) OR (json_extract(expected.value,'$.deleted')=0 AND feed_row.deleted_at IS NULL))))" +
    ")) THEN 1 ELSE 0 END"
  ).bind(mutationId, JSON.stringify(expectedStates));
  const auditRows = [
    {
      id: crypto.randomUUID(),
      actorUserId: mutation.actorUserId,
      targetType: "feed_catalog",
      targetId: null,
      action: "feed_catalog.batch",
      requestId: mutation.requestId,
      ipHash,
      createdAt: now,
      catalogVersion: mutation.newVersion
    },
    ...operations.map(operation => ({
      id: crypto.randomUUID(),
      actorUserId: mutation.actorUserId,
      targetType: operation.targetType,
      targetId: operation.resourceId,
      action: operation.action,
      requestId: mutation.requestId,
      ipHash,
      createdAt: now,
      catalogVersion: mutation.newVersion
    }))
  ];
  const auditStatement = db.prepare(
    "INSERT INTO audit_events(id,actor_user_id,target_type,target_id,action,request_id,ip_hash,created_at,catalog_version) " +
    "SELECT json_extract(value,'$.id'),json_extract(value,'$.actorUserId'),json_extract(value,'$.targetType')," +
    "json_extract(value,'$.targetId'),json_extract(value,'$.action'),json_extract(value,'$.requestId')," +
    "json_extract(value,'$.ipHash'),json_extract(value,'$.createdAt'),json_extract(value,'$.catalogVersion') " +
    `FROM json_each(?) WHERE ${batchSucceeded}`
  ).bind(JSON.stringify(auditRows), mutationId);
  const idempotency = db.prepare(
    "INSERT INTO catalog_idempotency(actor_user_id,http_method,normalized_path,idempotency_key,request_hash," +
    "status_code,response_body,created_at,expires_at) " +
    `SELECT ?,?,?,?,?,200,?,?,? WHERE ${batchSucceeded}`
  ).bind(
    mutation.actorUserId, mutation.method, mutation.path, mutation.key, mutation.requestHash,
    responseBody, now, expiresAt, mutationId
  );
  const idempotencyGuard = db.prepare(
    "UPDATE catalog_mutation_guards SET valid=CASE WHEN EXISTS (" +
    "SELECT 1 FROM catalog_idempotency WHERE actor_user_id=? AND http_method=? AND normalized_path=? " +
    "AND idempotency_key=? AND request_hash=?) THEN 1 ELSE 0 END WHERE mutation_id=?"
  ).bind(mutation.actorUserId, mutation.method, mutation.path, mutation.key, mutation.requestHash, mutationId);

  try {
    await db.batch([
      db.prepare(
        "UPDATE feed_catalog_state SET catalog_version=?,updated_at=?,last_mutation_id=? " +
        "WHERE singleton_id=1 AND catalog_version=?"
      ).bind(mutation.newVersion, now, mutationId, mutation.expectedVersion),
      ...buildBusinessStatements(db, operations, mutationId),
      stateGuard,
      auditStatement,
      idempotency,
      idempotencyGuard,
      db.prepare("DELETE FROM catalog_mutation_guards WHERE mutation_id=?").bind(mutationId)
    ]);
  } catch (error) {
    const stored = await findIdempotency(
      db,
      mutation.actorUserId,
      mutation.method,
      mutation.path,
      mutation.key,
      nowIso()
    );
    if (stored) return replayOrReject(stored, mutation.requestHash, mutation.requestId);
    const currentVersion = await getCatalogVersion(db);
    if (currentVersion !== mutation.expectedVersion) throw versionConflict(currentVersion);
    if (isUniqueConstraintError(error)) {
      throw new CatalogApiError(409, "BATCH_OPERATION_FAILED", "批量目录更新存在重复资源");
    }
    throw new CatalogApiError(500, "INTERNAL_ERROR", "批量目录更新失败，请稍后重试");
  }
  return jsonText(responseBody, 200, mutation.requestId);
}

function buildBusinessStatements(
  db: D1Database,
  operations: PreparedBatchOperation[],
  mutationId: string
): D1PreparedStatement[] {
  const statements: D1PreparedStatement[] = [];
  appendJsonStatement(statements, operations, "DELETE_FEED", rows => db.prepare(
    "WITH changes AS (" +
    "SELECT json_extract(value,'$.id') AS id,json_extract(value,'$.deletedAt') AS deleted_at," +
    "json_extract(value,'$.version') AS version,json_extract(value,'$.updatedAt') AS updated_at FROM json_each(?)" +
    ") UPDATE managed_feeds SET is_enabled=0," +
    "deleted_at=(SELECT deleted_at FROM changes WHERE changes.id=managed_feeds.id)," +
    "version=(SELECT version FROM changes WHERE changes.id=managed_feeds.id)," +
    "updated_at=(SELECT updated_at FROM changes WHERE changes.id=managed_feeds.id) " +
    "WHERE id IN (SELECT id FROM changes) AND deleted_at IS NULL AND EXISTS " +
    "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
  ).bind(rows, mutationId));
  appendJsonStatement(statements, operations, "DELETE_CATEGORY", rows => db.prepare(
    "WITH changes AS (" +
    "SELECT json_extract(value,'$.id') AS id,json_extract(value,'$.deletedAt') AS deleted_at," +
    "json_extract(value,'$.version') AS version,json_extract(value,'$.updatedAt') AS updated_at FROM json_each(?)" +
    ") UPDATE feed_categories SET is_enabled=0," +
    "deleted_at=(SELECT deleted_at FROM changes WHERE changes.id=feed_categories.id)," +
    "version=(SELECT version FROM changes WHERE changes.id=feed_categories.id)," +
    "updated_at=(SELECT updated_at FROM changes WHERE changes.id=feed_categories.id) " +
    "WHERE id IN (SELECT id FROM changes) AND deleted_at IS NULL AND EXISTS " +
    "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
  ).bind(rows, mutationId));
  appendJsonStatement(statements, operations, "PATCH_CATEGORY", rows => db.prepare(
    "WITH changes AS (" +
    "SELECT json_extract(value,'$.id') AS id,json_extract(value,'$.name') AS name," +
    "json_extract(value,'$.nameNorm') AS name_norm,json_extract(value,'$.sortOrder') AS sort_order," +
    "json_extract(value,'$.isEnabled') AS is_enabled,json_extract(value,'$.version') AS version," +
    "json_extract(value,'$.aiPolicy.manualSummary') AS ai_manual_summary_policy," +
    "json_extract(value,'$.aiPolicy.autoSummary') AS ai_auto_summary_policy," +
    "json_extract(value,'$.aiPolicy.autoTranslation') AS ai_auto_translation_policy," +
    "json_extract(value,'$.aiPolicy.translationTargetLanguage') AS ai_translation_target_language," +
    "json_extract(value,'$.aiPolicy.dailyEntryLimit') AS ai_daily_entry_limit," +
    "json_extract(value,'$.aiPolicy.maxConcurrency') AS ai_max_concurrency," +
    "json_extract(value,'$.updatedAt') AS updated_at FROM json_each(?)" +
    ") UPDATE feed_categories SET " +
    "name=(SELECT name FROM changes WHERE changes.id=feed_categories.id)," +
    "name_norm=(SELECT name_norm FROM changes WHERE changes.id=feed_categories.id)," +
    "sort_order=(SELECT sort_order FROM changes WHERE changes.id=feed_categories.id)," +
    "is_enabled=(SELECT is_enabled FROM changes WHERE changes.id=feed_categories.id)," +
    "ai_manual_summary_policy=(SELECT ai_manual_summary_policy FROM changes WHERE changes.id=feed_categories.id)," +
    "ai_auto_summary_policy=(SELECT ai_auto_summary_policy FROM changes WHERE changes.id=feed_categories.id)," +
    "ai_auto_translation_policy=(SELECT ai_auto_translation_policy FROM changes WHERE changes.id=feed_categories.id)," +
    "ai_translation_target_language=(SELECT ai_translation_target_language FROM changes WHERE changes.id=feed_categories.id)," +
    "ai_daily_entry_limit=(SELECT ai_daily_entry_limit FROM changes WHERE changes.id=feed_categories.id)," +
    "ai_max_concurrency=(SELECT ai_max_concurrency FROM changes WHERE changes.id=feed_categories.id)," +
    "version=(SELECT version FROM changes WHERE changes.id=feed_categories.id)," +
    "updated_at=(SELECT updated_at FROM changes WHERE changes.id=feed_categories.id) " +
    "WHERE id IN (SELECT id FROM changes) AND deleted_at IS NULL AND EXISTS " +
    "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
  ).bind(rows, mutationId));
  appendJsonStatement(statements, operations, "CREATE_CATEGORY", rows => db.prepare(
    "INSERT INTO feed_categories(id,name,name_norm,sort_order,is_enabled,ai_manual_summary_policy," +
    "ai_auto_summary_policy,ai_auto_translation_policy,ai_translation_target_language,ai_daily_entry_limit," +
    "ai_max_concurrency,version,created_at,updated_at) " +
    "SELECT json_extract(value,'$.id'),json_extract(value,'$.name'),json_extract(value,'$.nameNorm')," +
    "json_extract(value,'$.sortOrder'),json_extract(value,'$.isEnabled')," +
    "json_extract(value,'$.aiPolicy.manualSummary'),json_extract(value,'$.aiPolicy.autoSummary')," +
    "json_extract(value,'$.aiPolicy.autoTranslation'),json_extract(value,'$.aiPolicy.translationTargetLanguage')," +
    "json_extract(value,'$.aiPolicy.dailyEntryLimit'),json_extract(value,'$.aiPolicy.maxConcurrency')," +
    "json_extract(value,'$.version')," +
    "json_extract(value,'$.createdAt'),json_extract(value,'$.updatedAt') FROM json_each(?) WHERE EXISTS " +
    "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
  ).bind(rows, mutationId));
  appendJsonStatement(statements, operations, "PATCH_FEED", rows => db.prepare(
    "WITH changes AS (" +
    "SELECT json_extract(value,'$.id') AS id,json_extract(value,'$.originalUrl') AS original_url," +
    "json_extract(value,'$.normalizedUrl') AS normalized_url,json_extract(value,'$.displayName') AS display_name," +
    "json_extract(value,'$.siteUrl') AS site_url,json_extract(value,'$.categoryId') AS category_id," +
    "json_extract(value,'$.viewKind') AS view_kind,json_extract(value,'$.fullTextPolicy') AS full_text_policy," +
    "json_extract(value,'$.refreshIntervalMinutes') AS refresh_interval_minutes," +
    "json_extract(value,'$.sortOrder') AS sort_order,json_extract(value,'$.isEnabled') AS is_enabled," +
    "json_extract(value,'$.aiPolicy.manualSummary') AS ai_manual_summary_policy," +
    "json_extract(value,'$.aiPolicy.autoSummary') AS ai_auto_summary_policy," +
    "json_extract(value,'$.aiPolicy.autoTranslation') AS ai_auto_translation_policy," +
    "json_extract(value,'$.aiPolicy.translationTargetLanguage') AS ai_translation_target_language," +
    "json_extract(value,'$.aiPolicy.dailyEntryLimit') AS ai_daily_entry_limit," +
    "json_extract(value,'$.aiPolicy.maxConcurrency') AS ai_max_concurrency," +
    "json_extract(value,'$.version') AS version,json_extract(value,'$.updatedAt') AS updated_at FROM json_each(?)" +
    ") UPDATE managed_feeds SET " +
    "original_url=(SELECT original_url FROM changes WHERE changes.id=managed_feeds.id)," +
    "normalized_url=(SELECT normalized_url FROM changes WHERE changes.id=managed_feeds.id)," +
    "display_name=(SELECT display_name FROM changes WHERE changes.id=managed_feeds.id)," +
    "site_url=(SELECT site_url FROM changes WHERE changes.id=managed_feeds.id)," +
    "category_id=(SELECT category_id FROM changes WHERE changes.id=managed_feeds.id)," +
    "view_kind=(SELECT view_kind FROM changes WHERE changes.id=managed_feeds.id)," +
    "full_text_policy=(SELECT full_text_policy FROM changes WHERE changes.id=managed_feeds.id)," +
    "refresh_interval_minutes=(SELECT refresh_interval_minutes FROM changes WHERE changes.id=managed_feeds.id)," +
    "sort_order=(SELECT sort_order FROM changes WHERE changes.id=managed_feeds.id)," +
    "is_enabled=(SELECT is_enabled FROM changes WHERE changes.id=managed_feeds.id)," +
    "ai_manual_summary_policy=(SELECT ai_manual_summary_policy FROM changes WHERE changes.id=managed_feeds.id)," +
    "ai_auto_summary_policy=(SELECT ai_auto_summary_policy FROM changes WHERE changes.id=managed_feeds.id)," +
    "ai_auto_translation_policy=(SELECT ai_auto_translation_policy FROM changes WHERE changes.id=managed_feeds.id)," +
    "ai_translation_target_language=(SELECT ai_translation_target_language FROM changes WHERE changes.id=managed_feeds.id)," +
    "ai_daily_entry_limit=(SELECT ai_daily_entry_limit FROM changes WHERE changes.id=managed_feeds.id)," +
    "ai_max_concurrency=(SELECT ai_max_concurrency FROM changes WHERE changes.id=managed_feeds.id)," +
    "version=(SELECT version FROM changes WHERE changes.id=managed_feeds.id)," +
    "updated_at=(SELECT updated_at FROM changes WHERE changes.id=managed_feeds.id) " +
    "WHERE id IN (SELECT id FROM changes) AND deleted_at IS NULL AND EXISTS " +
    "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
  ).bind(rows, mutationId));
  appendJsonStatement(statements, operations, "CREATE_FEED", rows => db.prepare(
    "INSERT INTO managed_feeds(id,original_url,normalized_url,display_name,site_url,category_id,view_kind,full_text_policy," +
    "refresh_interval_minutes,sort_order,is_enabled,ai_manual_summary_policy,ai_auto_summary_policy," +
    "ai_auto_translation_policy,ai_translation_target_language,ai_daily_entry_limit,ai_max_concurrency," +
    "version,created_at,updated_at) " +
    "SELECT json_extract(value,'$.id'),json_extract(value,'$.originalUrl'),json_extract(value,'$.normalizedUrl')," +
    "json_extract(value,'$.displayName'),json_extract(value,'$.siteUrl'),json_extract(value,'$.categoryId')," +
    "json_extract(value,'$.viewKind'),json_extract(value,'$.fullTextPolicy'),json_extract(value,'$.refreshIntervalMinutes')," +
    "json_extract(value,'$.sortOrder'),json_extract(value,'$.isEnabled')," +
    "json_extract(value,'$.aiPolicy.manualSummary'),json_extract(value,'$.aiPolicy.autoSummary')," +
    "json_extract(value,'$.aiPolicy.autoTranslation'),json_extract(value,'$.aiPolicy.translationTargetLanguage')," +
    "json_extract(value,'$.aiPolicy.dailyEntryLimit'),json_extract(value,'$.aiPolicy.maxConcurrency')," +
    "json_extract(value,'$.version'),json_extract(value,'$.createdAt')," +
    "json_extract(value,'$.updatedAt') FROM json_each(?) WHERE EXISTS " +
    "(SELECT 1 FROM feed_catalog_state WHERE singleton_id=1 AND last_mutation_id=?)"
  ).bind(rows, mutationId));
  return statements;
}

function appendJsonStatement(
  statements: D1PreparedStatement[],
  operations: PreparedBatchOperation[],
  type: BatchOperationType,
  factory: (rows: string) => D1PreparedStatement
): void {
  const rows = operations.filter(operation => operation.type === type).map(operation => operation.row);
  if (rows.length > 0) statements.push(factory(JSON.stringify(rows)));
}

async function loadCatalog(db: D1Database): Promise<MutableCatalog> {
  const results = await db.batch<CategoryRow | FeedRow>([
    db.prepare(
      "SELECT id,name,name_norm,sort_order,is_enabled,ai_manual_summary_policy,ai_auto_summary_policy," +
      "ai_auto_translation_policy,ai_translation_target_language,ai_daily_entry_limit,ai_max_concurrency," +
      "version,created_at,updated_at " +
      "FROM feed_categories WHERE deleted_at IS NULL"
    ),
    db.prepare(
      "SELECT id,original_url,normalized_url,display_name,site_url,category_id,view_kind,full_text_policy," +
      "refresh_interval_minutes,sort_order,is_enabled,ai_manual_summary_policy,ai_auto_summary_policy," +
      "ai_auto_translation_policy,ai_translation_target_language,ai_daily_entry_limit,ai_max_concurrency," +
      "version,created_at,updated_at " +
      "FROM managed_feeds WHERE deleted_at IS NULL"
    )
  ]);
  const categories = (results[0]?.results ?? []) as CategoryRow[];
  const feeds = (results[1]?.results ?? []) as FeedRow[];
  return {
    categories: new Map(categories.map(category => [category.id, category])),
    feeds: new Map(feeds.map(feed => [feed.id, feed])),
    categoryReferences: new Map()
  };
}

function resolveCreateCategoryId(catalog: MutableCatalog, input: Record<string, unknown>): string | null {
  const hasCategoryId = input.categoryId !== undefined && input.categoryId !== null;
  const hasCategoryRef = input.categoryRef !== undefined && input.categoryRef !== null;
  if (hasCategoryId && hasCategoryRef) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "categoryId 与 categoryRef 不能同时提供");
  }
  if (hasCategoryId) return requireUuid(input.categoryId, "分类 ID");
  if (!hasCategoryRef) return null;
  const reference = requireRecord(input.categoryRef, "分类引用");
  assertOnlyFields(reference, ["operationId"]);
  const operationId = requireOperationId(reference.operationId);
  const categoryId = catalog.categoryReferences.get(operationId);
  if (!categoryId) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "分类引用必须指向更早创建的分类");
  }
  return categoryId;
}

function requireCategory(catalog: MutableCatalog, id: string): CategoryRow {
  const category = catalog.categories.get(id);
  if (!category) throw new CatalogApiError(404, "RESOURCE_NOT_FOUND", "分类不存在");
  return category;
}

function requireFeed(catalog: MutableCatalog, id: string): FeedRow {
  const feed = catalog.feeds.get(id);
  if (!feed) throw new CatalogApiError(404, "RESOURCE_NOT_FOUND", "Feed 不存在");
  return feed;
}

function hasCategoryName(catalog: MutableCatalog, nameNorm: string, excludedId?: string): boolean {
  return [...catalog.categories.values()].some(
    category => category.id !== excludedId && category.name_norm === nameNorm
  );
}

function hasFeedUrl(catalog: MutableCatalog, normalizedUrl: string, excludedId?: string): boolean {
  return [...catalog.feeds.values()].some(
    feed => feed.id !== excludedId && feed.normalized_url === normalizedUrl
  );
}

function operationSpec(
  operationId: string,
  resourceType: "FEED_CATEGORY" | "FEED",
  resourceId: string,
  type: BatchOperationType,
  action: string,
  targetType: "feed_category" | "feed",
  row: Record<string, unknown>
): PreparedBatchOperation {
  return {
    operationId,
    resourceType,
    resourceId,
    type,
    action,
    targetType,
    row
  };
}

function requireRecord(value: unknown, label: string): Record<string, unknown> {
  if (value === null || Array.isArray(value) || typeof value !== "object") {
    throw new CatalogApiError(400, "VALIDATION_ERROR", `${label}必须是对象`);
  }
  return value as Record<string, unknown>;
}

function requireOperationId(value: unknown): string {
  if (typeof value !== "string" || !operationIdPattern.test(value)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "operationId 格式无效");
  }
  return value;
}

function requireOperationType(value: unknown): BatchOperationType {
  if (typeof value !== "string" || !operationTypes.has(value as BatchOperationType)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "批量操作类型无效");
  }
  return value as BatchOperationType;
}

function batchFailure(operationIndex: number, operationId: string, innerCode: string): CatalogApiError {
  return new CatalogApiError(
    409,
    "BATCH_OPERATION_FAILED",
    "批量目录更新中的一项操作失败",
    { operationIndex, operationId, innerCode }
  );
}
