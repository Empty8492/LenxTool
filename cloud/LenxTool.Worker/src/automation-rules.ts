import {
  CatalogApiError,
  CatalogAuthContext,
  findIdempotency,
  jsonText,
  nowIso,
  readJson,
  replayOrReject,
  requireUuid,
  sha256
} from "./catalog";
import {
  AutomationRuleDefinition,
  AutomationRuleSnapshot,
  automationRuleLimits,
  canonicalJson,
  validateAndNormalizeAutomationRule
} from "./automation-rule-validation";

interface AutomationStateRow {
  rule_set_version: number;
  updated_at: string;
}

interface AutomationRuleRow {
  id: string;
  current_version: number;
  name: string;
  priority: number;
  conflict_order: number;
  is_enabled: number;
  match_mode: "ALL" | "ANY";
  conditions_json: string;
  actions_json: string;
  created_at: string;
  updated_at: string;
}

interface PreparedAutomationMutation {
  actorUserId: string;
  method: "POST" | "PATCH";
  path: string;
  key: string;
  requestHash: string;
  expectedRuleSetVersion: number;
  newRuleSetVersion: number;
  requestId: string;
}

interface CommitAutomationMutation {
  mutation: PreparedAutomationMutation;
  request: Request;
  status: 200 | 201;
  responseBody: string;
  snapshot: AutomationRuleSnapshot;
  action: "automation_rule.created" | "automation_rule.updated";
  businessStatement: (mutationId: string, now: string) => D1PreparedStatement;
}

const encoder = new TextEncoder();
const idempotencyKeyPattern = /^[A-Za-z0-9._:-]{16,128}$/u;
const maximumResponseBytes = 4 * 1024 * 1024;

export async function handleAutomationRuleRequest(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  url: URL
): Promise<Response | null> {
  const ruleMatch = /^\/v1\/admin\/automation-rules\/([^/]+)$/u.exec(url.pathname);
  const isReadRoute = url.pathname === "/v1/automation-rules";
  const isAdminCollection = url.pathname === "/v1/admin/automation-rules";
  if (!isReadRoute && !isAdminCollection && ruleMatch === null) return null;

  if (isReadRoute && request.method === "GET") {
    return readAutomationRules(request, db, auth, url);
  }
  if (auth.role !== "admin") {
    throw new CatalogApiError(403, "ADMIN_REQUIRED", "需要管理员权限");
  }
  if (url.search !== "") {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "规则写入不接受查询参数");
  }
  if (isAdminCollection && request.method === "POST") {
    return createAutomationRule(request, db, auth, url.pathname);
  }
  if (ruleMatch !== null && request.method === "PATCH") {
    const ruleId = requireUuid(ruleMatch[1], "规则 ID");
    return updateAutomationRule(
      request,
      db,
      auth,
      `/v1/admin/automation-rules/${ruleId}`,
      ruleId
    );
  }
  throw new CatalogApiError(404, "RESOURCE_NOT_FOUND", "接口不存在");
}

async function readAutomationRules(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  url: URL
): Promise<Response> {
  if (encoder.encode(request.url).byteLength > 2048) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "规则查询地址过长");
  }
  const allowedParameters = new Set(["scope", "afterVersion"]);
  for (const key of url.searchParams.keys()) {
    if (!allowedParameters.has(key) || url.searchParams.getAll(key).length !== 1) {
      throw new CatalogApiError(400, "VALIDATION_ERROR", "规则查询参数无效");
    }
  }
  const scope = url.searchParams.get("scope") ?? "ACTIVE";
  if (scope !== "ACTIVE" && scope !== "ALL") {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "规则查询范围无效");
  }
  if (scope === "ALL" && auth.role !== "admin") {
    throw new CatalogApiError(403, "ADMIN_REQUIRED", "需要管理员权限");
  }
  const afterVersion = parseAfterVersion(url.searchParams.get("afterVersion"));
  const activeOnly = scope === "ACTIVE";
  const results = await db.batch<AutomationStateRow | AutomationRuleRow>([
    db.prepare(
      "SELECT rule_set_version,updated_at FROM automation_rule_state WHERE singleton_id=1"
    ),
    db.prepare(
      "SELECT id,current_version,name,priority,conflict_order,is_enabled,match_mode," +
      "conditions_json,actions_json,created_at,updated_at FROM automation_rules" +
      (activeOnly ? " WHERE is_enabled=1" : "") +
      ` ORDER BY priority DESC,conflict_order,id LIMIT ${automationRuleLimits.maximumRules + 1}`
    )
  ]);
  const state = results[0]?.results[0] as AutomationStateRow | undefined;
  if (!state ||
      !Number.isSafeInteger(state.rule_set_version) ||
      state.rule_set_version < 0) {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "自动化规则状态不可用");
  }
  const etag = `"automation-${scope.toLowerCase()}-${state.rule_set_version}"`;
  const etagVersion = parseEtagVersion(request.headers.get("if-none-match"), scope);
  const clientVersion = afterVersion ?? etagVersion;
  if (clientVersion !== undefined && clientVersion > state.rule_set_version) {
    throw new CatalogApiError(
      409,
      "AUTOMATION_VERSION_AHEAD",
      "客户端规则版本高于服务端版本",
      { currentRuleSetVersion: state.rule_set_version },
      true
    );
  }
  if (clientVersion === state.rule_set_version) {
    return emptyNotModified(etag, auth.requestId);
  }

  const rows = (results[1]?.results ?? []) as AutomationRuleRow[];
  if (rows.length > automationRuleLimits.maximumRules) {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "自动化规则数量超过发布上限");
  }
  const rules = rows.map(toSnapshot);
  const body = JSON.stringify({
    ruleSetVersion: state.rule_set_version,
    scope,
    generatedAt: state.updated_at,
    limits: automationRuleLimits,
    rules
  });
  if (encoder.encode(body).byteLength > maximumResponseBytes) {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "自动化规则快照超过发布上限");
  }
  return automationJson(body, etag, auth.requestId);
}

