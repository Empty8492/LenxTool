import {
  CatalogApiError,
  CatalogAuthContext,
  assertHasFields,
  assertOnlyFields,
  findIdempotency,
  jsonText,
  nowIso,
  readJson,
  replayOrReject,
  requireBoolean,
  requireInteger,
  requireTrimmedString,
  requireUuid,
  sha256
} from "./catalog";
import { canonicalJson } from "./automation-rule-validation";

type SmartViewKind =
  "ARTICLE" | "PICTURE" | "AUDIO" | "VIDEO" | "NOTIFICATION";
type SmartViewReadFilter = "ALL" | "UNREAD" | "READ";

interface SmartViewFilter {
  feedId: string | null;
  categoryId: string | null;
  viewKind: SmartViewKind | null;
  readFilter: SmartViewReadFilter;
  favoritesOnly: boolean;
  searchText: string | null;
  publishedWithinDays: number | null;
}

interface SmartViewDefinition {
  name: string;
  sortOrder: number;
  isEnabled: boolean;
  filter: SmartViewFilter;
}

interface SmartViewSnapshot extends SmartViewDefinition {
  id: string;
  version: number;
}

interface SmartViewStateRow {
  view_set_version: number;
  updated_at: string;
}

interface SmartViewRow {
  id: string;
  current_version: number;
  name: string;
  sort_order: number;
  is_enabled: number;
  feed_id: string | null;
  category_id: string | null;
  view_kind: SmartViewKind | null;
  read_filter: SmartViewReadFilter;
  favorites_only: number;
  search_text: string | null;
  published_within_days: number | null;
}

interface PreparedMutation {
  actorUserId: string;
  method: "POST" | "PATCH" | "DELETE";
  path: string;
  key: string;
  requestHash: string;
  expectedVersion: number;
  newVersion: number;
  requestId: string;
}

interface MutationSpec {
  mutation: PreparedMutation;
  request: Request;
  status: 200 | 201;
  responseBody: string;
  targetId: string;
  snapshot: SmartViewSnapshot | null;
  action: "smart_view.created" | "smart_view.updated" | "smart_view.deleted";
  businessStatement: (
    mutationId: string,
    committedAt: string
  ) => D1PreparedStatement;
  successSql: string;
}

const maximumViews = 100;
const maximumResponseBytes = 512 * 1024;
const idempotencyKeyPattern = /^[A-Za-z0-9._:-]{16,128}$/u;
const allowedViewKinds = new Set<SmartViewKind>([
  "ARTICLE", "PICTURE", "AUDIO", "VIDEO", "NOTIFICATION"
]);
const allowedReadFilters = new Set<SmartViewReadFilter>([
  "ALL", "UNREAD", "READ"
]);

export async function handleSmartViewRequest(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  url: URL
): Promise<Response | null> {
  const itemMatch = /^\/v1\/admin\/smart-views\/([^/]+)$/u.exec(
    url.pathname
  );
  const isRead = url.pathname === "/v1/smart-views";
  const isAdminCollection = url.pathname === "/v1/admin/smart-views";
  if (!isRead && !isAdminCollection && itemMatch === null) return null;

  if (isRead && request.method === "GET") {
    return readSmartViews(request, db, auth, url);
  }
  if (auth.role !== "admin") {
    throw new CatalogApiError(403, "ADMIN_REQUIRED", "需要管理员权限");
  }
  if (url.search !== "") {
    throw new CatalogApiError(
      400,
      "VALIDATION_ERROR",
      "智能视图写入不接受查询参数"
    );
  }
  if (isAdminCollection && request.method === "POST") {
    return createSmartView(request, db, auth, url.pathname);
  }
  if (itemMatch !== null) {
    const id = requireUuid(itemMatch[1], "智能视图 ID");
    const path = `/v1/admin/smart-views/${id}`;
    if (request.method === "PATCH") {
      return updateSmartView(request, db, auth, path, id);
    }
    if (request.method === "DELETE") {
      return deleteSmartView(request, db, auth, path, id);
    }
  }
  throw new CatalogApiError(404, "RESOURCE_NOT_FOUND", "接口不存在");
}

