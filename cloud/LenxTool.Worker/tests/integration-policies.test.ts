import { env, exports } from "cloudflare:workers";
import { beforeEach, describe, expect, it } from "vitest";

const baseUrl = "https://worker.test";

// 端到端使用真实 workerd/D1，冻结角色、条件版本、幂等与私人字段隔离。
interface Session {
  userId: string;
  accessToken: string;
}

beforeEach(async () => {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM integration_policy_versions"),
    env.DB.prepare("DELETE FROM integration_policies"),
    env.DB.prepare(
      "UPDATE integration_policy_state SET policy_set_version=0," +
      "updated_at=?,last_mutation_id=NULL WHERE singleton_id=1"
    ).bind(new Date().toISOString()),
    env.DB.prepare("DELETE FROM integration_policy_idempotency"),
    env.DB.prepare("DELETE FROM integration_policy_mutation_guards"),
    env.DB.prepare("DELETE FROM audit_events"),
    env.DB.prepare("DELETE FROM daily_usage"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM invites"),
    env.DB.prepare("DELETE FROM auth_attempts"),
    env.DB.prepare("DELETE FROM users")
  ]);
});

describe("Worker integration policies", () => {
  it("migrates policy-only tables without private targets or credentials", async () => {
    const columns = await tableColumns("integration_policies");

    expect(columns).toEqual([
      "kind",
      "is_enabled",
      "allowed_hosts_json",
      "updated_by",
      "updated_at",
      "last_mutation_id"
    ]);
    expect(columns.join(" ")).not.toMatch(
      /token|password|credential|secret|authorization|target_url|health/iu
    );
    expect(await scalar(
      "SELECT policy_set_version AS value FROM integration_policy_state"
    )).toBe(0);
  });

  it("publishes an exact-host snapshot while users only read enabled policies", async () => {
    const admin = await seedSession("admin");
    const user = await seedSession("user");
    const response = await replace(admin, 0, "integration-policy-set-0001", [
      {
        kind: "READWISE",
        isEnabled: true,
        allowedHosts: ["API.Readwise.IO.", "reader.example.com"]
      },
      {
        kind: "ZOTERO",
        isEnabled: false,
        allowedHosts: ["api.zotero.org"]
      }
    ]);

    expect(response.status).toBe(200);
    expect(await response.clone().json()).toMatchObject({
      policySetVersion: 1,
      policies: [
        {
          kind: "ZOTERO",
          isEnabled: false,
          allowedHosts: ["api.zotero.org"]
        },
        {
          kind: "READWISE",
          isEnabled: true,
          allowedHosts: ["api.readwise.io", "reader.example.com"]
        }
      ]
    });
    const active = await read(user, "ACTIVE");
    expect(active.status).toBe(200);
    expect(active.headers.get("etag")).toBe(
      '"integration-policies-active-1"'
    );
    expect((await active.json<{ policies: unknown[] }>()).policies)
      .toHaveLength(1);
    expect((await read(user, "ALL")).status).toBe(403);
    expect((await read(admin, "ALL")).status).toBe(200);
  });

  it("authorizes writes before reading the body and defaults to no enabled integrations", async () => {
    const admin = await seedSession("admin");
    const user = await seedSession("user");
    const denied = await workerRequest("/v1/admin/integration-policies", {
      method: "PUT",
      headers: {
        authorization: `Bearer ${user.accessToken}`,
        "content-type": "application/json"
      },
      body: "{"
    });
    const empty = await replace(
      admin,
      0,
      "integration-policy-empty-0001",
      []
    );

    expect(denied.status).toBe(403);
    expect(await errorCode(denied)).toBe("ADMIN_REQUIRED");
    expect(empty.status).toBe(200);
    expect((await read(user, "ACTIVE").then(value => value.json<{
      policies: unknown[];
    }>())).policies).toEqual([]);
  });

  it("allows hostless Obsidian but keeps network integrations host-bound", async () => {
    const admin = await seedSession("admin");
    const user = await seedSession("user");
    const accepted = await replace(
      admin,
      0,
      "integration-obsidian-001",
      [
        {
          kind: "OBSIDIAN",
          isEnabled: true,
          allowedHosts: []
        },
        {
          kind: "WEBHOOK",
          isEnabled: false,
          allowedHosts: []
        }
      ]
    );

    expect(accepted.status).toBe(200);
    expect(await accepted.clone().json()).toMatchObject({
      policySetVersion: 1,
      policies: [
        {
          kind: "OBSIDIAN",
          isEnabled: true,
          allowedHosts: []
        },
        {
          kind: "WEBHOOK",
          isEnabled: false,
          allowedHosts: []
        }
      ]
    });
    expect(await read(user, "ACTIVE").then(response => response.json<{
      policies: unknown[];
    }>())).toMatchObject({
      policies: [{
        kind: "OBSIDIAN",
        isEnabled: true,
        allowedHosts: []
      }]
    });

    const rejected = await replace(
      admin,
      1,
      "integration-hostless-webhook",
      [{
        kind: "WEBHOOK",
        isEnabled: true,
        allowedHosts: []
      }]
    );
    expect(rejected.status).toBe(400);
    expect(await errorCode(rejected)).toBe("VALIDATION_ERROR");
  });

  it("rejects ambiguous targets and preserves version/idempotency semantics", async () => {
    const admin = await seedSession("admin");
    const invalidHosts = [
      "*.example.com",
      "https://example.com",
      "example.com:443",
      "127.0.0.1",
      "localhost",
      "service.local"
    ];
    for (const [index, host] of invalidHosts.entries()) {
      const response = await replace(
        admin,
        0,
        `integration-invalid-${index}`.padEnd(20, "0"),
        [{ kind: "WEBHOOK", isEnabled: true, allowedHosts: [host] }]
      );
      expect(response.status).toBe(400);
      expect(await errorCode(response)).toBe("VALIDATION_ERROR");
    }

    const accepted = await replace(
      admin,
      0,
      "integration-policy-replay-1",
      [{
        kind: "WEBHOOK",
        isEnabled: true,
        allowedHosts: ["hooks.example.com"]
      }]
    );
    const replay = await replace(
      admin,
      0,
      "integration-policy-replay-1",
      [{
        kind: "WEBHOOK",
        isEnabled: true,
        allowedHosts: ["hooks.example.com"]
      }]
    );
    const stale = await replace(
      admin,
      0,
      "integration-policy-stale-01",
      [{
        kind: "WEBHOOK",
        isEnabled: true,
        allowedHosts: ["other.example.com"]
      }]
    );

    expect(accepted.status).toBe(200);
    expect(await replay.text()).toBe(await accepted.clone().text());
    expect(stale.status).toBe(409);
    expect(await errorCode(stale)).toBe(
      "INTEGRATION_POLICY_VERSION_CONFLICT"
    );
    expect(await scalar(
      "SELECT COUNT(*) AS value FROM integration_policy_versions"
    )).toBe(1);
    expect(await scalar(
      "SELECT COUNT(*) AS value FROM audit_events " +
      "WHERE target_type='integration_policy_set'"
    )).toBe(1);
  });
});