async function createAutomationRule(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string
): Promise<Response> {
  const body = await readJson(request, 65_536);
  const prepared = await prepareAutomationMutation(request, db, auth, path, body);
  if (prepared instanceof Response) return prepared;
  const definition = validateAndNormalizeAutomationRule(body);
  const count = await db.prepare(
    "SELECT COUNT(*) AS count FROM automation_rules"
  ).first<{ count: number }>();
  if ((count?.count ?? automationRuleLimits.maximumRules) >= automationRuleLimits.maximumRules) {
    throw new CatalogApiError(409, "AUTOMATION_RULE_LIMIT_REACHED", "自动化规则数量已达到上限");
  }
  const now = nowIso();
  const snapshot: AutomationRuleSnapshot = {
    id: crypto.randomUUID(),
    version: 1,
    ...definition
  };
  const responseBody = JSON.stringify({
    ruleSetVersion: prepared.newRuleSetVersion,
    rule: snapshot
  });
  return commitAutomationMutation(db, {
    mutation: prepared,
    request,
    status: 201,
    responseBody,
    snapshot,
    action: "automation_rule.created",
    businessStatement: (mutationId, committedAt) => db.prepare(
      "INSERT INTO automation_rules(id,current_version,name,priority,conflict_order,is_enabled," +
      "match_mode,conditions_json,actions_json,created_by,updated_by,created_at,updated_at,last_mutation_id) " +
      "SELECT ?,?,?,?,?,?,?,?,?,?,?,?,?,? WHERE EXISTS (" +
      "SELECT 1 FROM automation_rule_state WHERE singleton_id=1 AND last_mutation_id=?)"
    ).bind(
      snapshot.id,
      snapshot.version,
      snapshot.name,
      snapshot.priority,
      snapshot.conflictOrder,
      snapshot.isEnabled ? 1 : 0,
      snapshot.matchMode,
      JSON.stringify(snapshot.conditions),
      JSON.stringify(snapshot.actions),
      auth.userId,
      auth.userId,
      now,
      committedAt,
      mutationId,
      mutationId
    )
  });
}

async function updateAutomationRule(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string,
  ruleId: string
): Promise<Response> {
  const body = await readJson(request, 65_536);
  const prepared = await prepareAutomationMutation(request, db, auth, path, body);
  if (prepared instanceof Response) return prepared;
  const definition = validateAndNormalizeAutomationRule(body);
  const current = await getAutomationRule(db, ruleId);
  const snapshot: AutomationRuleSnapshot = {
    id: ruleId,
    version: current.current_version + 1,
    ...definition
  };
  const responseBody = JSON.stringify({
    ruleSetVersion: prepared.newRuleSetVersion,
    rule: snapshot
  });
  return commitAutomationMutation(db, {
    mutation: prepared,
    request,
    status: 200,
    responseBody,
    snapshot,
    action: "automation_rule.updated",
    businessStatement: (mutationId, committedAt) => db.prepare(
      "UPDATE automation_rules SET current_version=?,name=?,priority=?,conflict_order=?,is_enabled=?," +
      "match_mode=?,conditions_json=?,actions_json=?,updated_by=?,updated_at=?,last_mutation_id=? " +
      "WHERE id=? AND current_version=? AND EXISTS (" +
      "SELECT 1 FROM automation_rule_state WHERE singleton_id=1 AND last_mutation_id=?)"
    ).bind(
      snapshot.version,
      snapshot.name,
      snapshot.priority,
      snapshot.conflictOrder,
      snapshot.isEnabled ? 1 : 0,
      snapshot.matchMode,
      JSON.stringify(snapshot.conditions),
      JSON.stringify(snapshot.actions),
      auth.userId,
      committedAt,
      mutationId,
      ruleId,
      current.current_version,
      mutationId
    )
  });
}