async function readSmartViews(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  url: URL
): Promise<Response> {
  if (new TextEncoder().encode(request.url).byteLength > 2048) {
    throw new CatalogApiError(
      400,
      "VALIDATION_ERROR",
      "智能视图查询地址过长"
    );
  }
  const allowed = new Set(["scope", "afterVersion"]);
  for (const key of url.searchParams.keys()) {
    if (!allowed.has(key) || url.searchParams.getAll(key).length !== 1) {
      throw new CatalogApiError(
        400,
        "VALIDATION_ERROR",
        "智能视图查询参数无效"
      );
    }
  }
  const scope = url.searchParams.get("scope") ?? "ACTIVE";
  if (scope !== "ACTIVE" && scope !== "ALL") {
    throw new CatalogApiError(
      400,
      "VALIDATION_ERROR",
      "智能视图查询范围无效"
    );
  }
  if (scope === "ALL" && auth.role !== "admin") {
    throw new CatalogApiError(403, "ADMIN_REQUIRED", "需要管理员权限");
  }
  const afterVersion = parseVersion(
    url.searchParams.get("afterVersion"),
    "afterVersion"
  );
  const results = await db.batch<SmartViewStateRow | SmartViewRow>([
    db.prepare(
      "SELECT view_set_version,updated_at FROM smart_view_state WHERE singleton_id=1"
    ),
    db.prepare(
      "SELECT id,current_version,name,sort_order,is_enabled,feed_id,category_id," +
      "view_kind,read_filter,favorites_only,search_text,published_within_days " +
      "FROM smart_views" +
      (scope === "ACTIVE" ? " WHERE is_enabled=1" : "") +
      ` ORDER BY sort_order,name,id LIMIT ${maximumViews + 1}`
    )
  ]);
  const state = results[0]?.results[0] as SmartViewStateRow | undefined;
  if (!state ||
      !Number.isSafeInteger(state.view_set_version) ||
      state.view_set_version < 0) {
    throw new CatalogApiError(
      503,
      "SERVICE_UNAVAILABLE",
      "智能视图状态不可用"
    );
  }
  const etag = `"smart-views-${scope.toLowerCase()}-${state.view_set_version}"`;
  const conditionalVersion = afterVersion ??
    parseEtagVersion(request.headers.get("if-none-match"), scope);
  if (conditionalVersion !== undefined &&
      conditionalVersion > state.view_set_version) {
    throw new CatalogApiError(
      409,
      "SMART_VIEW_VERSION_AHEAD",
      "客户端智能视图版本高于服务端版本",
      { currentViewSetVersion: state.view_set_version },
      true
    );
  }
  if (conditionalVersion === state.view_set_version) {
    return new Response(null, {
      status: 304,
      headers: {
        "cache-control": "no-store, no-transform",
        etag,
        "x-request-id": auth.requestId
      }
    });
  }

  const rows = (results[1]?.results ?? []) as SmartViewRow[];
  if (rows.length > maximumViews) {
    throw new CatalogApiError(
      503,
      "SERVICE_UNAVAILABLE",
      "智能视图数量超过发布上限"
    );
  }
  const body = JSON.stringify({
    viewSetVersion: state.view_set_version,
    scope,
    generatedAt: state.updated_at,
    limits: {
      maximumViews,
      maximumNameCodePoints: 120,
      maximumSearchCodePoints: 200
    },
    views: rows.map(toSnapshot)
  });
  if (new TextEncoder().encode(body).byteLength > maximumResponseBytes) {
    throw new CatalogApiError(
      503,
      "SERVICE_UNAVAILABLE",
      "智能视图快照超过发布上限"
    );
  }
  return smartViewJson(body, etag, auth.requestId);
}

