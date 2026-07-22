import { CatalogApiError, handleCatalogAdminRequest, handleCatalogReadRequest } from "./catalog";

export interface Env {
  DB: D1Database;
  TOKEN_SECRET: string;
  BOOTSTRAP_TOKEN?: string;
  GROQ_API_KEY: string;
  DEEPSEEK_API_KEY: string;
  ACCESS_TOKEN_TTL_SECONDS: string;
  REFRESH_TOKEN_TTL_DAYS: string;
}

interface UserRow {
  id: string;
  username: string;
  username_norm: string;
  password_salt: string;
  password_hash: string;
  role: "user" | "admin";
  disabled: number;
  ai_daily_limit: number;
  speech_daily_seconds: number;
}

interface AuthContext { user: UserRow; requestId: string; }
interface AccessClaims { sub: string; role: "user" | "admin"; exp: number; iat: number; jti: string; }
interface TokenPair { accessToken: string; refreshToken: string; expiresInSeconds: number; }
interface TokenMaterial extends TokenPair { refreshId: string; refreshHash: string; refreshExpiresAt: string; }

const jsonHeaders = { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" };
const encoder = new TextEncoder();

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const requestId = resolveRequestId(request);
    try {
      const url = new URL(request.url);
      if (request.method === "GET" && url.pathname === "/health") return json({ ok: true, service: "lenx-tool-api" }, 200, requestId);
      assertTokenSecret(env);
      if (request.method === "POST" && url.pathname === "/v1/auth/register") return await register(request, env, requestId);
      if (request.method === "POST" && url.pathname === "/v1/auth/login") return await login(request, env, requestId);
      if (request.method === "POST" && url.pathname === "/v1/auth/refresh") return await refresh(request, env, requestId);
      if (request.method === "POST" && url.pathname === "/v1/bootstrap/admin") return await bootstrapAdmin(request, env, requestId);

      const auth = await authenticate(request, env, requestId);
      if (request.method === "POST" && url.pathname === "/v1/auth/logout") return await logout(request, env, auth);
      if (request.method === "GET" && url.pathname === "/v1/me") return await currentUser(env, auth);
      const catalogAuth = {
        userId: auth.user.id,
        role: auth.user.role,
        requestId: auth.requestId
      };
      const catalogReadResponse = await handleCatalogReadRequest(request, env.DB, catalogAuth, url);
      if (catalogReadResponse) return catalogReadResponse;
      const catalogResponse = await handleCatalogAdminRequest(request, env.DB, catalogAuth, url);
      if (catalogResponse) return catalogResponse;
      if (request.method === "POST" && url.pathname === "/v1/admin/invites") return await createInvite(request, env, auth);
      if (request.method === "PATCH" && url.pathname.startsWith("/v1/admin/users/")) return await updateUser(request, env, auth, url.pathname.split("/").at(-1)!);
      if (request.method === "POST" && url.pathname === "/v1/proxy/ai") return await proxyAi(request, env, auth);
      if (request.method === "POST" && url.pathname === "/v1/proxy/transcriptions") return await proxySpeech(request, env, auth);
      throw new ApiError(404, "RESOURCE_NOT_FOUND", "接口不存在");
    } catch (error) {
      const apiError = error instanceof CatalogApiError
        ? new ApiError(error.status, error.code, error.userMessage, undefined, error.details, error.isRetryable)
        : error instanceof ApiError
        ? error
        : new ApiError(500, "INTERNAL_ERROR", "服务暂时不可用");
      return json({ error: apiError.toBody(requestId) }, apiError.status, requestId, apiError.retryAfter);
    }
  }
};

