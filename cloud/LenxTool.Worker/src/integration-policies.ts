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
  trustedPrivateEndpoints: PrivateEndpoint[];
  allowedResources: string[];
  allowedLoopbackHttpPorts: number[];
}

interface PrivateEndpoint {
  host: string;
  port: number;
}

interface PolicyStateRow {
  policy_set_version: number;
  updated_at: string;
}

interface PolicyRow {
  kind: string;
  is_enabled: number;
  allowed_hosts_json: string;
  trusted_private_endpoints_json: string;
  allowed_resources_json: string;
  allowed_loopback_http_ports_json: string;
}

interface StoredPolicyMapping {
  policy: IntegrationPolicy;
  requiresCompatibilityProjection: boolean;
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
const policySchemaVersion = 2;
const policySchemaHeader = "x-lenxtool-integration-policy-schema";
const maximumJsonColumnLength = 8 * 1024;
const maximumPolicySetJsonBytes = 40 * 1024;
const reservedSuffixes = [
  ".internal",
  ".invalid",
  ".lan",
  ".local",
  ".localhost",
  ".home.arpa"
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
  const requestedSchema = parseRequestedSchema(request);
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
        "SELECT kind,is_enabled,allowed_hosts_json," +
        "trusted_private_endpoints_json,allowed_resources_json," +
        "allowed_loopback_http_ports_json " +
        "FROM integration_policies" +
        (scope === "ACTIVE" ? " WHERE is_enabled=1" : "") +
        " ORDER BY kind"
      )
    ]);
  const state = stateResult?.results[0] as PolicyStateRow | undefined;
  assertState(state);
  const etag = requestedSchema === 2
    ? `"integration-policies-v2-${scope.toLowerCase()}-${state.policy_set_version}"`
    : `"integration-policies-${scope.toLowerCase()}-${state.policy_set_version}"`;
  const conditional = afterVersion ??
    parseEtagVersion(
      request.headers.get("if-none-match"),
      scope,
      requestedSchema
    );
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
  const storedPolicies = (policyResult?.results ?? [])
    .map(row => mapStoredPolicy(row as PolicyRow));
  const requiresCompatibilityProjection = storedPolicies.some(
    value => value.requiresCompatibilityProjection
  );
  const advancedOnly = storedPolicies.some(value =>
    isAdvancedOnly(value.policy)
  );
  if (requestedSchema === 1 && scope === "ALL" && advancedOnly) {
    throw schemaUpgradeRequired();
  }
  if (conditional === state.policy_set_version
      && !requiresCompatibilityProjection) {
    return new Response(null, {
      status: 304,
      headers: {
        "cache-control": "no-store, no-transform",
        etag,
        vary: policySchemaHeader,
        "x-request-id": auth.requestId
      }
    });
  }

  const normalizedPolicies = storedPolicies
    .map(value => value.policy)
    .filter(policy => requestedSchema === 2 || !isAdvancedOnly(policy))
    .sort(comparePolicies);
  const policies = requestedSchema === 2
    ? normalizedPolicies
    : normalizedPolicies.map(projectLegacyPolicy);
  return policyJson(
    JSON.stringify({
      ...(requestedSchema === 2
        ? { policySchemaVersion }
        : {}),
      policySetVersion: state.policy_set_version,
      scope,
      generatedAt: state.updated_at,
      policies
    }),
    etag,
    auth.requestId,
    requestedSchema
  );
}