function replace(
  session: Session,
  version: number,
  key: string,
  policies: unknown[]
): Promise<Response> {
  return workerRequest("/v1/admin/integration-policies", {
    method: "PUT",
    headers: {
      authorization: `Bearer ${session.accessToken}`,
      "content-type": "application/json",
      "if-match": `"integration-policies-all-${version}"`,
      "idempotency-key": key
    },
    body: JSON.stringify({ policies })
  });
}

function read(
  session: Session,
  scope: "ACTIVE" | "ALL"
): Promise<Response> {
  return workerRequest(`/v1/integration-policies?scope=${scope}`, {
    headers: { authorization: `Bearer ${session.accessToken}` }
  });
}

function workerRequest(path: string, init?: RequestInit): Promise<Response> {
  return exports.default.fetch(new Request(`${baseUrl}${path}`, init));
}

async function errorCode(response: Response): Promise<string> {
  return (await response.clone().json<{
    error: { code: string };
  }>()).error.code;
}

async function scalar(query: string): Promise<number> {
  const row = await env.DB.prepare(query).first<{ value: number }>();
  if (!row) throw new Error(`Missing scalar: ${query}`);
  return row.value;
}

async function tableColumns(table: string): Promise<string[]> {
  const result = await env.DB.prepare(
    `PRAGMA table_info(${table})`
  ).all<{ name: string }>();
  return result.results.map(row => row.name);
}

async function seedSession(role: "user" | "admin"): Promise<Session> {
  const userId = crypto.randomUUID();
  const createdAt = new Date().toISOString();
  await env.DB.prepare(
    "INSERT INTO users(id,username,username_norm,password_salt,password_hash,role," +
    "ai_daily_limit,speech_daily_seconds,created_at,updated_at) " +
    "VALUES(?,?,?,?,?,?,?,?,?,?)"
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

async function signAccessToken(
  claims: Record<string, unknown>
): Promise<string> {
  const encoder = new TextEncoder();
  const header = toBase64Url(
    encoder.encode(JSON.stringify({ alg: "HS256", typ: "JWT" }))
  );
  const payload = toBase64Url(
    encoder.encode(JSON.stringify(claims))
  );
  const key = await crypto.subtle.importKey(
    "raw",
    encoder.encode(env.TOKEN_SECRET),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"]
  );
  const signature = new Uint8Array(
    await crypto.subtle.sign(
      "HMAC",
      key,
      encoder.encode(`${header}.${payload}`)
    )
  );
  return `${header}.${payload}.${toBase64Url(signature)}`;
}

function toBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary)
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replace(/=+$/u, "");
}