async function register(request: Request, env: Env, requestId: string): Promise<Response> {
  await enforceAuthRateLimit(request, env);
  const body = await readJson(request, 16_384);
  assertOnlyFields(body, ["username", "password", "inviteCode"]);
  const username = requireUsername(body.username);
  const password = requireString(body.password, "密码", 10, 128);
  const inviteCode = requireString(body.inviteCode, "邀请码", 8, 128);
  const inviteHash = await sha256(inviteCode.trim());
  const inviteLookupTime = nowIso();
  const invite = await env.DB.prepare(
    "SELECT id,role,ai_daily_limit,speech_daily_seconds FROM invites WHERE code_hash=? AND disabled=0 AND used_count<max_uses AND (expires_at IS NULL OR expires_at>?)"
  ).bind(inviteHash, inviteLookupTime).first<{ id:string; role:"user"|"admin"; ai_daily_limit:number; speech_daily_seconds:number }>();
  if (!invite) throw new ApiError(400, "INVITE_INVALID", "邀请码无效、过期或已用完");

  const id = crypto.randomUUID();
  const { salt, hash } = await hashPassword(password);
  const now = nowIso();
  const results = await env.DB.batch([
    env.DB.prepare(
      "UPDATE invites SET used_count=used_count+1 WHERE id=? AND used_count<max_uses AND disabled=0 AND (expires_at IS NULL OR expires_at>?)"
    ).bind(invite.id, inviteLookupTime),
    env.DB.prepare(
      "INSERT INTO users(id,username,username_norm,password_salt,password_hash,role,ai_daily_limit,speech_daily_seconds,created_at,updated_at) " +
      "SELECT ?,?,?,?,?,?,?,?,?,? WHERE changes()=1"
    )
      .bind(id, username.display, username.normalized, salt, hash, invite.role, invite.ai_daily_limit, invite.speech_daily_seconds, now, now)
  ]);
  if ((results[0]?.meta.changes ?? 0) !== 1 || (results[1]?.meta.changes ?? 0) !== 1) {
    throw new ApiError(409, "INVITE_RACE", "邀请码刚刚已被其他注册使用");
  }
  const user = await getUser(env, id);
  const tokens = await issueTokens(env, user);
  await audit(env, id, "user", id, "register", requestId, request);
  return json({ user: publicUser(user), ...tokens }, 201, requestId);
}

async function login(request: Request, env: Env, requestId: string): Promise<Response> {
  await enforceAuthRateLimit(request, env);
  const body = await readJson(request, 4096);
  assertOnlyFields(body, ["username", "password"]);
  const usernameNorm = requireUsername(body.username).normalized;
  const password = requireString(body.password, "密码", 1, 128);
  const user = await env.DB.prepare("SELECT * FROM users WHERE username_norm=?").bind(usernameNorm).first<UserRow>();
  if (!user || !(await verifyPassword(password, user.password_salt, user.password_hash))) {
    await audit(env, user?.id ?? null, "user", user?.id ?? null, "auth.login.failed", requestId, request);
    throw new ApiError(401, "CREDENTIALS_INVALID", "用户名或密码错误");
  }
  if (user.disabled) {
    await audit(env, user.id, "user", user.id, "auth.login.failed", requestId, request);
    throw new ApiError(403, "ACCOUNT_DISABLED", "账号已被禁用");
  }
  const tokens = await issueTokens(env, user);
  await audit(env, user.id, "user", user.id, "auth.login.succeeded", requestId, request);
  return json({ ...(await accountState(env, user)), ...tokens }, 200, requestId);
}

async function refresh(request: Request, env: Env, requestId: string): Promise<Response> {
  await enforceAuthRateLimit(request, env);
  const body = await readJson(request, 4096);
  assertOnlyFields(body, ["refreshToken"]);
  const rawToken = requireString(body.refreshToken, "刷新令牌", 32, 512);
  const tokenHash = await sha256(rawToken);
  const now = nowIso();
  const row = await env.DB.prepare(
    "SELECT id,user_id,expires_at,revoked_at FROM refresh_tokens WHERE token_hash=?"
  ).bind(tokenHash).first<{id:string;user_id:string;expires_at:string;revoked_at:string|null}>();
  if (!row || row.revoked_at) {
    await audit(env, row?.user_id ?? null, "token", row?.id ?? null, "auth.refresh.failed", requestId, request);
    throw new ApiError(401, "TOKEN_INVALID", "刷新令牌无效或已过期");
  }
  if (row.expires_at <= now) {
    await audit(env, row.user_id, "token", row.id, "auth.refresh.failed", requestId, request);
    throw new ApiError(401, "TOKEN_EXPIRED", "刷新令牌已过期");
  }
  const user = await getUser(env, row.user_id);
  if (user.disabled) {
    await audit(env, user.id, "token", row.id, "auth.refresh.failed", requestId, request);
    throw new ApiError(403, "ACCOUNT_DISABLED", "账号已被禁用");
  }
  const tokens = await rotateTokens(env, user, row.id, now, requestId, request);
  if (!tokens) {
    await audit(env, user.id, "token", row.id, "auth.refresh.failed", requestId, request);
    throw new ApiError(401, "TOKEN_INVALID", "刷新令牌无效或已过期");
  }
  return json(tokens, 200, requestId);
}