async function replacePolicies(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext
): Promise<Response> {
  const body = await readJson(request, 65_536);
  assertOnlyFields(body, ["policySchemaVersion", "policies"]);
  if (body.policySchemaVersion !== policySchemaVersion) {
    throw schemaUpgradeRequired();
  }
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
    /^"integration-policies-v2-all-(0|[1-9][0-9]*)"$/u.exec(ifMatch);
  if (!match) throw validationError("If-Match 格式无效");
  const expectedVersion = Number(match[1]);
  if (!Number.isSafeInteger(expectedVersion)) {
    throw validationError("集成策略版本超出支持范围");
  }

  const requestHash = await sha256(
    `PUT\n/v1/admin/integration-policies\n${ifMatch}\n` +
    canonicalJson({ policySchemaVersion, policies })
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
    policySchemaVersion,
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
      "kind,is_enabled,allowed_hosts_json," +
      "trusted_private_endpoints_json,allowed_resources_json," +
      "allowed_loopback_http_ports_json," +
      "updated_by,updated_at,last_mutation_id) " +
      "SELECT ?,?,?,?,?,?,?,?,? WHERE EXISTS (" +
      "SELECT 1 FROM integration_policy_state " +
      "WHERE singleton_id=1 AND last_mutation_id=?)"
    ).bind(
      policy.kind,
      policy.isEnabled ? 1 : 0,
      JSON.stringify(policy.allowedHosts),
      JSON.stringify(policy.trustedPrivateEndpoints),
      JSON.stringify(policy.allowedResources),
      JSON.stringify(policy.allowedLoopbackHttpPorts),
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
    assertOnlyFields(input, [
      "kind",
      "isEnabled",
      "allowedHosts",
      "trustedPrivateEndpoints",
      "allowedResources",
      "allowedLoopbackHttpPorts"
    ]);
    if (typeof input.kind !== "string"
        || !allowedKinds.has(input.kind as IntegrationKind)) {
      throw validationError("集成类型无效");
    }
    const kind = input.kind as IntegrationKind;
    if (!seen.add(kind)) throw validationError("集成类型不能重复");
    const allowedHosts = normalizeAllowedHosts(input.allowedHosts);
    const trustedPrivateEndpoints = normalizePrivateEndpoints(
      input.trustedPrivateEndpoints
    );
    const allowedResources = normalizeResources(
      kind,
      input.allowedResources
    );
    const allowedLoopbackHttpPorts = normalizeLoopbackPorts(
      input.allowedLoopbackHttpPorts
    );
    const isEnabled = requireBoolean(input.isEnabled, "启用状态");
    if (isLocalOnly(kind)
        && (allowedHosts.length !== 0
          || trustedPrivateEndpoints.length !== 0
          || allowedResources.length !== 0
          || allowedLoopbackHttpPorts.length !== 0)) {
      throw validationError("本机集成目标不能写入共享策略元数据");
    }
    if (!supportsPrivateEndpoints(kind)
        && trustedPrivateEndpoints.length !== 0) {
      throw validationError("该集成类型不能配置受信私网目标");
    }
    if (!supportsResources(kind) && allowedResources.length !== 0) {
      throw validationError("该集成类型不能配置资源白名单");
    }
    if (kind !== "QBITTORRENT"
        && allowedLoopbackHttpPorts.length !== 0) {
      throw validationError("只有 qBittorrent 可以配置本机 HTTP 端口");
    }
    if (isEnabled
        && !isLocalOnly(kind)
        && allowedHosts.length === 0
        && trustedPrivateEndpoints.length === 0
        && allowedLoopbackHttpPorts.length === 0) {
      throw validationError("启用集成前必须配置受控网络目标");
    }
    if (isEnabled
        && supportsResources(kind)
        && allowedResources.length === 0) {
      throw validationError("启用该集成前必须配置至少一个允许资源");
    }
    return {
      kind,
      isEnabled,
      allowedHosts,
      trustedPrivateEndpoints,
      allowedResources,
      allowedLoopbackHttpPorts
    };
  });
  const normalized = policies.sort(comparePolicies);
  if (new TextEncoder().encode(JSON.stringify(normalized)).byteLength
      > maximumPolicySetJsonBytes) {
    throw validationError("集成策略集合超过安全传输预算");
  }
  return normalized;
}

function isLocalOnly(kind: IntegrationKind): boolean {
  return kind === "OBSIDIAN" || kind === "EAGLE";
}

function supportsPrivateEndpoints(kind: IntegrationKind): boolean {
  return kind === "READECK"
    || kind === "OUTLINE"
    || kind === "QBITTORRENT"
    || kind === "WEBHOOK";
}

function supportsResources(kind: IntegrationKind): boolean {
  return kind === "OUTLINE" || kind === "QBITTORRENT";
}

