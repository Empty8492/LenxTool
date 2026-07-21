import { beforeEach, describe, expect, it } from "vitest";
import { env, exports } from "cloudflare:workers";

const baseUrl = "https://worker.test";
const testPassword = "correct horse battery staple";
const inviteCode = "invite-code-for-tests";

interface SessionResponse {
  user: { id: string; username: string; role: string };
  quota?: unknown;
  accessToken: string;
  refreshToken: string;
  expiresInSeconds?: number;
}

beforeEach(async () => {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM audit_events"),
    env.DB.prepare("DELETE FROM daily_usage"),
    env.DB.prepare("DELETE FROM refresh_tokens"),
    env.DB.prepare("DELETE FROM invites"),
    env.DB.prepare("DELETE FROM auth_attempts"),
    env.DB.prepare("DELETE FROM users")
  ]);
});

describe("Worker v1 identity routes", () => {
  it("consumes a one-use invite atomically under concurrent registration", async () => {
    const inviterId = await seedInvite();

    const responses = await Promise.all([
      registerWithInvite("reader-one"),
      registerWithInvite("reader-two")
    ]);
    const statuses = responses.map(response => response.status).sort((left, right) => left - right);

    expect(statuses[0]).toBe(201);
    expect([400, 409]).toContain(statuses[1]);
    const failure = responses.find(response => response.status !== 201);
    expect(failure).toBeDefined();
    await expect(errorCode(failure!)).resolves.toMatch(/^(INVITE_INVALID|INVITE_RACE)$/u);

    const users = await env.DB.prepare(
      "SELECT COUNT(*) AS count FROM users WHERE id<>?"
    ).bind(inviterId).first<{ count: number }>();
    const invite = await env.DB.prepare(
      "SELECT used_count FROM invites WHERE code_hash=?"
    ).bind(await sha256(inviteCode)).first<{ used_count: number }>();
    expect(users?.count).toBe(1);
    expect(invite?.used_count).toBe(1);
  });

  it("returns the current public user and today's quota from GET /v1/me", async () => {
    const session = await registerUser();
    const date = new Date().toISOString().slice(0, 10);
    await env.DB.prepare(
      "INSERT INTO daily_usage(user_id,usage_date,ai_used,ai_reserved,speech_used_seconds,speech_reserved_seconds) VALUES(?,?,?,?,?,?)"
    ).bind(session.user.id, date, 3, 2, 45, 5).run();

    const response = await workerRequest("/v1/me", {
      headers: bearer(session.accessToken)
    });
    const body = await response.json<Record<string, unknown>>();

    expect(response.status).toBe(200);
    expect(body).toMatchObject({
      user: { id: session.user.id, username: "reader", role: "USER" },
      quota: {
        date,
        ai: { limit: 20, used: 3, reserved: 2, remaining: 15 },
        speechSeconds: { limit: 900, used: 45, reserved: 5, remaining: 850 }
      }
    });
    expect(body.serverTime).toEqual(expect.any(String));
    expect(JSON.stringify(body)).not.toMatch(/password|token|salt|hash/iu);
  });

  it("revokes logout refresh tokens and keeps repeated logout idempotent", async () => {
    const session = await registerUser();

    const first = await workerRequest("/v1/auth/logout", {
      method: "POST",
      headers: bearer(session.accessToken, true),
      body: JSON.stringify({ refreshToken: session.refreshToken })
    });
    const second = await workerRequest("/v1/auth/logout", {
      method: "POST",
      headers: bearer(session.accessToken, true),
      body: JSON.stringify({ refreshToken: session.refreshToken })
    });
    const replay = await refreshSession(session.refreshToken);

    expect(first.status).toBe(204);
    expect(await first.text()).toBe("");
    expect(second.status).toBe(204);
    expect(replay.status).toBe(401);
    await expect(errorCode(replay)).resolves.toBe("TOKEN_INVALID");

    const row = await env.DB.prepare(
      "SELECT id,revoked_at FROM refresh_tokens WHERE user_id=? ORDER BY created_at LIMIT 1"
    ).bind(session.user.id).first<{ id: string; revoked_at: string | null }>();
    expect(row).toBeDefined();
    expect(row?.revoked_at).not.toBeNull();
    const logoutAudits = await env.DB.prepare(
      "SELECT COUNT(*) AS count FROM audit_events WHERE action='auth.logout' AND target_id=?"
    ).bind(row!.id).first<{ count: number }>();
    expect(logoutAudits?.count).toBe(1);
  });

  it("allows only one winner when the same refresh token is used concurrently", async () => {
    const session = await registerUser();

    const responses = await Promise.all([
      refreshSession(session.refreshToken),
      refreshSession(session.refreshToken)
    ]);
    const statuses = responses.map(response => response.status).sort((left, right) => left - right);

    expect(statuses).toEqual([200, 401]);
    const failure = responses.find(response => response.status === 401);
    expect(failure).toBeDefined();
    await expect(errorCode(failure!)).resolves.toBe("TOKEN_INVALID");

    const active = await env.DB.prepare(
      "SELECT COUNT(*) AS count FROM refresh_tokens WHERE user_id=? AND revoked_at IS NULL"
    ).bind(session.user.id).first<{ count: number }>();
    expect(active?.count).toBe(1);
    const successAudits = await env.DB.prepare(
      "SELECT COUNT(*) AS count FROM audit_events WHERE action='auth.refresh.succeeded'"
    ).first<{ count: number }>();
    expect(successAudits?.count).toBe(1);
  });

  it("returns TOKEN_EXPIRED for an expired refresh token", async () => {
    const session = await registerUser();
    await env.DB.prepare(
      "UPDATE refresh_tokens SET expires_at=? WHERE user_id=?"
    ).bind("2000-01-01T00:00:00.000Z", session.user.id).run();

    const response = await refreshSession(session.refreshToken);

    expect(response.status).toBe(401);
    await expect(errorCode(response)).resolves.toBe("TOKEN_EXPIRED");
  });

  it("rejects disabled users on both access and refresh paths", async () => {
    const session = await registerUser();
    await env.DB.prepare("UPDATE users SET disabled=1 WHERE id=?").bind(session.user.id).run();

    const access = await workerRequest("/v1/me", {
      headers: bearer(session.accessToken)
    });
    const refresh = await refreshSession(session.refreshToken);

    expect(access.status).toBe(403);
    await expect(errorCode(access)).resolves.toBe("ACCOUNT_DISABLED");
    expect(refresh.status).toBe(403);
    await expect(errorCode(refresh)).resolves.toBe("ACCOUNT_DISABLED");
  });

  it("distinguishes forged and expired access tokens without exposing internals", async () => {
    const session = await registerUser();
    const forged = `${session.accessToken.slice(0, -1)}${session.accessToken.endsWith("a") ? "b" : "a"}`;
    const expired = await signTestAccessToken({
      sub: session.user.id,
      role: "user",
      iat: Math.floor(Date.now() / 1000) - 120,
      exp: Math.floor(Date.now() / 1000) - 60,
      jti: crypto.randomUUID()
    });

    const forgedResponse = await workerRequest("/v1/me", { headers: bearer(forged) });
    const expiredResponse = await workerRequest("/v1/me", { headers: bearer(expired) });

    expect(forgedResponse.status).toBe(401);
    await expect(errorCode(forgedResponse)).resolves.toBe("TOKEN_INVALID");
    expect(expiredResponse.status).toBe(401);
    await expect(errorCode(expiredResponse)).resolves.toBe("TOKEN_EXPIRED");
  });

  it("uses the AppError-compatible shape and keeps 401 separate from 403", async () => {
    const response = await workerRequest("/v1/me");
    const body = await response.json<{
      error: Record<string, unknown>;
    }>();

    expect(response.status).toBe(401);
    expect(body.error).toMatchObject({
      code: "AUTH_REQUIRED",
      title: expect.any(String),
      userMessage: expect.any(String),
      suggestion: expect.any(String),
      provider: "LenxTool Worker",
      requestId: expect.any(String),
      retryAfterSeconds: null,
      isRetryable: false
    });
    expect(body.error).not.toHaveProperty("message");
    expect(response.headers.get("x-request-id")).toBe(body.error.requestId);
  });

  it("rejects unknown identity request fields without consuming the refresh token", async () => {
    const session = await registerUser();
    const invalid = await workerRequest("/v1/auth/refresh", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ refreshToken: session.refreshToken, unexpected: true })
    });

    expect(invalid.status).toBe(400);
    await expect(errorCode(invalid)).resolves.toBe("VALIDATION_ERROR");
    const valid = await refreshSession(session.refreshToken);
    expect(valid.status).toBe(200);
  });

  it("stops reading an identity JSON body after its byte limit", async () => {
    const response = await workerRequest("/v1/auth/login", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ username: "x".repeat(5000), password: testPassword })
    });

    expect(response.status).toBe(413);
    await expect(errorCode(response)).resolves.toBe("PAYLOAD_TOO_LARGE");
  });

  it("creates the first admin once and supports login with no stored plaintext", async () => {
    const unauthorized = await workerRequest("/v1/bootstrap/admin", {
      method: "POST",
      headers: { "content-type": "application/json", authorization: "Bootstrap wrong-secret-value-with-32-bytes" },
      body: JSON.stringify({ username: "owner", password: testPassword })
    });
    expect(unauthorized.status).toBe(401);

    const created = await workerRequest("/v1/bootstrap/admin", {
      method: "POST",
      headers: { "content-type": "application/json", authorization: `Bootstrap ${env.BOOTSTRAP_TOKEN}` },
      body: JSON.stringify({ username: "owner", password: testPassword })
    });
    expect(created.status).toBe(201);
    await expect(created.json()).resolves.toMatchObject({
      user: { username: "owner", role: "ADMIN" }
    });

    const repeated = await workerRequest("/v1/bootstrap/admin", {
      method: "POST",
      headers: { "content-type": "application/json", authorization: `Bootstrap ${env.BOOTSTRAP_TOKEN}` },
      body: JSON.stringify({ username: "other-owner", password: testPassword })
    });
    expect(repeated.status).toBe(409);
    await expect(errorCode(repeated)).resolves.toBe("BOOTSTRAP_ALREADY_COMPLETED");

    const login = await workerRequest("/v1/auth/login", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ username: "owner", password: testPassword })
    });
    const session = await login.json<SessionResponse>();
    expect(login.status).toBe(200);
    expect(session.user.role).toBe("ADMIN");
    expect(session.quota).toBeDefined();
    expect(session.expiresInSeconds).toBe(900);

    const row = await env.DB.prepare(
      "SELECT password_salt,password_hash FROM users WHERE username_norm='owner'"
    ).first<{ password_salt: string; password_hash: string }>();
    expect(row?.password_salt).not.toContain(testPassword);
    expect(row?.password_hash).not.toContain(testPassword);
    const audits = await env.DB.prepare(
      "SELECT COUNT(*) AS count FROM audit_events WHERE action='bootstrap.admin.created'"
    ).first<{ count: number }>();
    expect(audits?.count).toBe(1);
  });
});