async function logout(request: Request, env: Env, auth: AuthContext): Promise<Response> {
  const body = await readJson(request, 4096);
  assertOnlyFields(body, ["refreshToken"]);
  const rawToken = requireString(body.refreshToken, "刷新令牌", 32, 512);
  const row = await env.DB.prepare(
    "SELECT id,user_id,revoked_at FROM refresh_tokens WHERE token_hash=?"
  ).bind(await sha256(rawToken)).first<{id:string;user_id:string;revoked_at:string|null}>();

  if (row?.user_id === auth.user.id && row.revoked_at === null) {
    const successAudit = await conditionalAuditStatement(
      env, auth.user.id, "token", row.id, "auth.logout", auth.requestId, request
    );
    await env.DB.batch([
      env.DB.prepare(
        "UPDATE refresh_tokens SET revoked_at=? WHERE id=? AND user_id=? AND revoked_at IS NULL"
      ).bind(nowIso(), row.id, auth.user.id),
      successAudit
    ]);
  }

  return empty(204, auth.requestId);
}

async function currentUser(env: Env, auth: AuthContext): Promise<Response> {
  return json({
    ...(await accountState(env, auth.user)),
    serverTime: nowIso()
  }, 200, auth.requestId);
}

async function bootstrapAdmin(request: Request, env: Env, requestId: string): Promise<Response> {
  if (!env.BOOTSTRAP_TOKEN || env.BOOTSTRAP_TOKEN.length < 32) {
    throw new ApiError(404, "RESOURCE_NOT_FOUND", "接口不存在");
  }
  await enforceAuthRateLimit(request, env);
  const authorization = request.headers.get("authorization") ?? "";
  const supplied = authorization.startsWith("Bootstrap ") ? authorization.slice(10) : "";
  const valid = timingSafeEqual(await sha256(supplied), await sha256(env.BOOTSTRAP_TOKEN));
  if (!valid) {
    await audit(env, null, "bootstrap", null, "bootstrap.admin.failed", requestId, request);
    throw new ApiError(401, "BOOTSTRAP_AUTH_INVALID", "初始化凭据无效");
  }

  const body = await readJson(request, 8192);
  assertOnlyFields(body, ["username", "password"]);
  const username = requireUsername(body.username);
  const password = requireString(body.password, "密码", 12, 128);
  const existing = await env.DB.prepare("SELECT COUNT(*) AS count FROM users").first<{count:number}>();
  if ((existing?.count ?? 0) !== 0) {
    throw new ApiError(409, "BOOTSTRAP_ALREADY_COMPLETED", "首个管理员已经初始化");
  }

  const id = crypto.randomUUID();
  const now = nowIso();
  const { salt, hash } = await hashPassword(password);
  const successAudit = await conditionalAuditStatement(
    env, id, "user", id, "bootstrap.admin.created", requestId, request
  );
  const results = await env.DB.batch([
    env.DB.prepare(
      "INSERT INTO users(id,username,username_norm,password_salt,password_hash,role,ai_daily_limit,speech_daily_seconds,created_at,updated_at) " +
      "SELECT ?,?,?,?,?,'admin',0,0,?,? WHERE NOT EXISTS (SELECT 1 FROM users)"
    ).bind(id, username.display, username.normalized, salt, hash, now, now),
    successAudit
  ]);
  if ((results[0]?.meta.changes ?? 0) !== 1 || (results[1]?.meta.changes ?? 0) !== 1) {
    throw new ApiError(409, "BOOTSTRAP_ALREADY_COMPLETED", "首个管理员已经初始化");
  }

  const user = await getUser(env, id);
  return json({ user: publicUser(user) }, 201, requestId);
}

async function createInvite(request: Request, env: Env, auth: AuthContext): Promise<Response> {
  requireAdmin(auth);
  const body = await readJson(request, 8192);
  assertOnlyFields(body, ["role", "aiDailyLimit", "speechDailySeconds", "maxUses", "expiresAt"]);
  const role = body.role === "admin" ? "admin" : "user";
  const aiLimit = boundedInt(body.aiDailyLimit ?? 10, 0, 100_000, "AI 每日额度");
  const speechLimit = boundedInt(body.speechDailySeconds ?? 600, 0, 86400, "语音每日额度");
  const maxUses = boundedInt(body.maxUses ?? 1, 1, 10000, "邀请码使用次数");
  const code = randomToken(24);
  const id = crypto.randomUUID();
  await env.DB.prepare("INSERT INTO invites(id,code_hash,created_by,role,ai_daily_limit,speech_daily_seconds,max_uses,expires_at,created_at) VALUES(?,?,?,?,?,?,?,?,?)")
    .bind(id, await sha256(code), auth.user.id, role, aiLimit, speechLimit, maxUses, body.expiresAt ?? null, nowIso()).run();
  await audit(env, auth.user.id, "invite", id, "create", auth.requestId, request);
  return json({ id, code, role, aiDailyLimit: aiLimit, speechDailySeconds: speechLimit, maxUses }, 201, auth.requestId);
}