function normalizeAllowedHosts(value: unknown): string[] {
  if (!Array.isArray(value) || value.length > 32) {
    throw validationError("目标主机列表无效");
  }
  const normalized = [...new Set(value.map(normalizeExactHost))].sort();
  ensureColumnBudget(normalized, "目标主机");
  return normalized;
}

function normalizePrivateEndpoints(value: unknown): PrivateEndpoint[] {
  if (!Array.isArray(value) || value.length > 32) {
    throw validationError("受信私网目标列表无效");
  }
  const values = value.map(item => {
    if (item === null || Array.isArray(item) || typeof item !== "object") {
      throw validationError("受信私网目标无效");
    }
    const endpoint = item as Record<string, unknown>;
    assertOnlyFields(endpoint, ["host", "port"]);
    return {
      host: normalizePrivateHost(endpoint.host),
      port: normalizePort(endpoint.port)
    };
  });
  const normalized = [...new Map(values.map(item => [
    `${item.host}:${item.port}`,
    item
  ])).values()].sort((left, right) =>
    (left.host < right.host ? -1 : left.host > right.host ? 1 : 0)
      || left.port - right.port
  );
  ensureColumnBudget(normalized, "受信私网目标");
  return normalized;
}

function normalizeResources(
  kind: IntegrationKind,
  value: unknown
): string[] {
  if (!Array.isArray(value) || value.length > 32) {
    throw validationError("资源白名单无效");
  }
  const values = value.map(item => {
    if (typeof item !== "string") {
      throw validationError("资源白名单项必须是字符串");
    }
    if (kind === "OUTLINE") {
      const normalized = item.trim().toLowerCase();
      if (normalized === "00000000-0000-0000-0000-000000000000"
          || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u
            .test(normalized)) {
        throw validationError("Outline collection ID 必须是非空 UUID");
      }
      return normalized;
    }
    if (kind === "QBITTORRENT") {
      const normalized = item.trim();
      if (normalized.length === 0
          || normalized.length > 128
          || /[\u0000-\u001f\u007f-\u009f]/u.test(normalized)) {
        throw validationError("qBittorrent 分类格式无效");
      }
      return normalized;
    }
    return item.trim();
  });
  const normalized = [...new Set(values)].sort();
  ensureColumnBudget(normalized, "资源白名单");
  return normalized;
}

function normalizeLoopbackPorts(value: unknown): number[] {
  if (!Array.isArray(value) || value.length > 16) {
    throw validationError("本机 HTTP 端口列表无效");
  }
  const normalized = [...new Set(value.map(normalizePort))]
    .sort((a, b) => a - b);
  ensureColumnBudget(normalized, "本机 HTTP 端口");
  return normalized;
}

function normalizePort(value: unknown): number {
  if (!Number.isInteger(value) || (value as number) < 1
      || (value as number) > 65_535) {
    throw validationError("目标端口必须位于 1 到 65535 之间");
  }
  return value as number;
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
      || host === "home.arpa"
      || isIpv4Literal(host)
      || reservedSuffixes.some(suffix => host.endsWith(suffix))
      || !/^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/u
        .test(host)) {
    throw validationError("目标必须是可公开解析的精确 DNS 主机名");
  }
  return host;
}

function ensureColumnBudget(value: unknown, label: string): void {
  if (new TextEncoder().encode(JSON.stringify(value)).byteLength
      > maximumJsonColumnLength) {
    throw validationError(`${label}超过共享策略列预算`);
  }
}

function normalizePrivateHost(value: unknown): string {
  const host = normalizeDnsSyntax(value);
  if (host === "localhost"
      || host.endsWith(".localhost")
      || host.endsWith(".local")
      || host.endsWith(".invalid")) {
    throw validationError(
      "受信私网目标不能使用 localhost、.local 或无效保留域"
    );
  }
  return host;
}

