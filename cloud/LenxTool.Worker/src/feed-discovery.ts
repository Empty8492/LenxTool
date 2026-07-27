import {
  CatalogApiError,
  type CatalogAuthContext,
  type ViewKind
} from "./catalog";

type DiscoveryScope = "ACTIVE" | "ALL";
type MatchKind = "EXACT_FEED_URL" | "EXACT_SITE_URL" | "EXACT_TITLE" | "KEYWORD";
type Confidence = "EXACT" | "HIGH" | "MEDIUM";

interface DiscoveryCursor {
  version: 1;
  query: string;
  scope: DiscoveryScope;
  rank: number;
  updatedAt: string;
  feedId: string;
}

interface DiscoveryConditions {
  query: string;
  foldedQuery: string;
  pageSize: number;
  scope: DiscoveryScope;
  cursor?: DiscoveryCursor;
}

interface DiscoveryRow {
  feed_id: string;
  normalized_url: string;
  display_name: string;
  site_url: string | null;
  category_id: string | null;
  category_name: string | null;
  category_is_enabled: number;
  view_kind: ViewKind;
  feed_is_enabled: number;
  updated_at: string;
  rank: number;
}

interface CountRow {
  total: number;
}

interface CatalogStateRow {
  catalog_version: number;
}

const encoder = new TextEncoder();
const decoder = new TextDecoder();
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu;
const base64UrlPattern = /^[A-Za-z0-9_-]+$/u;
const controlCharacterPattern = /[\p{Cc}\p{Cf}]/u;
const maximumRequestBytes = 2048;
const maximumQueryCodePoints = 200;
const maximumCursorCharacters = 1024;
const defaultPageSize = 20;
const maximumPageSize = 50;
const maximumRequestsPerMinute = 60;