async function updateUser(request: Request, env: Env, auth: AuthContext, userId: string): Promise<Response> {
  requireAdmin(auth);
  const body = await readJson(request, 8192);
  assertOnlyFields(body, ["disabled", "aiDailyLimit", "speechDailySeconds"]);
  const target = await getManagedUser(env, userId);
  const disabled = body.disabled === undefined ? target.disabled : body.disabled ? 1 : 0;
  const ai = body.aiDailyLimit === undefined ? target.ai_daily_limit : boundedInt(body.aiDailyLimit, 0, 100_000, "AI 每日额度");
  const speech = body.speechDailySeconds === undefined ? target.speech_daily_seconds : boundedInt(body.speechDailySeconds, 0, 86400, "语音每日额度");
  await env.DB.prepare("UPDATE users SET disabled=?,ai_daily_limit=?,speech_daily_seconds=?,updated_at=? WHERE id=?")
    .bind(disabled, ai, speech, nowIso(), userId).run();
  if (disabled) await env.DB.prepare("UPDATE refresh_tokens SET revoked_at=? WHERE user_id=? AND revoked_at IS NULL").bind(nowIso(), userId).run();
  await audit(env, auth.user.id, "user", userId, "update_quota_or_status", auth.requestId, request);
  return json({ ok: true }, 200, auth.requestId);
}

async function proxyAi(request: Request, env: Env, auth: AuthContext): Promise<Response> {
  if (!env.DEEPSEEK_API_KEY) throw new ApiError(503, "PROVIDER_NOT_CONFIGURED", "共享 AI 服务尚未配置");
  assertContentLength(request, 2_000_000);
  const reserved = auth.user.role !== "admin";
  if (reserved) await reserveQuota(env, auth.user, "ai", 1);
  try {
    const upstream = await fetch("https://api.deepseek.com/chat/completions", {
      method: "POST", headers: { "content-type": "application/json", authorization: `Bearer ${env.DEEPSEEK_API_KEY}`, "x-request-id": auth.requestId }, body: request.body
    });
    if (reserved) await settleQuota(env, auth.user.id, "ai", 1, upstream.ok);
    await audit(env, auth.user.id, "provider", "deepseek", "shared_ai", auth.requestId, request);
    return passthrough(upstream, auth.requestId);
  } catch (error) {
    if (reserved) await settleQuota(env, auth.user.id, "ai", 1, false);
    throw error;
  }
}

async function proxySpeech(request: Request, env: Env, auth: AuthContext): Promise<Response> {
  if (!env.GROQ_API_KEY) throw new ApiError(503, "PROVIDER_NOT_CONFIGURED", "共享语音服务尚未配置");
  assertContentLength(request, 210_000_000);
  const seconds = Number(request.headers.get("x-audio-duration-seconds"));
  if (!Number.isFinite(seconds) || seconds <= 0 || seconds > 7200) throw new ApiError(400, "DURATION_REQUIRED", "需要有效的音频时长");
  const reserved = auth.user.role !== "admin";
  if (reserved) await reserveQuota(env, auth.user, "speech", seconds);
  try {
    const upstream = await fetch("https://api.groq.com/openai/v1/audio/transcriptions", {
      method: "POST", headers: { "content-type": request.headers.get("content-type") ?? "application/octet-stream", authorization: `Bearer ${env.GROQ_API_KEY}`, "x-request-id": auth.requestId }, body: request.body
    });
    if (reserved) await settleQuota(env, auth.user.id, "speech", seconds, upstream.ok);
    await audit(env, auth.user.id, "provider", "groq", "shared_speech", auth.requestId, request);
    return passthrough(upstream, auth.requestId);
  } catch (error) {
    if (reserved) await settleQuota(env, auth.user.id, "speech", seconds, false);
    throw error;
  }
}