async function createSmartView(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string
): Promise<Response> {
  const body = await readJson(request, 16_384);
  const prepared = await prepareMutation(request, db, auth, path, body);
  if (prepared instanceof Response) return prepared;
  const definition = normalizeDefinition(body);
  const count = await db.prepare(
    "SELECT COUNT(*) AS count FROM smart_views"
  ).first<{ count: number }>();
  if ((count?.count ?? maximumViews) >= maximumViews) {
    throw new CatalogApiError(
      409,
      "SMART_VIEW_LIMIT_REACHED",
      "智能视图数量已达到上限"
    );
  }
  const snapshot: SmartViewSnapshot = {
    id: crypto.randomUUID(),
    version: 1,
    ...definition
  };
  return commitMutation(db, mutationSpec(
    prepared,
    request,
    201,
    snapshot,
    "smart_view.created",
    (mutationId, committedAt) => insertStatement(
      db,
      snapshot,
      auth.userId,
      mutationId,
      committedAt
    )
  ));
}

async function updateSmartView(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string,
  id: string
): Promise<Response> {
  const body = await readJson(request, 16_384);
  const prepared = await prepareMutation(request, db, auth, path, body);
  if (prepared instanceof Response) return prepared;
  const current = await getSmartView(db, id);
  const snapshot: SmartViewSnapshot = {
    id,
    version: current.current_version + 1,
    ...normalizeDefinition(body)
  };
  return commitMutation(db, mutationSpec(
    prepared,
    request,
    200,
    snapshot,
    "smart_view.updated",
    (mutationId, committedAt) => updateStatement(
      db,
      snapshot,
      current.current_version,
      auth.userId,
      mutationId,
      committedAt
    )
  ));
}

async function deleteSmartView(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string,
  id: string
): Promise<Response> {
  await requireEmptyBody(request);
  const prepared = await prepareMutation(request, db, auth, path, null);
  if (prepared instanceof Response) return prepared;
  const current = await getSmartView(db, id);
  const responseBody = JSON.stringify({
    viewSetVersion: prepared.newVersion,
    deletedViewId: id
  });
  return commitMutation(db, {
    mutation: prepared,
    request,
    status: 200,
    responseBody,
    targetId: id,
    snapshot: null,
    action: "smart_view.deleted",
    businessStatement: mutationId => db.prepare(
      "DELETE FROM smart_views WHERE id=? AND current_version=? AND EXISTS (" +
      "SELECT 1 FROM smart_view_state WHERE singleton_id=1 AND last_mutation_id=?)"
    ).bind(id, current.current_version, mutationId),
    successSql:
      "NOT EXISTS (SELECT 1 FROM smart_views WHERE id=?) AND " +
      "EXISTS (SELECT 1 FROM smart_view_state WHERE singleton_id=1 AND last_mutation_id=?)"
  });
}

async function requireEmptyBody(request: Request): Promise<void> {
  if (!request.body) return;
  const reader = request.body.getReader();
  try {
    const first = await reader.read();
    if (!first.done && first.value.byteLength > 0) {
      try {
        await reader.cancel();
      } catch {
        // Preserve the validation error if body cancellation also fails.
      }
      throw new CatalogApiError(
        400,
        "VALIDATION_ERROR",
        "删除智能视图不接受请求正文"
      );
    }
  } finally {
    reader.releaseLock();
  }
}

function mutationSpec(
  mutation: PreparedMutation,
  request: Request,
  status: 200 | 201,
  snapshot: SmartViewSnapshot,
  action: MutationSpec["action"],
  businessStatement: MutationSpec["businessStatement"]
): MutationSpec {
  return {
    mutation,
    request,
    status,
    responseBody: JSON.stringify({
      viewSetVersion: mutation.newVersion,
      view: snapshot
    }),
    targetId: snapshot.id,
    snapshot,
    action,
    businessStatement,
    successSql:
      "EXISTS (SELECT 1 FROM smart_views WHERE id=? AND last_mutation_id=?)"
  };
}