export async function handleFeedDiscoveryRequest(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  url: URL
): Promise<Response | null> {
  if (url.pathname !== "/v1/feeds/discoveries" || request.method !== "GET") {
    return null;
  }
  if (encoder.encode(request.url).byteLength > maximumRequestBytes) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "发现查询地址过长");
  }

  await enforceRateLimit(db, auth);
  const conditions = parseConditions(url);
  if (conditions.scope === "ALL" && auth.role !== "admin") {
    throw new CatalogApiError(403, "ADMIN_REQUIRED", "需要管理员权限");
  }

  const search = createSearchSql(conditions);
  const rankSql =
    "CASE " +
    "WHEN normalized_url=? THEN 500 " +
    "WHEN site_url=? THEN 450 " +
    "WHEN display_name_norm=? THEN 400 " +
    "WHEN display_name_norm LIKE ? ESCAPE '\\' THEN 300 " +
    "WHEN display_name_norm LIKE ? ESCAPE '\\' THEN 250 " +
    "WHEN category_name_norm=? THEN 200 " +
    "WHEN category_name_norm LIKE ? ESCAPE '\\' THEN 150 " +
    "ELSE 100 END";
  const cursorSql = conditions.cursor
    ? " AND (rank<? OR (rank=? AND updated_at<?) OR " +
      "(rank=? AND updated_at=? AND feed_id>?))"
    : "";
  const pageSql =
    "WITH ranked AS (" +
    "SELECT feed_id,normalized_url,display_name,site_url,category_id,category_name," +
    "category_is_enabled,view_kind,feed_is_enabled,updated_at," +
    `${rankSql} AS rank FROM feed_discovery_index WHERE ${search.whereSql}` +
    ") SELECT feed_id,normalized_url,display_name,site_url,category_id,category_name," +
    "category_is_enabled,view_kind,feed_is_enabled,updated_at,rank FROM ranked WHERE rank>0" +
    cursorSql +
    " ORDER BY rank DESC,updated_at DESC,feed_id LIMIT ?";
  const pageBindings: unknown[] = [
    conditions.query,
    conditions.query,
    conditions.foldedQuery,
    search.prefixPattern,
    search.containsPattern,
    conditions.foldedQuery,
    search.containsPattern,
    ...search.bindings
  ];
  if (conditions.cursor) {
    pageBindings.push(
      conditions.cursor.rank,
      conditions.cursor.rank,
      conditions.cursor.updatedAt,
      conditions.cursor.rank,
      conditions.cursor.updatedAt,
      conditions.cursor.feedId
    );
  }
  pageBindings.push(conditions.pageSize + 1);

  const results = await db.batch<CatalogStateRow | CountRow | DiscoveryRow>([
    db.prepare(
      "SELECT catalog_version FROM feed_catalog_state WHERE singleton_id=1"
    ),
    db.prepare(
      `SELECT COUNT(*) AS total FROM feed_discovery_index WHERE ${search.whereSql}`
    ).bind(...search.bindings),
    db.prepare(pageSql).bind(...pageBindings)
  ]);
  const state = results[0]?.results[0] as CatalogStateRow | undefined;
  const count = results[1]?.results[0] as CountRow | undefined;
  if (!state
    || !Number.isSafeInteger(state.catalog_version)
    || state.catalog_version < 0
    || !count
    || !Number.isSafeInteger(count.total)
    || count.total < 0) {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "发现索引状态不可用");
  }

  const rows = (results[2]?.results ?? []) as DiscoveryRow[];
  const hasMore = rows.length > conditions.pageSize;
  const pageRows = rows.slice(0, conditions.pageSize);
  const nextCursor = hasMore
    ? encodeCursor(pageRows.at(-1)!, conditions)
    : null;
  const body = JSON.stringify({
    catalogVersion: state.catalog_version,
    query: conditions.query,
    scope: conditions.scope,
    items: pageRows.map(toDiscoveryItem),
    pagination: {
      pageSize: conditions.pageSize,
      totalItems: count.total,
      nextCursor
    }
  });
  const etag = await discoveryEtag(state.catalog_version, conditions);
  if (request.headers.get("if-none-match") === etag) {
    return new Response(null, {
      status: 304,
      headers: discoveryHeaders(etag, auth.requestId, false)
    });
  }
  return new Response(body, {
    status: 200,
    headers: discoveryHeaders(etag, auth.requestId, true)
  });
}

function parseConditions(url: URL): DiscoveryConditions {
  for (const key of url.searchParams.keys()) {
    if (key !== "query" && key !== "pageSize" && key !== "scope" && key !== "cursor") {
      throw validationError("发现查询包含未知参数");
    }
  }
  for (const key of ["query", "pageSize", "scope", "cursor"]) {
    if (url.searchParams.getAll(key).length > 1) {
      throw validationError("发现查询参数不能重复");
    }
  }

  const rawQuery = url.searchParams.get("query");
  if (rawQuery === null || controlCharacterPattern.test(rawQuery)) {
    throw validationError("发现关键词格式无效");
  }
  const query = normalizeQuery(rawQuery);
  if (query.length === 0 || [...query].length > maximumQueryCodePoints) {
    throw validationError("发现关键词长度必须为 1～200 个字符");
  }

  const pageSizeValue = url.searchParams.get("pageSize");
  let pageSize = defaultPageSize;
  if (pageSizeValue !== null) {
    if (!/^[1-9][0-9]?$/u.test(pageSizeValue)) {
      throw validationError("发现分页大小格式无效");
    }
    pageSize = Number(pageSizeValue);
    if (!Number.isSafeInteger(pageSize) || pageSize > maximumPageSize) {
      throw validationError("发现分页大小必须为 1～50");
    }
  }

  const scopeValue = url.searchParams.get("scope") ?? "ACTIVE";
  if (scopeValue !== "ACTIVE" && scopeValue !== "ALL") {
    throw validationError("发现查询范围无效");
  }
  const conditions: DiscoveryConditions = {
    query,
    foldedQuery: query.toLocaleLowerCase("en-US"),
    pageSize,
    scope: scopeValue
  };

  const cursorValue = url.searchParams.get("cursor");
  if (cursorValue !== null) {
    conditions.cursor = decodeCursor(cursorValue, conditions);
  }
  return conditions;
}