async function prepareAutomationMutation(
  request: Request,
  db: D1Database,
  auth: CatalogAuthContext,
  path: string,
  body: unknown
): Promise<PreparedAutomationMutation | Response> {
  if (request.method !== "POST" && request.method !== "PATCH") {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "规则写入方法无效");
  }
  const key = request.headers.get("idempotency-key") ?? "";
  if (!idempotencyKeyPattern.test(key)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "Idempotency-Key 格式无效");
  }
  const ifMatch = request.headers.get("if-match") ?? "";
  const match = /^"automation-all-(0|[1-9][0-9]*)"$/u.exec(ifMatch);
  if (!match) throw new CatalogApiError(400, "VALIDATION_ERROR", "If-Match 格式无效");
  const expectedRuleSetVersion = Number(match[1]);
  if (!Number.isSafeInteger(expectedRuleSetVersion)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "规则版本超出支持范围");
  }
  const requestHash = await sha256(
    `${request.method}\n${path}\n${ifMatch}\n${canonicalJson(body)}`
  );
  const currentTime = nowIso();
  await db.prepare(
    "DELETE FROM catalog_idempotency WHERE actor_user_id=? AND http_method=? AND normalized_path=? " +
    "AND idempotency_key=? AND expires_at<=?"
  ).bind(auth.userId, request.method, path, key, currentTime).run();
  const stored = await findIdempotency(
    db,
    auth.userId,
    request.method,
    path,
    key,
    currentTime
  );
  if (stored) return replayOrReject(stored, requestHash, auth.requestId);

  const currentRuleSetVersion = await getRuleSetVersion(db);
  if (currentRuleSetVersion !== expectedRuleSetVersion) {
    throw automationVersionConflict(currentRuleSetVersion);
  }
  if (currentRuleSetVersion >= Number.MAX_SAFE_INTEGER) {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "规则版本已达到服务上限");
  }
  return {
    actorUserId: auth.userId,
    method: request.method,
    path,
    key,
    requestHash,
    expectedRuleSetVersion,
    newRuleSetVersion: currentRuleSetVersion + 1,
    requestId: auth.requestId
  };
}

async function commitAutomationMutation(
  db: D1Database,
  spec: CommitAutomationMutation
): Promise<Response> {
  const mutationId = crypto.randomUUID();
  const committedAt = nowIso();
  const expiresAt = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
  const ip = spec.request.headers.get("cf-connecting-ip");
  const ipHash = ip ? await sha256(ip) : null;
  const succeeded =
    "EXISTS (SELECT 1 FROM automation_rules WHERE id=? AND current_version=? AND last_mutation_id=?)";
  const versionStatement = db.prepare(
    "INSERT INTO automation_rule_versions(rule_id,version,snapshot_json,published_by,published_at) " +
    `SELECT ?,?,?,?,? WHERE ${succeeded}`
  ).bind(
    spec.snapshot.id,
    spec.snapshot.version,
    JSON.stringify(spec.snapshot),
    spec.mutation.actorUserId,
    committedAt,
    spec.snapshot.id,
    spec.snapshot.version,
    mutationId
  );
  const auditStatement = db.prepare(
    "INSERT INTO audit_events(id,actor_user_id,target_type,target_id,action,request_id,ip_hash,created_at) " +
    `SELECT ?,?,?,?,?,?,?,? WHERE ${succeeded}`
  ).bind(
    crypto.randomUUID(),
    spec.mutation.actorUserId,
    "automation_rule",
    spec.snapshot.id,
    spec.action,
    spec.mutation.requestId,
    ipHash,
    committedAt,
    spec.snapshot.id,
    spec.snapshot.version,
    mutationId
  );
  const idempotencyStatement = db.prepare(
    "INSERT INTO catalog_idempotency(actor_user_id,http_method,normalized_path,idempotency_key," +
    "request_hash,status_code,response_body,created_at,expires_at) " +
    `SELECT ?,?,?,?,?,?,?,?,? WHERE ${succeeded}`
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
    spec.snapshot.id,
    spec.snapshot.version,
    mutationId
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
        "UPDATE automation_rule_state SET rule_set_version=?,updated_at=?,last_mutation_id=? " +
        "WHERE singleton_id=1 AND rule_set_version=?"
      ).bind(
        spec.mutation.newRuleSetVersion,
        committedAt,
        mutationId,
        spec.mutation.expectedRuleSetVersion
      ),
      spec.businessStatement(mutationId, committedAt),
      versionStatement,
      auditStatement,
      idempotencyStatement,
      guardStatement,
      db.prepare("DELETE FROM catalog_mutation_guards WHERE mutation_id=?").bind(mutationId)
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
      return replayOrReject(stored, spec.mutation.requestHash, spec.mutation.requestId);
    }
    const currentVersion = await getRuleSetVersion(db);
    if (currentVersion !== spec.mutation.expectedRuleSetVersion) {
      throw automationVersionConflict(currentVersion);
    }
    throw new CatalogApiError(500, "INTERNAL_ERROR", "自动化规则发布失败，请稍后重试");
  }
  return jsonText(spec.responseBody, spec.status, spec.mutation.requestId);
}