async function registerUser(): Promise<SessionResponse> {
  await seedInvite();
  const response = await registerWithInvite("reader");
  expect(response.status).toBe(201);
  return response.json<SessionResponse>();
}

async function seedInvite(): Promise<string> {
  const inviterId = crypto.randomUUID();
  const now = new Date().toISOString();
  await env.DB.prepare(
    "INSERT INTO users(id,username,username_norm,password_salt,password_hash,role,ai_daily_limit,speech_daily_seconds,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?)"
  ).bind(inviterId, "inviter", "inviter", "unused-salt", "unused-hash", "admin", 0, 0, now, now).run();
  await env.DB.prepare(
    "INSERT INTO invites(id,code_hash,created_by,role,ai_daily_limit,speech_daily_seconds,max_uses,created_at) VALUES(?,?,?,?,?,?,?,?)"
  ).bind(crypto.randomUUID(), await sha256(inviteCode), inviterId, "user", 20, 900, 1, now).run();
  return inviterId;
}

function registerWithInvite(username: string): Promise<Response> {
  return workerRequest("/v1/auth/register", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ username, password: testPassword, inviteCode })
  });
}

function workerRequest(path: string, init?: RequestInit): Promise<Response> {
  return exports.default.fetch(new Request(`${baseUrl}${path}`, init));
}

function refreshSession(refreshToken: string): Promise<Response> {
  return workerRequest("/v1/auth/refresh", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ refreshToken })
  });
}

function bearer(token: string, json = false): HeadersInit {
  return {
    authorization: `Bearer ${token}`,
    ...(json ? { "content-type": "application/json" } : {})
  };
}

async function errorCode(response: Response): Promise<string> {
  const body = await response.clone().json<{ error: { code: string } }>();
  return body.error.code;
}

async function sha256(value: string): Promise<string> {
  const bytes = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value)));
  return toBase64Url(bytes);
}

async function signTestAccessToken(claims: Record<string, unknown>): Promise<string> {
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
