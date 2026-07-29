import {
  CatalogApiError,
  CatalogAuthContext,
  assertOnlyFields,
  jsonText,
  nowIso,
  readJson,
  requireBoolean,
  sha256
} from "./catalog";
import { canonicalJson } from "./automation-rule-validation";

type IntegrationKind =
  | "OBSIDIAN"
  | "EAGLE"
  | "ZOTERO"
  | "READWISE"
  | "CUBOX"
  | "READECK"
  | "OUTLINE"
  | "QBITTORRENT"
  | "WEBHOOK";

interface IntegrationPolicy {
  kind: IntegrationKind;
  isEnabled: boolean;
  allowedHosts: string[];
}

interface PolicyStateRow {
  policy_set_version: number;
  updated_at: string;
}

interface PolicyRow {
  kind: string;
  is_enabled: number;
  allowed_hosts_json: string;
}

interface IdempotencyRow {
  request_hash: string;
  status_code: number;
  response_body: string;
}

const allowedKinds = new Set<IntegrationKind>([
  "OBSIDIAN",
  "EAGLE",
  "ZOTERO",
  "READWISE",
  "CUBOX",
  "READECK",
  "OUTLINE",
  "QBITTORRENT",
  "WEBHOOK"
]);
const kindOrder = new Map(
  [...allowedKinds].map((kind, index) => [kind, index])
);
const keyPattern = /^[A-Za-z0-9._:-]{16,128}$/u;
const reservedSuffixes = [
  ".internal",
  ".invalid",
  ".lan",
  ".local",
  ".localhost"
];

export async function handleIntegrationPolicyRequest(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  url: URL
): Promise<Response | null> {
  const isRead = url.pathname === "/v1/integration-policies";
  const isWrite =
    url.pathname === "/v1/admin/integration-policies";
  if (!isRead && !isWrite) return null;

  if (isRead && request.method === "GET") {
    return readPolicies(request, db, auth, url);
  }
  // 权限检查必须早于 JSON 解析，避免普通用户利用错误差异探测管理接口。
  if (auth.role !== "admin") {
    throw new CatalogApiError(
      403,
      "ADMIN_REQUIRED",
      "需要管理员权限"
    );
  }
  if (!isWrite || request.method !== "PUT") {
    throw new CatalogApiError(
      404,
      "RESOURCE_NOT_FOUND",
      "接口不存在"
    );
  }
  if (url.search !== "") {
    throw validationError("集成策略写入不接受查询参数");
  }
  return replacePolicies(request, db, auth);
}

async function readPolicies(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  url: URL
): Promise<Response> {
  if (new TextEncoder().encode(request.url).byteLength > 2048) {
    throw validationError("集成策略查询地址过长");
  }
  for (const key of url.searchParams.keys()) {
    if (!["scope", "afterVersion"].includes(key)
        || url.searchParams.getAll(key).length !== 1) {
      throw validationError("集成策略查询参数无效");
    }
  }
  const scope = url.searchParams.get("scope") ?? "ACTIVE";
  if (scope !== "ACTIVE" && scope !== "ALL") {
    throw validationError("集成策略查询范围无效");
  }
  if (scope === "ALL" && auth.role !== "admin") {
    throw new CatalogApiError(
      403,
      "ADMIN_REQUIRED",
      "需要管理员权限"
    );
  }
  const afterVersion = parseVersion(
    url.searchParams.get("afterVersion"),
    "afterVersion"
  );
  const [stateResult, policyResult] =
    await db.batch<PolicyStateRow | PolicyRow>([
      db.prepare(
        "SELECT policy_set_version,updated_at " +
        "FROM integration_policy_state WHERE singleton_id=1"
      ),
      db.prepare(
        "SELECT kind,is_enabled,allowed_hosts_json " +
        "FROM integration_policies" +
        (scope === "ACTIVE" ? " WHERE is_enabled=1" : "") +
        " ORDER BY kind"
      )
    ]);
  const state = stateResult?.results[0] as PolicyStateRow | undefined;
  assertState(state);
  const etag =
    `"integration-policies-${scope.toLowerCase()}-${state.policy_set_version}"`;
  const conditional = afterVersion ??
    parseEtagVersion(request.headers.get("if-none-match"), scope);
  if (conditional !== undefined
      && conditional > state.policy_set_version) {
    throw new CatalogApiError(
      409,
      "INTEGRATION_POLICY_VERSION_AHEAD",
      "客户端集成策略版本高于服务端版本",
      { currentPolicySetVersion: state.policy_set_version },
      true
    );
  }
  if (conditional === state.policy_set_version) {
    return new Response(null, {
      status: 304,
      headers: {
        "cache-control": "no-store",
        etag,
        "x-request-id": auth.requestId
      }
    });
  }

  const policies = (policyResult?.results ?? [])
    .map(row => mapStoredPolicy(row as PolicyRow))
    .sort(comparePolicies);
  return policyJson(
    JSON.stringify({
      policySetVersion: state.policy_set_version,
      scope,
      generatedAt: state.updated_at,
      policies
    }),
    etag,
    auth.requestId
  );
}