async function reserveQuota(env: Env, user: UserRow, kind: "ai"|"speech", amount: number): Promise<void> {
  const date = new Date().toISOString().slice(0, 10);
  await env.DB.prepare("INSERT OR IGNORE INTO daily_usage(user_id,usage_date) VALUES(?,?)").bind(user.id, date).run();
  const sql = kind === "ai"
    ? "UPDATE daily_usage SET ai_reserved=ai_reserved+? WHERE user_id=? AND usage_date=? AND ai_used+ai_reserved+?<=?"
    : "UPDATE daily_usage SET speech_reserved_seconds=speech_reserved_seconds+? WHERE user_id=? AND usage_date=? AND speech_used_seconds+speech_reserved_seconds+?<=?";
  const limit = kind === "ai" ? user.ai_daily_limit : user.speech_daily_seconds;
  const result = await env.DB.prepare(sql).bind(amount, user.id, date, amount, limit).run();
  if ((result.meta.changes ?? 0) !== 1) throw new ApiError(429, "SHARED_QUOTA_EXCEEDED", "今日共享额度已用完", 86400);
}

async function settleQuota(env: Env, userId: string, kind: "ai"|"speech", amount: number, success: boolean): Promise<void> {
  const date = new Date().toISOString().slice(0, 10);
  const sql = kind === "ai"
    ? `UPDATE daily_usage SET ai_reserved=MAX(0,ai_reserved-?),ai_used=ai_used+? WHERE user_id=? AND usage_date=?`
    : `UPDATE daily_usage SET speech_reserved_seconds=MAX(0,speech_reserved_seconds-?),speech_used_seconds=speech_used_seconds+? WHERE user_id=? AND usage_date=?`;
  await env.DB.prepare(sql).bind(amount, success ? amount : 0, userId, date).run();
}

async function authenticate(request: Request, env: Env, requestId: string): Promise<AuthContext> {
  const auth = request.headers.get("authorization");
  if (!auth?.startsWith("Bearer ")) throw new ApiError(401, "AUTH_REQUIRED", "请先登录");
  const claims = await verifyAccessToken(auth.slice(7), env.TOKEN_SECRET);
  const user = await getUser(env, claims.sub);
  if (user.disabled) throw new ApiError(403, "ACCOUNT_DISABLED", "账号已被禁用");
  return { user, requestId };
}

async function createTokenMaterial(env: Env, user: UserRow): Promise<TokenMaterial> {
  const ttl = Number(env.ACCESS_TOKEN_TTL_SECONDS || "900");
  const now = Math.floor(Date.now() / 1000);
  const claims: AccessClaims = { sub: user.id, role: user.role, iat: now, exp: now + ttl, jti: crypto.randomUUID() };
  const accessToken = await signAccessToken(claims, env.TOKEN_SECRET);
  const refreshToken = randomToken(32);
  const refreshId = crypto.randomUUID();
  const days = Number(env.REFRESH_TOKEN_TTL_DAYS || "30");
  const refreshExpiresAt = new Date(Date.now() + days * 86400000).toISOString();
  return {
    accessToken,
    refreshToken,
    expiresInSeconds: ttl,
    refreshId,
    refreshHash: await sha256(refreshToken),
    refreshExpiresAt
  };
}

async function issueTokens(env: Env, user: UserRow): Promise<TokenPair> {
  const material = await createTokenMaterial(env, user);
  await env.DB.prepare("INSERT INTO refresh_tokens(id,user_id,token_hash,expires_at,created_at) VALUES(?,?,?,?,?)")
    .bind(material.refreshId, user.id, material.refreshHash, material.refreshExpiresAt, nowIso()).run();
  return tokenPair(material);
}

async function rotateTokens(
  env: Env,
  user: UserRow,
  oldTokenId: string,
  rotatedAt: string,
  requestId: string,
  request: Request
): Promise<TokenPair | null> {
  const material = await createTokenMaterial(env, user);
  const successAudit = await conditionalAuditStatement(
    env, user.id, "token", oldTokenId, "auth.refresh.succeeded", requestId, request
  );
  const results = await env.DB.batch([
    env.DB.prepare(
      "UPDATE refresh_tokens SET revoked_at=?,replaced_by=? WHERE id=? AND revoked_at IS NULL AND expires_at>?"
    ).bind(rotatedAt, material.refreshId, oldTokenId, rotatedAt),
    env.DB.prepare(
      "INSERT INTO refresh_tokens(id,user_id,token_hash,expires_at,created_at) " +
      "SELECT ?,?,?,?,? WHERE EXISTS (SELECT 1 FROM refresh_tokens WHERE id=? AND revoked_at=? AND replaced_by=?)"
    ).bind(
      material.refreshId,
      user.id,
      material.refreshHash,
      material.refreshExpiresAt,
      rotatedAt,
      oldTokenId,
      rotatedAt,
      material.refreshId
    ),
    successAudit
  ]);
  return (results[0]?.meta.changes ?? 0) === 1 &&
    (results[1]?.meta.changes ?? 0) === 1 &&
    (results[2]?.meta.changes ?? 0) === 1
    ? tokenPair(material)
    : null;
}

