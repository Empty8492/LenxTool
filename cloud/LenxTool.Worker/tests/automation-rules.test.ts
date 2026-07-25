import { env, exports } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";

const baseUrl = "https://worker.test";
const now = "2026-07-25T10:00:00.000Z";

interface Session {
  userId: string;
  accessToken: string;
}

interface Rule {
  id: string;
  version: number;
  name: string;
  priority: number;
  conflictOrder: number;
  isEnabled: boolean;
  matchMode: "ALL" | "ANY";
  conditions: Array<{ field: string; operator: string; value: string | null }>;
  actions: Array<{ type: string; order: number; value: string | null }>;
}

interface RuleMutation {
  ruleSetVersion: number;
  rule: Rule;
}

beforeEach(async () => {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM automation_rule_versions"),
    env.DB.prepare("DELETE FROM automation_rules"),
    env.DB.prepare(
      "UPDATE automation_rule_state SET rule_set_version=0,updated_at=?,last_mutation_id=NULL " +
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

describe("Worker v1 restricted automation rules", () => {
  it("migrates versioned rule tables with bounded database constraints", async () => {
    const admin = await seedSession("admin");
    const state = await env.DB.prepare(
      "SELECT singleton_id,rule_set_version,last_mutation_id FROM automation_rule_state"
    ).first<Record<string, unknown>>();
    const currentColumns = await tableColumns("automation_rules");
    const versionColumns = await tableColumns("automation_rule_versions");

    expect(state).toEqual({
      singleton_id: 1,
      rule_set_version: 0,
      last_mutation_id: null
    });
    expect(currentColumns).toEqual([
      "id", "current_version", "name", "priority", "conflict_order", "is_enabled",
      "match_mode", "conditions_json", "actions_json", "created_by", "updated_by",
      "created_at", "updated_at", "last_mutation_id"
    ]);
    expect(versionColumns).toEqual([
      "rule_id", "version", "snapshot_json", "published_by", "published_at"
    ]);
    await expect(env.DB.prepare(
      "INSERT INTO automation_rules(id,current_version,name,priority,conflict_order,is_enabled," +
      "match_mode,conditions_json,actions_json,created_by,updated_by,created_at,updated_at,last_mutation_id) " +
      "VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)"
    ).bind(
      crypto.randomUUID(),
      1,
      "invalid priority",
      1001,
      0,
      1,
      "ALL",
      "[]",
      "[]",
      admin.userId,
      admin.userId,
      now,
      now,
      crypto.randomUUID()
    ).run()).rejects.toThrow();
    await expect(env.DB.prepare(
      "INSERT INTO automation_rules(id,current_version,name,priority,conflict_order,is_enabled," +
      "match_mode,conditions_json,actions_json,created_by,updated_by,created_at,updated_at,last_mutation_id) " +
      "VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)"
    ).bind(
      crypto.randomUUID(),
      1,
      "invalid json",
      0,
      0,
      1,
      "ALL",
      "not-json",
      "[]",
      admin.userId,
      admin.userId,
      now,
      now,
      crypto.randomUUID()
    ).run()).rejects.toThrow();
  });

  it("publishes normalized versions and exposes only enabled rules to ordinary users", async () => {
    const admin = await seedSession("admin");
    const user = await seedSession("user");
    const input = validRuleInput();

    const first = await publishRule(admin, 0, "automation-create-0001", input, "rule-create-request");
    const firstText = await first.text();
    const created = JSON.parse(firstText) as RuleMutation;
    const replay = await publishRule(admin, 0, "automation-create-0001", input, "rule-replay-request");

    expect(first.status).toBe(201);
    expect(replay.status).toBe(201);
    expect(await replay.text()).toBe(firstText);
    expect(created).toMatchObject({
      ruleSetVersion: 1,
      rule: {
        id: expect.any(String),
        version: 1,
        name: "AI release digest",
        priority: 200,
        conflictOrder: 10,
        isEnabled: true,
        matchMode: "ALL"
      }
    });
    expect(created.rule.conditions).toEqual([
      { field: "TITLE", operator: "CONTAINS", value: "release notes" },
      { field: "PUBLISHED_AT", operator: "AFTER", value: "2026-07-01T00:00:00.000Z" },
      { field: "HAS_VIDEO", operator: "EQUALS", value: "true" }
    ]);
    expect(created.rule.actions).toEqual([
      { type: "ADD_TAG", order: 10, value: "AI" },
      { type: "GENERATE_SUMMARY", order: 20, value: null },
      { type: "NOTIFY", order: 30, value: null }
    ]);

    const active = await readRules(user, "ACTIVE");
    const activeBody = await active.json<{
      ruleSetVersion: number;
      scope: string;
      limits: Record<string, number>;
      rules: Rule[];
    }>();
    expect(active.status).toBe(200);
    expect(active.headers.get("etag")).toBe('"automation-active-1"');
    expect(activeBody).toMatchObject({
      ruleSetVersion: 1,
      scope: "ACTIVE",
      limits: {
        maximumConditions: 16,
        maximumActions: 8,
        maximumRegexLength: 256,
        regexTimeoutMilliseconds: 100
      },
      rules: [{ id: created.rule.id, version: 1, isEnabled: true }]
    });

    const disabled = await patchRule(
      admin,
      created.rule.id,
      1,
      "automation-disable-0001",
      { ...input, isEnabled: false },
      "rule-disable-request"
    );
    const disabledBody = await disabled.json<RuleMutation>();
    expect(disabled.status).toBe(200);
    expect(disabledBody).toMatchObject({
      ruleSetVersion: 2,
      rule: { id: created.rule.id, version: 2, isEnabled: false }
    });

    const userAfterDisable = await readRules(user, "ACTIVE");
    expect((await userAfterDisable.json<{ rules: Rule[] }>()).rules).toEqual([]);
    const adminAll = await readRules(admin, "ALL");
    expect((await adminAll.json<{ rules: Rule[] }>()).rules).toMatchObject([
      { id: created.rule.id, version: 2, isEnabled: false }
    ]);
    expect(await scalar(
      "SELECT COUNT(*) AS value FROM automation_rule_versions WHERE rule_id=?",
      created.rule.id
    )).toBe(2);
    expect(await scalar(
      "SELECT COUNT(*) AS value FROM audit_events WHERE target_type='automation_rule' AND target_id=?",
      created.rule.id
    )).toBe(2);
    const audits = await env.DB.prepare(
      "SELECT action,request_id FROM audit_events WHERE target_id=? ORDER BY created_at"
    ).bind(created.rule.id).all<Record<string, unknown>>();
    expect(audits.results).toEqual([
      { action: "automation_rule.created", request_id: "rule-create-request" },
      { action: "automation_rule.updated", request_id: "rule-disable-request" }
    ]);
    expect(JSON.stringify(audits.results)).not.toContain("release notes");
  });

  it("enforces authentication, administrator writes, optimistic versions, and idempotency", async () => {
    const admin = await seedSession("admin");
    const user = await seedSession("user");
    const input = validRuleInput();

    const anonymous = await workerRequest("/v1/admin/automation-rules", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(input)
    });
    const denied = await publishRule(user, 0, "automation-user-denied", input);
    const accepted = await publishRule(admin, 0, "automation-version-create", input);
    const created = await accepted.clone().json<RuleMutation>();
    const stale = await patchRule(
      admin,
      created.rule.id,
      0,
      "automation-stale-patch",
      { ...input, name: "stale" }
    );
    const reused = await publishRule(
      admin,
      0,
      "automation-version-create",
      { ...input, name: "different request" }
    );
    const userAll = await readRules(user, "ALL");

    expect(anonymous.status).toBe(401);
    expect(denied.status).toBe(403);
    await expect(errorCode(denied)).resolves.toBe("ADMIN_REQUIRED");
    expect(accepted.status).toBe(201);
    expect(stale.status).toBe(409);
    await expect(errorCode(stale)).resolves.toBe("AUTOMATION_VERSION_CONFLICT");
    expect(reused.status).toBe(409);
    await expect(errorCode(reused)).resolves.toBe("IDEMPOTENCY_KEY_REUSED");
    expect(userAll.status).toBe(403);
    expect(await scalar("SELECT rule_set_version AS value FROM automation_rule_state")).toBe(1);
    expect(await scalar("SELECT COUNT(*) AS value FROM automation_rules")).toBe(1);
  });

  it("rejects unknown fields, invalid combinations, oversized collections, and action payload injection", async () => {
    const admin = await seedSession("admin");
    const base = validRuleInput();
    const invalidInputs: Array<Record<string, unknown>> = [
      { ...base, script: "return fetch('https://example.com')" },
      {
        ...base,
        conditions: [{ field: "TITLE", operator: "BEFORE", value: "2026-07-01T00:00:00Z" }]
      },
      {
        ...base,
        conditions: [{ field: "AUTHOR", operator: "EXISTS", value: "unexpected" }]
      },
      {
        ...base,
        conditions: [{ field: "FEED", operator: "EQUALS", value: "not-a-guid" }]
      },
      {
        ...base,
        conditions: [{ field: "PUBLISHED_AT", operator: "AFTER", value: "2026-07-01T08:00:00" }]
      },
      {
        ...base,
        conditions: [{ field: "PUBLISHED_AT", operator: "AFTER", value: "0000-01-01T00:00:00Z" }]
      },
      {
        ...base,
        conditions: Array.from(
          { length: 17 },
          (_, index) => ({ field: "TITLE", operator: "CONTAINS", value: `value-${index}` })
        )
      },
      {
        ...base,
        actions: [{ type: "NOTIFY", order: 0, value: "https://example.com/hook" }]
      },
      {
        ...base,
        actions: [
          { type: "TRANSLATE", order: 0, value: "en" },
          { type: "TRANSLATE", order: 1, value: "ja" }
        ]
      },
      {
        ...base,
        actions: [
          { type: "HIDE", order: 1 },
          { type: "MARK_READ", order: 1 }
        ]
      }
    ];

    for (const [index, input] of invalidInputs.entries()) {
      const response = await publishRule(admin, 0, `automation-invalid-${index}`, input);
      expect(response.status, `invalid input ${index}`).toBe(400);
      await expect(errorCode(response)).resolves.toBe("VALIDATION_ERROR");
    }
    expect(await scalar("SELECT rule_set_version AS value FROM automation_rule_state")).toBe(0);
    expect(await scalar("SELECT COUNT(*) AS value FROM automation_rules")).toBe(0);
    expect(await scalar("SELECT COUNT(*) AS value FROM audit_events")).toBe(0);
  });

  it("accepts a traditionally catastrophic regex without executing it and rejects non-portable regex", async () => {
    const admin = await seedSession("admin");
    const catastrophic = validRuleInput({
      conditions: [{ field: "TITLE", operator: "REGEX", value: "(a+)+$" }]
    });
    const accepted = await publishRule(admin, 0, "automation-regex-safe", catastrophic);
    const created = await accepted.clone().json<RuleMutation>();

    expect(accepted.status).toBe(201);
    expect(created.rule.conditions[0]).toEqual({
      field: "TITLE",
      operator: "REGEX",
      value: "(a+)+$"
    });

    const unsafePatterns = [
      "[",
      String.raw`(a+)\1`,
      "(?=a)a",
      String.raw`\u{1F600}`,
      "a".repeat(257)
    ];
    for (const [index, pattern] of unsafePatterns.entries()) {
      const response = await patchRule(
        admin,
        created.rule.id,
        1,
        `automation-regex-invalid-${index}`,
        validRuleInput({
          conditions: [{ field: "TITLE", operator: "REGEX", value: pattern }]
        })
      );
      expect(response.status, `unsafe regex ${index}`).toBe(400);
    }
    expect(await scalar("SELECT rule_set_version AS value FROM automation_rule_state")).toBe(1);
    expect(await scalar(
      "SELECT COUNT(*) AS value FROM automation_rule_versions WHERE rule_id=?",
      created.rule.id
    )).toBe(1);
  });

  it("supports version cache validation without leaking disabled rules or content data", async () => {
    const admin = await seedSession("admin");
    const user = await seedSession("user");
    const published = await publishRule(admin, 0, "automation-cache-create", validRuleInput());
    expect(published.status).toBe(201);

    const current = await workerRequest("/v1/automation-rules?scope=ACTIVE&afterVersion=1", {
      headers: {
        authorization: `Bearer ${user.accessToken}`,
        "x-request-id": "automation-not-modified"
      }
    });
    const ahead = await workerRequest("/v1/automation-rules?scope=ACTIVE&afterVersion=2", {
      headers: { authorization: `Bearer ${user.accessToken}` }
    });

    expect(current.status).toBe(304);
    expect(current.headers.get("etag")).toBe('"automation-active-1"');
    expect(current.headers.get("x-request-id")).toBe("automation-not-modified");
    expect(await current.text()).toBe("");
    expect(ahead.status).toBe(409);
    await expect(errorCode(ahead)).resolves.toBe("AUTOMATION_VERSION_AHEAD");

    const stored = await env.DB.prepare(
      "SELECT snapshot_json FROM automation_rule_versions"
    ).first<{ snapshot_json: string }>();
    expect(stored?.snapshot_json).not.toMatch(/articleContent|summaryText|translationText/iu);
  });
});

function validRuleInput(
  overrides: Record<string, unknown> = {}
): Record<string, unknown> {
  return {
    name: "  AI release digest  ",
    priority: 200,
    conflictOrder: 10,
    isEnabled: true,
    matchMode: "ALL",
    conditions: [
      { field: "TITLE", operator: "CONTAINS", value: "  release notes  " },
      { field: "PUBLISHED_AT", operator: "AFTER", value: "2026-07-01T08:00:00+08:00" },
      { field: "HAS_VIDEO", operator: "EQUALS", value: "TRUE" }
    ],
    actions: [
      { type: "NOTIFY", order: 30 },
      { type: "ADD_TAG", order: 10, value: "  AI  " },
      { type: "GENERATE_SUMMARY", order: 20 }
    ],
    ...overrides
  };
}

function publishRule(
  session: Session,
  version: number,
  idempotencyKey: string,
  body: Record<string, unknown>,
  requestId?: string
): Promise<Response> {
  return mutationRequest(
    "/v1/admin/automation-rules",
    "POST",
    session,
    version,
    idempotencyKey,
    body,
    requestId
  );
}

function patchRule(
  session: Session,
  ruleId: string,
  version: number,
  idempotencyKey: string,
  body: Record<string, unknown>,
  requestId?: string
): Promise<Response> {
  return mutationRequest(
    `/v1/admin/automation-rules/${ruleId}`,
    "PATCH",
    session,
    version,
    idempotencyKey,
    body,
    requestId
  );
}

function mutationRequest(
  path: string,
  method: "POST" | "PATCH",
  session: Session,
  version: number,
  idempotencyKey: string,
  body: Record<string, unknown>,
  requestId?: string
): Promise<Response> {
  const headers = new Headers({
    authorization: `Bearer ${session.accessToken}`,
    "content-type": "application/json",
    "if-match": `"automation-all-${version}"`,
    "idempotency-key": idempotencyKey
  });
  if (requestId) headers.set("x-request-id", requestId);
  return workerRequest(path, { method, headers, body: JSON.stringify(body) });
}

function readRules(session: Session, scope: "ACTIVE" | "ALL"): Promise<Response> {
  return workerRequest(`/v1/automation-rules?scope=${scope}`, {
    headers: { authorization: `Bearer ${session.accessToken}` }
  });
}

function workerRequest(path: string, init?: RequestInit): Promise<Response> {
  return exports.default.fetch(new Request(`${baseUrl}${path}`, init));
}

async function errorCode(response: Response): Promise<string> {
  const body = await response.clone().json<{ error: { code: string } }>();
  return body.error.code;
}

async function scalar(query: string, ...parameters: unknown[]): Promise<number> {
  const row = await env.DB.prepare(query).bind(...parameters).first<{ value: number }>();
  if (!row) throw new Error(`Expected scalar query result: ${query}`);
  return row.value;
}

async function tableColumns(table: string): Promise<string[]> {
  const result = await env.DB.prepare(`PRAGMA table_info(${table})`).all<{ name: string }>();
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