async function replacePolicies(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext
): Promise<Response> {
  const body = await readJson(request, 65_536);
  assertOnlyFields(body, ["policies"]);
  if (!Array.isArray(body.policies)) {
    throw validationError("集成策略集合无效");
  }
  const policies = normalizePolicySet(body.policies);
  const key = request.headers.get("idempotency-key") ?? "";
  if (!keyPattern.test(key)) {
    throw validationError("Idempotency-Key 格式无效");
  }
  const ifMatch = request.headers.get("if-match") ?? "";
  const match =
    /^"integration-policies-all-(0|[1-9][0-9]*)"$/u.exec(ifMatch);
  if (!match) throw validationError("If-Match 格式无效");
  const expectedVersion = Number(match[1]);
  if (!Number.isSafeInteger(expectedVersion)) {
    throw validationError("集成策略版本超出支持范围");
  }

  const requestHash = await sha256(
    `PUT\n/v1/admin/integration-policies\n${ifMatch}\n` +
    canonicalJson({ policies })
  );
  const currentTime = nowIso();
  await db.prepare(
    "DELETE FROM integration_policy_idempotency " +
    "WHERE actor_user_id=? AND idempotency_key=? AND expires_at<=?"
  ).bind(auth.userId, key, currentTime).run();
  const stored = await findIdempotency(
    db,
    auth.userId,
    key,
    currentTime
  );
  if (stored) {
    return replayOrReject(stored, requestHash, auth.requestId);
  }

  const currentVersion = await getPolicySetVersion(db);
  if (currentVersion !== expectedVersion) {
    throw versionConflict(currentVersion);
  }
  if (currentVersion >= Number.MAX_SAFE_INTEGER) {
    throw new CatalogApiError(
      503,
      "SERVICE_UNAVAILABLE",
      "集成策略版本已达到服务上限"
    );
  }

  const newVersion = currentVersion + 1;
  const mutationId = crypto.randomUUID();
  const committedAt = nowIso();
  const responseBody = JSON.stringify({
    policySetVersion: newVersion,
    policies
  });
  const expiresAt = new Date(
    Date.now() + 24 * 60 * 60 * 1000
  ).toISOString();
  const ip = request.headers.get("cf-connecting-ip");
  const ipHash = ip ? await sha256(ip) : null;
  const policyWrites = policies.map(policy =>
    db.prepare(
      "INSERT INTO integration_policies(" +
      "kind,is_enabled,allowed_hosts_json,updated_by,updated_at,last_mutation_id) " +
      "SELECT ?,?,?,?,?,? WHERE EXISTS (" +
      "SELECT 1 FROM integration_policy_state " +
      "WHERE singleton_id=1 AND last_mutation_id=?)"
    ).bind(
      policy.kind,
      policy.isEnabled ? 1 : 0,
      JSON.stringify(policy.allowedHosts),
      auth.userId,
      committedAt,
      mutationId,
      mutationId
    )
  );
  try {
    await db.batch([
      db.prepare(
        "UPDATE integration_policy_state SET policy_set_version=?," +
        "updated_at=?,last_mutation_id=? " +
        "WHERE singleton_id=1 AND policy_set_version=?"
      ).bind(newVersion, committedAt, mutationId, expectedVersion),
      db.prepare(
        "DELETE FROM integration_policies WHERE EXISTS (" +
        "SELECT 1 FROM integration_policy_state " +
        "WHERE singleton_id=1 AND last_mutation_id=?)"
      ).bind(mutationId),
      ...policyWrites,
      db.prepare(
        "INSERT INTO integration_policy_versions(" +
        "policy_set_version,snapshot_json,published_by,published_at) " +
        "SELECT ?,?,?,? WHERE EXISTS (" +
        "SELECT 1 FROM integration_policy_state " +
        "WHERE singleton_id=1 AND last_mutation_id=?)"
      ).bind(
        newVersion,
        responseBody,
        auth.userId,
        committedAt,
        mutationId
      ),
      db.prepare(
        "INSERT INTO audit_events(" +
        "id,actor_user_id,target_type,target_id,action,request_id,ip_hash,created_at) " +
        "SELECT ?,?,'integration_policy_set',NULL," +
        "'integration_policy.replaced',?,?,? WHERE EXISTS (" +
        "SELECT 1 FROM integration_policy_state " +
        "WHERE singleton_id=1 AND last_mutation_id=?)"
      ).bind(
        crypto.randomUUID(),
        auth.userId,
        auth.requestId,
        ipHash,
        committedAt,
        mutationId
      ),
      db.prepare(
        "INSERT INTO integration_policy_idempotency(" +
        "actor_user_id,idempotency_key,request_hash,status_code,response_body," +
        "created_at,expires_at) SELECT ?,?,?,200,?,?,? WHERE EXISTS (" +
        "SELECT 1 FROM integration_policy_state " +
        "WHERE singleton_id=1 AND last_mutation_id=?)"
      ).bind(
        auth.userId,
        key,
        requestHash,
        responseBody,
        committedAt,
        expiresAt,
        mutationId
      ),
      db.prepare(
        "INSERT INTO integration_policy_mutation_guards(mutation_id,valid) " +
        "VALUES(?,CASE WHEN EXISTS (" +
        "SELECT 1 FROM integration_policy_idempotency " +
        "WHERE actor_user_id=? AND idempotency_key=? AND request_hash=?" +
        ") THEN 1 ELSE 0 END)"
      ).bind(mutationId, auth.userId, key, requestHash),
      db.prepare(
        "DELETE FROM integration_policy_mutation_guards WHERE mutation_id=?"
      ).bind(mutationId)
    ]);
  } catch {
    const replay = await findIdempotency(
      db,
      auth.userId,
      key,
      nowIso()
    );
    if (replay) {
      return replayOrReject(replay, requestHash, auth.requestId);
    }
    const latestVersion = await getPolicySetVersion(db);
    if (latestVersion !== expectedVersion) {
      throw versionConflict(latestVersion);
    }
    throw new CatalogApiError(
      500,
      "INTERNAL_ERROR",
      "集成策略发布失败，请稍后重试"
    );
  }
  return jsonText(responseBody, 200, auth.requestId);
}