async function prepareMutation(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string,
  body: unknown
): Promise<PreparedMutation | Response> {
  if (request.method !== "POST" &&
      request.method !== "PATCH" &&
      request.method !== "DELETE") {
    throw new CatalogApiError(
      400,
      "VALIDATION_ERROR",
      "智能视图写入方法无效"
    );
  }
  const key = request.headers.get("idempotency-key") ?? "";
  if (!idempotencyKeyPattern.test(key)) {
    throw new CatalogApiError(
      400,
      "VALIDATION_ERROR",
      "Idempotency-Key 格式无效"
    );
  }
  const ifMatch = request.headers.get("if-match") ?? "";
  const match = /^"smart-views-all-(0|[1-9][0-9]*)"$/u.exec(ifMatch);
  if (!match) {
    throw new CatalogApiError(
      400,
      "VALIDATION_ERROR",
      "If-Match 格式无效"
    );
  }
  const expectedVersion = Number(match[1]);
  if (!Number.isSafeInteger(expectedVersion)) {
    throw new CatalogApiError(
      400,
      "VALIDATION_ERROR",
      "智能视图版本超出支持范围"
    );
  }
  const requestHash = await sha256(
    `${request.method}\n${path}\n${ifMatch}\n${canonicalJson(body)}`
  );
  const currentTime = nowIso();
  await db.prepare(
    "DELETE FROM catalog_idempotency WHERE actor_user_id=? AND http_method=? " +
    "AND normalized_path=? AND idempotency_key=? AND expires_at<=?"
  ).bind(
    auth.userId,
    request.method,
    path,
    key,
    currentTime
  ).run();
  const stored = await findIdempotency(
    db,
    auth.userId,
    request.method,
    path,
    key,
    currentTime
  );
  if (stored) return replayOrReject(stored, requestHash, auth.requestId);
  const currentVersion = await getViewSetVersion(db);
  if (currentVersion !== expectedVersion) {
    throw versionConflict(currentVersion);
  }
  if (currentVersion >= Number.MAX_SAFE_INTEGER) {
    throw new CatalogApiError(
      503,
      "SERVICE_UNAVAILABLE",
      "智能视图版本已达到服务上限"
    );
  }
  return {
    actorUserId: auth.userId,
    method: request.method,
    path,
    key,
    requestHash,
    expectedVersion,
    newVersion: currentVersion + 1,
    requestId: auth.requestId
  };
}