function normalizeQuery(value: string): string {
  return value
    .normalize("NFKC")
    .trim()
    .replace(/\s+/gu, " ");
}

function createSearchSql(conditions: DiscoveryConditions): {
  whereSql: string;
  bindings: unknown[];
  prefixPattern: string;
  containsPattern: string;
} {
  const escaped = escapeLikePattern(conditions.foldedQuery);
  const prefixPattern = `${escaped}%`;
  const containsPattern = `%${escaped}%`;
  const scopeSql = conditions.scope === "ACTIVE"
    ? "feed_is_enabled=1 AND (category_id IS NULL OR category_is_enabled=1) AND "
    : "";
  return {
    whereSql:
      scopeSql +
      "(display_name_norm LIKE ? ESCAPE '\\' " +
      "OR lower(normalized_url) LIKE ? ESCAPE '\\' " +
      "OR lower(COALESCE(site_url,'')) LIKE ? ESCAPE '\\' " +
      "OR COALESCE(category_name_norm,'') LIKE ? ESCAPE '\\')",
    bindings: [
      containsPattern,
      containsPattern,
      containsPattern,
      containsPattern
    ],
    prefixPattern,
    containsPattern
  };
}

function escapeLikePattern(value: string): string {
  return value.replace(/[\\%_]/gu, character => `\\${character}`);
}

function decodeCursor(
  value: string,
  conditions: Pick<DiscoveryConditions, "query" | "scope">
): DiscoveryCursor {
  if (value.length === 0
    || value.length > maximumCursorCharacters
    || !base64UrlPattern.test(value)) {
    throw validationError("发现游标格式无效");
  }
  try {
    const json = decoder.decode(fromBase64Url(value));
    const parsed = JSON.parse(json) as unknown;
    if (!isRecord(parsed)
      || Object.keys(parsed).sort().join(",")
        !== "feedId,query,rank,scope,updatedAt,version"
      || parsed.version !== 1
      || parsed.query !== conditions.query
      || parsed.scope !== conditions.scope
      || !Number.isSafeInteger(parsed.rank)
      || (parsed.rank as number) < 1
      || (parsed.rank as number) > 500
      || typeof parsed.updatedAt !== "string"
      || parsed.updatedAt.length < 20
      || parsed.updatedAt.length > 40
      || typeof parsed.feedId !== "string"
      || !uuidPattern.test(parsed.feedId)) {
      throw validationError("发现游标与当前查询不匹配");
    }
    return parsed as unknown as DiscoveryCursor;
  } catch (error) {
    if (error instanceof CatalogApiError) throw error;
    throw validationError("发现游标格式无效");
  }
}

function encodeCursor(
  row: DiscoveryRow,
  conditions: Pick<DiscoveryConditions, "query" | "scope">
): string {
  const cursor: DiscoveryCursor = {
    version: 1,
    query: conditions.query,
    scope: conditions.scope,
    rank: row.rank,
    updatedAt: row.updated_at,
    feedId: row.feed_id
  };
  return toBase64Url(encoder.encode(JSON.stringify(cursor)));
}

function toDiscoveryItem(row: DiscoveryRow): {
  normalizedFeedUrl: string;
  title: string;
  siteUrl: string | null;
  documentKind: null;
  lastUpdatedAt: string;
  health: "UNKNOWN";
  evidence: Array<{
    sourceId: "worker:known-catalog";
    sourceKind: "KNOWN_CATALOG";
    matchKind: MatchKind;
    confidence: Confidence;
  }>;
  warnings: [];
  catalog: {
    feedId: string;
    categoryId: string | null;
    categoryName: string | null;
    viewKind: ViewKind;
    isEnabled: boolean;
  };
} {
  const evidence = matchEvidence(row.rank);
  return {
    normalizedFeedUrl: row.normalized_url,
    title: row.display_name,
    siteUrl: row.site_url,
    documentKind: null,
    lastUpdatedAt: row.updated_at,
    health: "UNKNOWN",
    evidence: [{
      sourceId: "worker:known-catalog",
      sourceKind: "KNOWN_CATALOG",
      ...evidence
    }],
    warnings: [],
    catalog: {
      feedId: row.feed_id,
      categoryId: row.category_id,
      categoryName: row.category_name,
      viewKind: row.view_kind,
      isEnabled: row.feed_is_enabled === 1
        && (row.category_id === null || row.category_is_enabled === 1)
    }
  };
}