function normalizePolicySet(values: unknown[]): IntegrationPolicy[] {
  if (values.length > allowedKinds.size) {
    throw validationError("集成策略数量超过支持上限");
  }
  const seen = new Set<IntegrationKind>();
  const policies = values.map(value => {
    if (value === null
        || Array.isArray(value)
        || typeof value !== "object") {
      throw validationError("集成策略项无效");
    }
    const input = value as Record<string, unknown>;
    assertOnlyFields(input, ["kind", "isEnabled", "allowedHosts"]);
    if (typeof input.kind !== "string"
        || !allowedKinds.has(input.kind as IntegrationKind)) {
      throw validationError("集成类型无效");
    }
    const kind = input.kind as IntegrationKind;
    if (!seen.add(kind)) throw validationError("集成类型不能重复");
    if (!Array.isArray(input.allowedHosts)
        || input.allowedHosts.length > 32) {
      throw validationError("目标主机列表无效");
    }
    const allowedHosts = [...new Set(
      input.allowedHosts.map(normalizeExactHost)
    )].sort();
    const isEnabled = requireBoolean(input.isEnabled, "启用状态");
    if (isEnabled
        && requiresAllowedHosts(kind)
        && allowedHosts.length === 0) {
      throw validationError("启用集成前必须配置精确目标主机");
    }
    return { kind, isEnabled, allowedHosts };
  });
  return policies.sort(comparePolicies);
}

// Obsidian 只写用户授权的本地 Vault；其余集成仍受精确 DNS 白名单约束。
function requiresAllowedHosts(kind: IntegrationKind): boolean {
  return kind !== "OBSIDIAN";
}