async function commitMutation(
  db: D1Database,
  spec: MutationSpec
): Promise<Response> {
  const mutationId = crypto.randomUUID();
  const committedAt = nowIso();
  const expiresAt = new Date(
    Date.now() + 24 * 60 * 60 * 1000
  ).toISOString();
  const ip = spec.request.headers.get("cf-connecting-ip");
  const ipHash = ip ? await sha256(ip) : null;
  const successArguments = spec.snapshot === null
    ? [spec.targetId, mutationId]
    : [spec.targetId, mutationId];
  const versionStatement = spec.snapshot === null
    ? db.prepare("SELECT 1")
    : db.prepare(
      "INSERT INTO smart_view_versions(view_id,version,snapshot_json,published_by,published_at) " +
      `SELECT ?,?,?,?,? WHERE ${spec.successSql}`
    ).bind(
      spec.snapshot.id,
      spec.snapshot.version,
      JSON.stringify(spec.snapshot),
      spec.mutation.actorUserId,
      committedAt,
      ...successArguments
    );
  const auditStatement = db.prepare(
    "INSERT INTO audit_events(id,actor_user_id,target_type,target_id,action,request_id,ip_hash,created_at) " +
    `SELECT ?,?,?,?,?,?,?,? WHERE ${spec.successSql}`
  ).bind(
    crypto.randomUUID(),
    spec.mutation.actorUserId,
    "smart_view",
    spec.targetId,
    spec.action,
    spec.mutation.requestId,
    ipHash,
    committedAt,
    ...successArguments
  );
  const idempotencyStatement = db.prepare(
    "INSERT INTO catalog_idempotency(actor_user_id,http_method,normalized_path,idempotency_key," +
    "request_hash,status_code,response_body,created_at,expires_at) " +
    `SELECT ?,?,?,?,?,?,?,?,? WHERE ${spec.successSql}`
  ).bind(
    spec.mutation.actorUserId,
    spec.mutation.method,
    spec.mutation.path,
    spec.mutation.key,
    spec.mutation.requestHash,
    spec.status,
    spec.responseBody,
    committedAt,
    expiresAt,
    ...successArguments
  );
  const guardStatement = db.prepare(
    "INSERT INTO catalog_mutation_guards(mutation_id,valid) VALUES(?,CASE WHEN EXISTS (" +
    "SELECT 1 FROM catalog_idempotency WHERE actor_user_id=? AND http_method=? AND normalized_path=? " +
    "AND idempotency_key=? AND request_hash=?) THEN 1 ELSE 0 END)"
  ).bind(
    mutationId,
    spec.mutation.actorUserId,
    spec.mutation.method,
    spec.mutation.path,
    spec.mutation.key,
    spec.mutation.requestHash
  );
  try {
    await db.batch([
      db.prepare(
        "UPDATE smart_view_state SET view_set_version=?,updated_at=?,last_mutation_id=? " +
        "WHERE singleton_id=1 AND view_set_version=?"
      ).bind(
        spec.mutation.newVersion,
        committedAt,
        mutationId,
        spec.mutation.expectedVersion
      ),
      spec.businessStatement(mutationId, committedAt),
      versionStatement,
      auditStatement,
      idempotencyStatement,
      guardStatement,
      db.prepare(
        "DELETE FROM catalog_mutation_guards WHERE mutation_id=?"
      ).bind(mutationId)
    ]);
  } catch {
    const stored = await findIdempotency(
      db,
      spec.mutation.actorUserId,
      spec.mutation.method,
      spec.mutation.path,
      spec.mutation.key,
      nowIso()
    );
    if (stored) {
      return replayOrReject(
        stored,
        spec.mutation.requestHash,
        spec.mutation.requestId
      );
    }
    const currentVersion = await getViewSetVersion(db);
    if (currentVersion !== spec.mutation.expectedVersion) {
      throw versionConflict(currentVersion);
    }
    throw new CatalogApiError(
      500,
      "INTERNAL_ERROR",
      "智能视图发布失败，请稍后重试"
    );
  }
  return jsonText(
    spec.responseBody,
    spec.status,
    spec.mutation.requestId
  );
}

function insertStatement(
  db: D1Database,
  snapshot: SmartViewSnapshot,
  actorUserId: string,
  mutationId: string,
  committedAt: string
): D1PreparedStatement {
  return db.prepare(
    "INSERT INTO smart_views(id,current_version,name,sort_order,is_enabled,feed_id,category_id," +
    "view_kind,read_filter,favorites_only,search_text,published_within_days,created_by,updated_by," +
    "created_at,updated_at,last_mutation_id) SELECT ?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,? WHERE EXISTS (" +
    "SELECT 1 FROM smart_view_state WHERE singleton_id=1 AND last_mutation_id=?)"
  ).bind(
    snapshot.id,
    snapshot.version,
    snapshot.name,
    snapshot.sortOrder,
    snapshot.isEnabled ? 1 : 0,
    snapshot.filter.feedId,
    snapshot.filter.categoryId,
    snapshot.filter.viewKind,
    snapshot.filter.readFilter,
    snapshot.filter.favoritesOnly ? 1 : 0,
    snapshot.filter.searchText,
    snapshot.filter.publishedWithinDays,
    actorUserId,
    actorUserId,
    committedAt,
    committedAt,
    mutationId,
    mutationId
  );
}