function normalizeDnsSyntax(value: unknown): string {
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
      || isIpv4Literal(host)
      || !/^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/u
        .test(host)) {
    throw validationError("目标必须是完整的精确 DNS 主机名");
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

function mapStoredPolicy(row: PolicyRow): StoredPolicyMapping {
  let hosts: unknown;
  let privateEndpoints: unknown;
  let resources: unknown;
  let loopbackPorts: unknown;
  try {
    hosts = JSON.parse(row.allowed_hosts_json);
    privateEndpoints = JSON.parse(row.trusted_private_endpoints_json);
    resources = JSON.parse(row.allowed_resources_json);
    loopbackPorts = JSON.parse(row.allowed_loopback_http_ports_json);
  } catch {
    throw serviceUnavailable();
  }
  try {
    if (row.kind === "OBSIDIAN" || row.kind === "EAGLE") {
      // 严格本机契约前，旧入口允许 Obsidian 主机且启用的 Eagle 还要求主机；
      // 读取时验证旧值后只发布空主机，让管理员可直接 PUT 完成一次性自愈。
      const allowedHosts = normalizeAllowedHosts(hosts);
      const normalizedPrivateEndpoints =
        normalizePrivateEndpoints(privateEndpoints);
      const normalizedResources = normalizeResources(
        row.kind,
        resources
      );
      const normalizedLoopbackPorts =
        normalizeLoopbackPorts(loopbackPorts);
      if (row.is_enabled !== 0 && row.is_enabled !== 1) {
        throw validationError("启用状态无效");
      }
      if (normalizedPrivateEndpoints.length !== 0
          || normalizedResources.length !== 0
          || normalizedLoopbackPorts.length !== 0) {
        throw validationError("本机集成包含不允许的扩展策略元数据");
      }
      return {
        policy: {
          kind: row.kind,
          isEnabled: row.is_enabled === 1,
          allowedHosts: [],
          trustedPrivateEndpoints: [],
          allowedResources: [],
          allowedLoopbackHttpPorts: []
        },
        requiresCompatibilityProjection: allowedHosts.length !== 0
      };
    }
    return {
      policy: normalizePolicySet([{
        kind: row.kind,
        isEnabled: row.is_enabled === 1,
        allowedHosts: hosts,
        trustedPrivateEndpoints: privateEndpoints,
        allowedResources: resources,
        allowedLoopbackHttpPorts: loopbackPorts
      }])[0]!,
      requiresCompatibilityProjection: false
    };
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

function isAdvancedOnly(policy: IntegrationPolicy): boolean {
  return policy.isEnabled
    && policy.allowedHosts.length === 0
    && (policy.trustedPrivateEndpoints.length !== 0
      || policy.allowedLoopbackHttpPorts.length !== 0);
}

function projectLegacyPolicy(policy: IntegrationPolicy): {
  kind: IntegrationKind;
  isEnabled: boolean;
  allowedHosts: string[];
} {
  return {
    kind: policy.kind,
    isEnabled: policy.isEnabled,
    allowedHosts: policy.allowedHosts
  };
}

function parseRequestedSchema(request: Request): 1 | 2 {
  const values = request.headers.get(policySchemaHeader);
  if (values === null) return 1;
  if (values === String(policySchemaVersion)) return 2;
  throw schemaUpgradeRequired();
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
  scope: "ACTIVE" | "ALL",
  schemaVersion: 1 | 2
): number | undefined {
  if (value === null) return undefined;
  const prefix = schemaVersion === 2
    ? "integration-policies-v2"
    : "integration-policies";
  const match = new RegExp(
    `^"${prefix}-${scope.toLowerCase()}-(0|[1-9][0-9]*)"$`,
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

function schemaUpgradeRequired(): CatalogApiError {
  return new CatalogApiError(
    400,
    "INTEGRATION_POLICY_SCHEMA_UPGRADE_REQUIRED",
    "集成策略管理端需要升级到 schema v2"
  );
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
  requestId: string,
  schemaVersion: 1 | 2
): Response {
  return new Response(body, {
    status: 200,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store, no-transform",
      etag,
      vary: policySchemaHeader,
      ...(schemaVersion === 2
        ? { [policySchemaHeader]: String(policySchemaVersion) }
        : {}),
      "x-request-id": requestId
    }
  });
}