function normalizeExactHost(value: unknown): string {
  if (typeof value !== "string") {
    throw validationError("目标主机必须是字符串");
  }
  const candidate = value.trim().replace(/\.$/u, "");
  if (candidate.length === 0
      || candidate.length > 253
      || candidate.includes("*")
      || candidate.includes("://")
      || /[/\\@:?#[\]]/u.test(candidate)
      || isIpv4Literal(candidate)) {
    throw validationError(
      "目标必须是精确 DNS 主机名，不能包含协议、端口、路径、通配符或 IP"
    );
  }
  let host: string;
  try {
    host = new URL(`https://${candidate}`).hostname
      .toLowerCase()
      .replace(/\.$/u, "");
  } catch {
    throw validationError("目标主机格式无效");
  }
  if (!host.includes(".")
      || host === "localhost"
      || reservedSuffixes.some(suffix => host.endsWith(suffix))
      || !/^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/u
        .test(host)) {
    throw validationError("目标必须是可公开解析的精确 DNS 主机名");
  }
  return host;
}

function isIpv4Literal(value: string): boolean {
  const parts = value.split(".");
  return parts.length === 4
    && parts.every(part =>
      /^(0|[1-9][0-9]{0,2})$/u.test(part)
      && Number(part) <= 255
    );
}

function mapStoredPolicy(row: PolicyRow): IntegrationPolicy {
  let hosts: unknown;
  try {
    hosts = JSON.parse(row.allowed_hosts_json);
  } catch {
    throw serviceUnavailable();
  }
  try {
    return normalizePolicySet([{
      kind: row.kind,
      isEnabled: row.is_enabled === 1,
      allowedHosts: hosts
    }])[0]!;
  } catch {
    throw serviceUnavailable();
  }
}

function comparePolicies(
  left: IntegrationPolicy,
  right: IntegrationPolicy
): number {
  return (kindOrder.get(left.kind) ?? Number.MAX_SAFE_INTEGER)
    - (kindOrder.get(right.kind) ?? Number.MAX_SAFE_INTEGER);
}

async function getPolicySetVersion(db: D1Database): Promise<number> {
  const row = await db.prepare(
    "SELECT policy_set_version FROM integration_policy_state " +
    "WHERE singleton_id=1"
  ).first<{ policy_set_version: number }>();
  assertState(row);
  return row.policy_set_version;
}

function assertState(
  row: { policy_set_version: number } | null | undefined
): asserts row is { policy_set_version: number } {
  if (!row
      || !Number.isSafeInteger(row.policy_set_version)
      || row.policy_set_version < 0) {
    throw serviceUnavailable();
  }
}

async function findIdempotency(
  db: D1Database,
  actorUserId: string,
  key: string,
  currentTime: string
): Promise<IdempotencyRow | null> {
  return db.prepare(
    "SELECT request_hash,status_code,response_body " +
    "FROM integration_policy_idempotency " +
    "WHERE actor_user_id=? AND idempotency_key=? AND expires_at>?"
  ).bind(actorUserId, key, currentTime).first<IdempotencyRow>();
}

function replayOrReject(
  stored: IdempotencyRow,
  requestHash: string,
  requestId: string
): Response {
  if (stored.request_hash !== requestHash) {
    throw new CatalogApiError(
      409,
      "IDEMPOTENCY_KEY_REUSED",
      "幂等键已经用于不同的集成策略请求"
    );
  }
  return jsonText(stored.response_body, stored.status_code, requestId);
}

function parseVersion(
  value: string | null,
  label: string
): number | undefined {
  if (value === null) return undefined;
  if (!/^(0|[1-9][0-9]*)$/u.test(value)) {
    throw validationError(`${label} 格式无效`);
  }
  const result = Number(value);
  if (!Number.isSafeInteger(result)) {
    throw validationError(`${label} 超出支持范围`);
  }
  return result;
}

function parseEtagVersion(
  value: string | null,
  scope: "ACTIVE" | "ALL"
): number | undefined {
  if (value === null) return undefined;
  const match = new RegExp(
    `^"integration-policies-${scope.toLowerCase()}-(0|[1-9][0-9]*)"$`,
    "u"
  ).exec(value);
  if (!match) return undefined;
  const result = Number(match[1]);
  return Number.isSafeInteger(result) ? result : undefined;
}

function versionConflict(currentVersion: number): CatalogApiError {
  return new CatalogApiError(
    409,
    "INTEGRATION_POLICY_VERSION_CONFLICT",
    "其他管理员已经修改了集成策略",
    { currentPolicySetVersion: currentVersion },
    true
  );
}

function validationError(message: string): CatalogApiError {
  return new CatalogApiError(400, "VALIDATION_ERROR", message);
}

function serviceUnavailable(): CatalogApiError {
  return new CatalogApiError(
    503,
    "SERVICE_UNAVAILABLE",
    "集成策略状态不可用"
  );
}

function policyJson(
  body: string,
  etag: string,
  requestId: string
): Response {
  return new Response(body, {
    status: 200,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store",
      etag,
      "x-request-id": requestId
    }
  });
}