function tokenPair(material: TokenMaterial): TokenPair {
  return {
    accessToken: material.accessToken,
    refreshToken: material.refreshToken,
    expiresInSeconds: material.expiresInSeconds
  };
}

export function normalizeUsername(value: string): string { return value.normalize("NFKC").trim().toLocaleLowerCase("zh-CN"); }
function requireUsername(value: unknown): {display:string;normalized:string} {
  if (typeof value !== "string") throw new ApiError(400, "VALIDATION_ERROR", "用户名格式无效");
  const display = value.normalize("NFKC").trim();
  const length = Array.from(display).length;
  if (length < 3 || length > 40 || !/^[-._\p{L}\p{M}\p{N}]+$/u.test(display)) {
    throw new ApiError(400, "VALIDATION_ERROR", "用户名格式无效");
  }
  return { display, normalized: normalizeUsername(display) };
}

async function hashPassword(password: string): Promise<{salt:string;hash:string}> {
  const salt = crypto.getRandomValues(new Uint8Array(16));
  return { salt: toBase64Url(salt), hash: await derivePassword(password, salt) };
}
async function verifyPassword(password:string,saltText:string,expected:string):Promise<boolean>{
  const actual = await derivePassword(password, fromBase64Url(saltText));
  return timingSafeEqual(actual, expected);
}
async function derivePassword(password:string,salt:Uint8Array):Promise<string>{
  const key = await crypto.subtle.importKey("raw",encoder.encode(password),"PBKDF2",false,["deriveBits"]);
  const bits = await crypto.subtle.deriveBits({name:"PBKDF2",hash:"SHA-256",salt:salt as BufferSource,iterations:310000},key,256);
  return toBase64Url(new Uint8Array(bits));
}

async function signAccessToken(claims:AccessClaims,secret:string):Promise<string>{
  const header=toBase64Url(encoder.encode(JSON.stringify({alg:"HS256",typ:"JWT"})));
  const payload=toBase64Url(encoder.encode(JSON.stringify(claims)));
  return `${header}.${payload}.${await hmac(`${header}.${payload}`,secret)}`;
}
async function verifyAccessToken(token:string,secret:string):Promise<AccessClaims>{
  const parts=token.split("."); if(parts.length!==3) throw new ApiError(401,"TOKEN_INVALID","登录已失效");
  const [header,payload,signature]=parts as [string,string,string];
  if(!timingSafeEqual(await hmac(`${header}.${payload}`,secret),signature)) throw new ApiError(401,"TOKEN_INVALID","登录已失效");
  let parsedHeader: {alg?:unknown;typ?:unknown};
  let claims: AccessClaims;
  try {
    parsedHeader=JSON.parse(new TextDecoder().decode(fromBase64Url(header))) as {alg?:unknown;typ?:unknown};
    claims=JSON.parse(new TextDecoder().decode(fromBase64Url(payload))) as AccessClaims;
  } catch {
    throw new ApiError(401,"TOKEN_INVALID","登录已失效");
  }
  if(parsedHeader.alg!=="HS256"||parsedHeader.typ!=="JWT"||typeof claims.sub!=="string"||
    (claims.role!=="user"&&claims.role!=="admin")||typeof claims.exp!=="number"||
    typeof claims.iat!=="number"||typeof claims.jti!=="string"){
    throw new ApiError(401,"TOKEN_INVALID","登录已失效");
  }
  if(claims.exp<=Math.floor(Date.now()/1000)) throw new ApiError(401,"TOKEN_EXPIRED","登录已过期");
  return claims;
}
async function hmac(value:string,secret:string):Promise<string>{
  const key=await crypto.subtle.importKey("raw",encoder.encode(secret),{name:"HMAC",hash:"SHA-256"},false,["sign"]);
  return toBase64Url(new Uint8Array(await crypto.subtle.sign("HMAC",key,encoder.encode(value))));
}
async function sha256(value:string):Promise<string>{return toBase64Url(new Uint8Array(await crypto.subtle.digest("SHA-256",encoder.encode(value))));}