function updateStatement(
  db: D1Database,
  snapshot: SmartViewSnapshot,
  currentVersion: number,
  actorUserId: string,
  mutationId: string,
  committedAt: string
): D1PreparedStatement {
  return db.prepare(
    "UPDATE smart_views SET current_version=?,name=?,sort_order=?,is_enabled=?,feed_id=?,category_id=?," +
    "view_kind=?,read_filter=?,favorites_only=?,search_text=?,published_within_days=?,updated_by=?," +
    "updated_at=?,last_mutation_id=? WHERE id=? AND current_version=? AND EXISTS (" +
    "SELECT 1 FROM smart_view_state WHERE singleton_id=1 AND last_mutation_id=?)"
  ).bind(
    snapshot.version,
    snapshot.name,
    snapshot.sortOrder,
    snapshot.isEnabled ? 1 : 0,
    snapshot.filter.feedId,
    snapshot.filter.categoryId,
    snapshot.filter.viewKind,
    snapshot.filter.readFilter,
    snapshot.filter.favoritesOnly ? 1 : 0,
    snapshot.filter.searchText,
    snapshot.filter.publishedWithinDays,
    actorUserId,
    committedAt,
    mutationId,
    snapshot.id,
    currentVersion,
    mutationId
  );
}

function normalizeDefinition(
  body: Record<string, unknown>
): SmartViewDefinition {
  assertOnlyFields(body, ["name", "sortOrder", "isEnabled", "filter"]);
  assertHasFields(
    body,
    "智能视图必须包含名称、排序、启用状态和筛选定义"
  );
  if (body.filter === null ||
      Array.isArray(body.filter) ||
      typeof body.filter !== "object") {
    throw new CatalogApiError(
      400,
      "VALIDATION_ERROR",
      "智能视图筛选定义无效"
    );
  }
  const filter = body.filter as Record<string, unknown>;
  assertOnlyFields(filter, [
    "feedId",
    "categoryId",
    "viewKind",
    "readFilter",
    "favoritesOnly",
    "searchText",
    "publishedWithinDays"
  ]);
  const viewKind = nullableEnum(
    filter.viewKind,
    allowedViewKinds,
    "内容视图类别"
  );
  const readFilter = nullableEnum(
    filter.readFilter,
    allowedReadFilters,
    "已读筛选"
  ) ?? "ALL";
  return {
    name: requireTrimmedString(
      body.name,
      "智能视图名称",
      120
    ),
    sortOrder: requireInteger(body.sortOrder, 0, 1000, "排序"),
    isEnabled: requireBoolean(body.isEnabled, "启用状态"),
    filter: {
      feedId: nullableUuid(filter.feedId, "Feed ID"),
      categoryId: nullableUuid(filter.categoryId, "分类 ID"),
      viewKind,
      readFilter,
      favoritesOnly: filter.favoritesOnly === undefined
        ? false
        : requireBoolean(filter.favoritesOnly, "仅收藏"),
      searchText: nullableText(
        filter.searchText,
        "搜索关键词",
        200
      ),
      publishedWithinDays: filter.publishedWithinDays == null
        ? null
        : requireInteger(
          filter.publishedWithinDays,
          1,
          365,
          "发布时间窗口"
        )
    }
  };
}

function nullableUuid(value: unknown, label: string): string | null {
  return value == null ? null : requireUuid(value, label);
}

function nullableText(
  value: unknown,
  label: string,
  maximum: number
): string | null {
  return value == null
    ? null
    : requireTrimmedString(value, label, maximum);
}

function nullableEnum<T extends string>(
  value: unknown,
  allowed: ReadonlySet<T>,
  label: string
): T | null {
  if (value == null) return null;
  if (typeof value !== "string" || !allowed.has(value as T)) {
    throw new CatalogApiError(
      400,
      "VALIDATION_ERROR",
      `${label}无效`
    );
  }
  return value as T;
}