function matchEvidence(rank: number): {
  matchKind: MatchKind;
  confidence: Confidence;
} {
  if (rank === 500) {
    return { matchKind: "EXACT_FEED_URL", confidence: "EXACT" };
  }
  if (rank === 450) {
    return { matchKind: "EXACT_SITE_URL", confidence: "EXACT" };
  }
  if (rank === 400) {
    return { matchKind: "EXACT_TITLE", confidence: "HIGH" };
  }
  return { matchKind: "KEYWORD", confidence: "MEDIUM" };
}

async function enforceRateLimit(
  db: D1Database,
  auth: CatalogAuthContext
): Promise<void> {
  const now = new Date();
  const bucket = now.toISOString().slice(0, 16);
  const oldestBucket = new Date(now.getTime() - 2 * 60 * 60 * 1000)
    .toISOString()
    .slice(0, 16);
  const results = await db.batch<{ attempts: number }>([
    db.prepare(
      "DELETE FROM feed_discovery_rate_limits WHERE bucket<?"
    ).bind(oldestBucket),
    db.prepare(
      "INSERT INTO feed_discovery_rate_limits(actor_user_id,bucket,attempts) VALUES(?,?,1) " +
      "ON CONFLICT(actor_user_id,bucket) DO UPDATE SET attempts=MIN(attempts+1,1000000)"
    ).bind(auth.userId, bucket),
    db.prepare(
      "SELECT attempts FROM feed_discovery_rate_limits WHERE actor_user_id=? AND bucket=?"
    ).bind(auth.userId, bucket)
  ]);
  const attempts = results[2]?.results[0]?.attempts;
  if (typeof attempts !== "number" || !Number.isSafeInteger(attempts)) {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "发现限流状态不可用");
  }
  if (attempts > maximumRequestsPerMinute) {
    throw new CatalogApiError(
      429,
      "RATE_LIMITED",
      "发现查询过于频繁",
      undefined,
      true,
      60
    );
  }
}

async function discoveryEtag(
  catalogVersion: number,
  conditions: DiscoveryConditions
): Promise<string> {
  const cacheKey = JSON.stringify({
    query: conditions.query,
    scope: conditions.scope,
    pageSize: conditions.pageSize,
    cursor: conditions.cursor ?? null
  });
  const hash = new Uint8Array(
    await crypto.subtle.digest("SHA-256", encoder.encode(cacheKey))
  );
  return `"feed-discovery-${catalogVersion}-${toBase64Url(hash.slice(0, 12))}"`;
}

function discoveryHeaders(
  etag: string,
  requestId: string,
  includeContentType: boolean
): Headers {
  const headers = new Headers({
    "cache-control": "private, max-age=60",
    "etag": etag,
    "vary": "Authorization",
    "x-request-id": requestId
  });
  if (includeContentType) {
    headers.set("content-type", "application/json; charset=utf-8");
  }
  return headers;
}

function fromBase64Url(value: string): Uint8Array {
  const base64 = value
    .replaceAll("-", "+")
    .replaceAll("_", "/")
    .padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(base64);
  return Uint8Array.from(binary, character => character.charCodeAt(0));
}

function toBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary)
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replace(/=+$/u, "");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function validationError(message: string): CatalogApiError {
  return new CatalogApiError(400, "VALIDATION_ERROR", message);
}