async function getUser(env: Env, id: string): Promise<UserRow> {
  const user = await env.DB.prepare("SELECT * FROM users WHERE id=?").bind(id).first<UserRow>();
  if (!user) throw new ApiError(401, "TOKEN_INVALID", "登录已失效");
  return user;
}
async function getManagedUser(env: Env, id: string): Promise<UserRow> {
  const user = await env.DB.prepare("SELECT * FROM users WHERE id=?").bind(id).first<UserRow>();
  if (!user) throw new ApiError(404, "RESOURCE_NOT_FOUND", "账号不存在");
  return user;
}
function publicUser(user: UserRow) {
  return { id: user.id, username: user.username, role: user.role.toUpperCase() as "USER" | "ADMIN" };
}
async function accountState(env:Env,user:UserRow){
  const date=new Date().toISOString().slice(0,10);
  const usage=await env.DB.prepare(
    "SELECT ai_used,ai_reserved,speech_used_seconds,speech_reserved_seconds FROM daily_usage WHERE user_id=? AND usage_date=?"
  ).bind(user.id,date).first<{ai_used:number;ai_reserved:number;speech_used_seconds:number;speech_reserved_seconds:number}>();
  const aiUsed=nonNegativeWhole(usage?.ai_used);
  const aiReserved=nonNegativeWhole(usage?.ai_reserved);
  const speechUsed=nonNegativeWhole(usage?.speech_used_seconds);
  const speechReserved=nonNegativeWhole(usage?.speech_reserved_seconds);
  return {
    user:publicUser(user),
    quota:{
      date,
      ai:{limit:user.ai_daily_limit,used:aiUsed,reserved:aiReserved,remaining:Math.max(0,user.ai_daily_limit-aiUsed-aiReserved)},
      speechSeconds:{limit:user.speech_daily_seconds,used:speechUsed,reserved:speechReserved,remaining:Math.max(0,user.speech_daily_seconds-speechUsed-speechReserved)}
    }
  };
}
function nonNegativeWhole(value: number | undefined) {
  return Math.max(0, Math.ceil(Number.isFinite(value) ? value ?? 0 : 0));
}
function requireAdmin(auth:AuthContext){if(auth.user.role!=="admin")throw new ApiError(403,"ADMIN_REQUIRED","需要管理员权限");}
function assertTokenSecret(env: Env) {
  if (!env.TOKEN_SECRET || encoder.encode(env.TOKEN_SECRET).byteLength < 32) {
    throw new Error("TOKEN_SECRET missing or too short");
  }
}
function requireString(value:unknown,label:string,min:number,max:number):string{if(typeof value!=="string"||value.trim().length<min||value.length>max)throw new ApiError(400,"VALIDATION_ERROR",`${label}格式无效`);return value;}
function boundedInt(value:unknown,min:number,max:number,label:string):number{const n=Number(value);if(!Number.isInteger(n)||n<min||n>max)throw new ApiError(400,"VALIDATION_ERROR",`${label}超出范围`);return n;}
function assertOnlyFields(body:Record<string,unknown>,allowed:readonly string[]){const fields=new Set(allowed);if(Object.keys(body).some(key=>!fields.has(key)))throw new ApiError(400,"VALIDATION_ERROR","请求包含未知字段");}
function assertContentLength(request:Request,max:number){const value=Number(request.headers.get("content-length"));if(Number.isFinite(value)&&value>max)throw new ApiError(413,"PAYLOAD_TOO_LARGE","请求内容过大");}
async function readJson(request: Request, max: number): Promise<Record<string, unknown>> {
  assertContentLength(request, max);
  const bytes = await readBodyWithinLimit(request, max);
  let parsed: unknown;
  try {
    const text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    parsed = JSON.parse(text);
  } catch {
    throw new ApiError(400, "INVALID_JSON", "JSON 格式无效");
  }
  if (parsed === null || Array.isArray(parsed) || typeof parsed !== "object") {
    throw new ApiError(400, "VALIDATION_ERROR", "JSON 必须是对象");
  }
  return parsed as Record<string,unknown>;
}
async function readBodyWithinLimit(request: Request, max: number): Promise<Uint8Array> {
  if (!request.body) return new Uint8Array();
  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > max) {
        try { await reader.cancel(); } catch { /* Preserve the payload error if cancellation also fails. */ }
        throw new ApiError(413, "PAYLOAD_TOO_LARGE", "请求内容过大");
      }
      chunks.push(value);
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
async function enforceAuthRateLimit(request:Request,env:Env){const ip=request.headers.get("cf-connecting-ip")??"unknown";const key=await sha256(ip);const bucket=new Date().toISOString().slice(0,16);await env.DB.prepare("INSERT INTO auth_attempts(key_hash,bucket,attempts) VALUES(?,?,1) ON CONFLICT(key_hash,bucket) DO UPDATE SET attempts=attempts+1").bind(key,bucket).run();const row=await env.DB.prepare("SELECT attempts FROM auth_attempts WHERE key_hash=? AND bucket=?").bind(key,bucket).first<{attempts:number}>();if((row?.attempts??0)>20)throw new ApiError(429,"RATE_LIMITED","尝试次数过多",60);}
async function conditionalAuditStatement(
  env: Env,
  actor: string | null,
  targetType: string,
  targetId: string | null,
  action: string,
  requestId: string,
  request: Request
): Promise<D1PreparedStatement> {
  const ip = request.headers.get("cf-connecting-ip");
  return env.DB.prepare(
    "INSERT INTO audit_events(id,actor_user_id,target_type,target_id,action,request_id,ip_hash,created_at) " +
    "SELECT ?,?,?,?,?,?,?,? WHERE changes()=1"
  ).bind(
    crypto.randomUUID(),
    actor,
    targetType,
    targetId,
    action,
    requestId,
    ip ? await sha256(ip) : null,
    nowIso()
  );
}
async function audit(env:Env,actor:string|null,targetType:string,targetId:string|null,action:string,requestId:string,request:Request){const ip=request.headers.get("cf-connecting-ip");await env.DB.prepare("INSERT INTO audit_events(id,actor_user_id,target_type,target_id,action,request_id,ip_hash,created_at) VALUES(?,?,?,?,?,?,?,?)").bind(crypto.randomUUID(),actor,targetType,targetId,action,requestId,ip?await sha256(ip):null,nowIso()).run();}
function passthrough(upstream:Response,requestId:string){const headers=new Headers();for(const name of ["content-type","retry-after","x-request-id","x-ratelimit-limit-requests","x-ratelimit-remaining-requests"]){const value=upstream.headers.get(name);if(value)headers.set(name,value);}headers.set("x-request-id",headers.get("x-request-id")??requestId);headers.set("cache-control","no-store");return new Response(upstream.body,{status:upstream.status,headers});}
function json(body:unknown,status:number,requestId:string,retryAfter?:number){const headers=new Headers(jsonHeaders);headers.set("x-request-id",requestId);if(retryAfter)headers.set("retry-after",String(retryAfter));return new Response(JSON.stringify(body),{status,headers});}
function empty(status:number,requestId:string){const headers=new Headers({"cache-control":"no-store"});headers.set("x-request-id",requestId);return new Response(null,{status,headers});}
function resolveRequestId(request:Request){const candidate=request.headers.get("x-request-id");return candidate&&candidate.length<=128&&/^[\x20-\x7e]+$/u.test(candidate)?candidate:crypto.randomUUID();}
function nowIso(){return new Date().toISOString();}
function randomToken(bytes:number){return toBase64Url(crypto.getRandomValues(new Uint8Array(bytes)));}
function toBase64Url(bytes:Uint8Array){let binary="";for(const b of bytes)binary+=String.fromCharCode(b);return btoa(binary).replaceAll("+","-").replaceAll("/","_").replace(/=+$/u,"");}
function fromBase64Url(value:string){const base=value.replaceAll("-","+").replaceAll("_","/").padEnd(Math.ceil(value.length/4)*4,"=");const binary=atob(base);return Uint8Array.from(binary,c=>c.charCodeAt(0));}
function timingSafeEqual(left:string,right:string){if(left.length!==right.length)return false;let diff=0;for(let i=0;i<left.length;i++)diff|=left.charCodeAt(i)^right.charCodeAt(i);return diff===0;}

class ApiError extends Error {
  constructor(
    public status:number,
    public code:string,
    public userMessage:string,
    public retryAfter?:number,
    public details?:Record<string,unknown>,
    public retryable?:boolean
  ){super(userMessage);}
  toBody(requestId:string){
    const title=this.status===401?"认证失败":this.status===403?"没有访问权限":
      this.status===404?"资源不存在":this.status===409?"请求发生冲突":
      this.status===429?"请求过于频繁":this.status>=500?"服务暂时不可用":"请求内容有误";
    const suggestion=this.status===401?"请重新登录或更新凭据。":this.status===403?"请检查账号状态或联系管理员。":
      this.status===404?"请检查请求地址后重试。":this.status===409?"请同步最新状态后重试。":
      this.status===429?"请在限制解除后重试。":this.status>=500?"请稍后重试。":"请检查输入后重试。";
    return {
      code:this.code,title,userMessage:this.userMessage,suggestion,provider:"LenxTool Worker",requestId,
      retryAfterSeconds:this.retryAfter??null,isRetryable:this.retryable??(this.status===429||this.status>=500),
      ...(this.details ? { details:this.details } : {})
    };
  }
}