async function getAutomationRule(db: D1Database, ruleId: string): Promise<AutomationRuleRow> {
  const row = await db.prepare(
    "SELECT id,current_version,name,priority,conflict_order,is_enabled,match_mode,conditions_json," +
    "actions_json,created_at,updated_at FROM automation_rules WHERE id=?"
  ).bind(ruleId).first<AutomationRuleRow>();
  if (!row) throw new CatalogApiError(404, "RESOURCE_NOT_FOUND", "自动化规则不存在");
  return row;
}

async function getRuleSetVersion(db: D1Database): Promise<number> {
  const state = await db.prepare(
    "SELECT rule_set_version FROM automation_rule_state WHERE singleton_id=1"
  ).first<{ rule_set_version: number }>();
  if (!state ||
      !Number.isSafeInteger(state.rule_set_version) ||
      state.rule_set_version < 0) {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "自动化规则状态不可用");
  }
  return state.rule_set_version;
}

function toSnapshot(row: AutomationRuleRow): AutomationRuleSnapshot {
  try {
    if (!Number.isSafeInteger(row.current_version) || row.current_version < 1) {
      throw new Error("Invalid rule version.");
    }
    const definition = validateAndNormalizeAutomationRule({
      name: row.name,
      priority: row.priority,
      conflictOrder: row.conflict_order,
      isEnabled: row.is_enabled === 1,
      matchMode: row.match_mode,
      conditions: JSON.parse(row.conditions_json),
      actions: JSON.parse(row.actions_json)
    });
    return {
      id: row.id,
      version: row.current_version,
      ...definition
    };
  } catch {
    throw new CatalogApiError(503, "SERVICE_UNAVAILABLE", "自动化规则快照损坏");
  }
}

function parseAfterVersion(value: string | null): number | undefined {
  if (value === null) return undefined;
  if (!/^(0|[1-9][0-9]*)$/u.test(value)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "afterVersion 格式无效");
  }
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed)) {
    throw new CatalogApiError(400, "VALIDATION_ERROR", "afterVersion 超出支持范围");
  }
  return parsed;
}

function parseEtagVersion(value: string | null, scope: "ACTIVE" | "ALL"): number | undefined {
  if (value === null) return undefined;
  const match = new RegExp(`^"automation-${scope.toLowerCase()}-(0|[1-9][0-9]*)"$`, "u").exec(value);
  if (!match) return undefined;
  const parsed = Number(match[1]);
  return Number.isSafeInteger(parsed) ? parsed : undefined;
}

function automationVersionConflict(currentRuleSetVersion: number): CatalogApiError {
  return new CatalogApiError(
    409,
    "AUTOMATION_VERSION_CONFLICT",
    "其他管理员已经修改了自动化规则",
    { currentRuleSetVersion },
    true
  );
}

function automationJson(body: string, etag: string, requestId: string): Response {
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

function emptyNotModified(etag: string, requestId: string): Response {
  return new Response(null, {
    status: 304,
    headers: {
      "cache-control": "no-store, no-transform",
      etag,
      "x-request-id": requestId
    }
  });
}