function toSnapshot(row: SmartViewRow): SmartViewSnapshot {
  return {
    id: requireUuid(row.id, "智能视图 ID"),
    version: requireInteger(
      row.current_version,
      1,
      Number.MAX_SAFE_INTEGER,
      "智能视图版本"
    ),
    name: requireTrimmedString(row.name, "智能视图名称", 120),
    sortOrder: requireInteger(row.sort_order, 0, 1000, "排序"),
    isEnabled: row.is_enabled === 1,
    filter: {
      feedId: nullableUuid(row.feed_id, "Feed ID"),
      categoryId: nullableUuid(row.category_id, "分类 ID"),
      viewKind: nullableEnum(
        row.view_kind,
        allowedViewKinds,
        "内容视图类别"
      ),
      readFilter: nullableEnum(
        row.read_filter,
        allowedReadFilters,
        "已读筛选"
      ) ?? "ALL",
      favoritesOnly: row.favorites_only === 1,
      searchText: nullableText(row.search_text, "搜索关键词", 200),
      publishedWithinDays: row.published_within_days == null
        ? null
        : requireInteger(
          row.published_within_days,
          1,
          365,
          "发布时间窗口"
        )
    }
  };
}

async function getSmartView(
  db: D1Database,
  id: string
): Promise<SmartViewRow> {
  const row = await db.prepare(
    "SELECT id,current_version,name,sort_order,is_enabled,feed_id,category_id," +
    "view_kind,read_filter,favorites_only,search_text,published_within_days " +
    "FROM smart_views WHERE id=?"
  ).bind(id).first<SmartViewRow>();
  if (!row) {
    throw new CatalogApiError(
      404,
      "RESOURCE_NOT_FOUND",
      "智能视图不存在"
    );
  }
  return row;
}

async function getViewSetVersion(db: D1Database): Promise<number> {
  const row = await db.prepare(
    "SELECT view_set_version FROM smart_view_state WHERE singleton_id=1"
  ).first<{ view_set_version: number }>();
  if (!row ||
      !Number.isSafeInteger(row.view_set_version) ||
      row.view_set_version < 0) {
    throw new CatalogApiError(
      503,
      "SERVICE_UNAVAILABLE",
      "智能视图状态不可用"
    );
  }
  return row.view_set_version;
}

function parseVersion(
  value: string | null,
  label: string
): number | undefined {
  if (value === null) return undefined;
  if (!/^(0|[1-9][0-9]*)$/u.test(value)) {
    throw new CatalogApiError(
      400,
      "VALIDATION_ERROR",
      `${label} 格式无效`
    );
  }
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed)) {
    throw new CatalogApiError(
      400,
      "VALIDATION_ERROR",
      `${label} 超出支持范围`
    );
  }
  return parsed;
}

function parseEtagVersion(
  value: string | null,
  scope: "ACTIVE" | "ALL"
): number | undefined {
  if (value === null) return undefined;
  const match = new RegExp(
    `^"smart-views-${scope.toLowerCase()}-(0|[1-9][0-9]*)"$`,
    "u"
  ).exec(value);
  if (!match) return undefined;
  const parsed = Number(match[1]);
  return Number.isSafeInteger(parsed) ? parsed : undefined;
}

function versionConflict(currentVersion: number): CatalogApiError {
  return new CatalogApiError(
    409,
    "SMART_VIEW_VERSION_CONFLICT",
    "其他管理员已经修改了智能视图",
    { currentViewSetVersion: currentVersion },
    true
  );
}

function smartViewJson(
  body: string,
  etag: string,
  requestId: string
): Response {
  return new Response(body, {
    status: 200,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store, no-transform",
      etag,
      "x-request-id": requestId
    }
  });
}
