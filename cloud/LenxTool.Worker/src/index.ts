export interface Env {
  DB: D1Database;
  TOKEN_SECRET: string;
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

const jsonHeaders = { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" };
const encoder = new TextEncoder();

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const requestId = request.headers.get("x-request-id") ?? crypto.randomUUID();
    try {
      const url = new URL(request.url);
      if (request.method === "GET" && url.pathname === "/health") return json({ ok: true, service: "lenx-tool-api" }, 200, requestId);
      assertTokenSecret(env);
      if (request.method === "POST" && url.pathname === "/v1/auth/register") return register(request, env, requestId);
      if (request.method === "POST" && url.pathname === "/v1/auth/login") return login(request, env, requestId);
      if (request.method === "POST" && url.pathname === "/v1/auth/refresh") return refresh(request, env, requestId);

      const auth = await authenticate(request, env, requestId);
      if (request.method === "POST" && url.pathname === "/v1/admin/invites") return createInvite(request, env, auth);
      if (request.method === "PATCH" && url.pathname.startsWith("/v1/admin/users/")) return updateUser(request, env, auth, url.pathname.split("/").at(-1)!);
      if (request.method === "POST" && url.pathname === "/v1/proxy/ai") return proxyAi(request, env, auth);
      if (request.method === "POST" && url.pathname === "/v1/proxy/transcriptions") return proxySpeech(request, env, auth);
      return json({ error: { code: "NOT_FOUND", message: "接口不存在" } }, 404, requestId);
    } catch (error) {
      if (error instanceof ApiError) return json({ error: error.body }, error.status, requestId, error.retryAfter);
      return json({ error: { code: "INTERNAL_ERROR", message: "服务暂时不可用" } }, 500, requestId);
    }
  }
};

async function register(request: Request, env: Env, requestId: string): Promise<Response> {
  await enforceAuthRateLimit(request, env);
  const body = await readJson(request, 16_384);
  const username = requireString(body.username, "用户名", 3, 40);
  const password = requireString(body.password, "密码", 10, 128);
  const inviteCode = requireString(body.inviteCode, "邀请码", 8, 128);
  const usernameNorm = normalizeUsername(username);
  const inviteHash = await sha256(inviteCode.trim());
  const invite = await env.DB.prepare(
    "SELECT id,role,ai_daily_limit,speech_daily_seconds FROM invites WHERE code_hash=? AND disabled=0 AND used_count<max_uses AND (expires_at IS NULL OR expires_at>?)"
  ).bind(inviteHash, nowIso()).first<{ id:string; role:"user"|"admin"; ai_daily_limit:number; speech_daily_seconds:number }>();
  if (!invite) throw new ApiError(400, "INVITE_INVALID", "邀请码无效、过期或已用完");

  const id = crypto.randomUUID();
  const { salt, hash } = await hashPassword(password);
  const now = nowIso();
  const results = await env.DB.batch([
    env.DB.prepare("UPDATE invites SET used_count=used_count+1 WHERE id=? AND used_count<max_uses AND disabled=0").bind(invite.id),
    env.DB.prepare("INSERT INTO users(id,username,username_norm,password_salt,password_hash,role,ai_daily_limit,speech_daily_seconds,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?)")
      .bind(id, username.trim(), usernameNorm, salt, hash, invite.role, invite.ai_daily_limit, invite.speech_daily_seconds, now, now)
  ]);
  if ((results[0]?.meta.changes ?? 0) !== 1) throw new ApiError(409, "INVITE_RACE", "邀请码刚刚已被其他注册使用");
  const user = await getUser(env, id);
  const tokens = await issueTokens(env, user);
  await audit(env, id, "user", id, "register", requestId, request);
  return json({ user: publicUser(user), ...tokens }, 201, requestId);
}

async function login(request: Request, env: Env, requestId: string): Promise<Response> {
  await enforceAuthRateLimit(request, env);
  const body = await readJson(request, 16_384);
  const usernameNorm = normalizeUsername(requireString(body.username, "用户名", 3, 40));
  const password = requireString(body.password, "密码", 1, 128);
  const user = await env.DB.prepare("SELECT * FROM users WHERE username_norm=?").bind(usernameNorm).first<UserRow>();
  if (!user || !(await verifyPassword(password, user.password_salt, user.password_hash))) {
    throw new ApiError(401, "CREDENTIALS_INVALID", "用户名或密码错误");
  }
  if (user.disabled) throw new ApiError(403, "ACCOUNT_DISABLED", "账号已被禁用");
  const tokens = await issueTokens(env, user);
  await audit(env, user.id, "user", user.id, "login", requestId, request);
  return json({ user: publicUser(user), ...tokens }, 200, requestId);
}

async function refresh(request: Request, env: Env, requestId: string): Promise<Response> {
  const body = await readJson(request, 8192);
  const rawToken = requireString(body.refreshToken, "刷新令牌", 32, 512);
  const tokenHash = await sha256(rawToken);
  const row = await env.DB.prepare("SELECT id,user_id FROM refresh_tokens WHERE token_hash=? AND revoked_at IS NULL AND expires_at>?")
    .bind(tokenHash, nowIso()).first<{id:string;user_id:string}>();
  if (!row) throw new ApiError(401, "REFRESH_INVALID", "刷新令牌无效或已过期");
  const user = await getUser(env, row.user_id);
  if (user.disabled) throw new ApiError(403, "ACCOUNT_DISABLED", "账号已被禁用");
  const tokens = await issueTokens(env, user);
  const newHash = await sha256(tokens.refreshToken);
  await env.DB.prepare("UPDATE refresh_tokens SET revoked_at=?,replaced_by=(SELECT id FROM refresh_tokens WHERE token_hash=?) WHERE id=? AND revoked_at IS NULL")
    .bind(nowIso(), newHash, row.id).run();
  await audit(env, user.id, "token", row.id, "refresh", requestId, request);
  return json(tokens, 200, requestId);
}

async function createInvite(request: Request, env: Env, auth: AuthContext): Promise<Response> {
  requireAdmin(auth);
  const body = await readJson(request, 8192);
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
  const target = await getUser(env, userId);
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

async function issueTokens(env: Env, user: UserRow): Promise<{accessToken:string;refreshToken:string;expiresIn:number}> {
  const ttl = Number(env.ACCESS_TOKEN_TTL_SECONDS || "900");
  const now = Math.floor(Date.now() / 1000);
  const claims: AccessClaims = { sub: user.id, role: user.role, iat: now, exp: now + ttl, jti: crypto.randomUUID() };
  const accessToken = await signAccessToken(claims, env.TOKEN_SECRET);
  const refreshToken = randomToken(32);
  const refreshId = crypto.randomUUID();
  const days = Number(env.REFRESH_TOKEN_TTL_DAYS || "30");
  const expires = new Date(Date.now() + days * 86400000).toISOString();
  await env.DB.prepare("INSERT INTO refresh_tokens(id,user_id,token_hash,expires_at,created_at) VALUES(?,?,?,?,?)")
    .bind(refreshId, user.id, await sha256(refreshToken), expires, nowIso()).run();
  return { accessToken, refreshToken, expiresIn: ttl };
}

export function normalizeUsername(value: string): string { return value.normalize("NFKC").trim().toLocaleLowerCase("zh-CN"); }

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
  const claims=JSON.parse(new TextDecoder().decode(fromBase64Url(payload))) as AccessClaims;
  if(!claims.sub||claims.exp<=Math.floor(Date.now()/1000)) throw new ApiError(401,"TOKEN_EXPIRED","登录已过期");
  return claims;
}
async function hmac(value:string,secret:string):Promise<string>{
  const key=await crypto.subtle.importKey("raw",encoder.encode(secret),{name:"HMAC",hash:"SHA-256"},false,["sign"]);
  return toBase64Url(new Uint8Array(await crypto.subtle.sign("HMAC",key,encoder.encode(value))));
}
async function sha256(value:string):Promise<string>{return toBase64Url(new Uint8Array(await crypto.subtle.digest("SHA-256",encoder.encode(value))));}

async function getUser(env:Env,id:string):Promise<UserRow>{const user=await env.DB.prepare("SELECT * FROM users WHERE id=?").bind(id).first<UserRow>();if(!user)throw new ApiError(401,"USER_NOT_FOUND","账号不存在");return user;}
function publicUser(user:UserRow){return{id:user.id,username:user.username,role:user.role,aiDailyLimit:user.ai_daily_limit,speechDailySeconds:user.speech_daily_seconds};}
function requireAdmin(auth:AuthContext){if(auth.user.role!=="admin")throw new ApiError(403,"ADMIN_REQUIRED","需要管理员权限");}
function assertTokenSecret(env:Env){if(!env.TOKEN_SECRET)throw new Error("TOKEN_SECRET missing");}
function requireString(value:unknown,label:string,min:number,max:number):string{if(typeof value!=="string"||value.trim().length<min||value.length>max)throw new ApiError(400,"VALIDATION_ERROR",`${label}格式无效`);return value;}
function boundedInt(value:unknown,min:number,max:number,label:string):number{const n=Number(value);if(!Number.isInteger(n)||n<min||n>max)throw new ApiError(400,"VALIDATION_ERROR",`${label}超出范围`);return n;}
function assertContentLength(request:Request,max:number){const value=Number(request.headers.get("content-length"));if(Number.isFinite(value)&&value>max)throw new ApiError(413,"PAYLOAD_TOO_LARGE","请求内容过大");}
async function readJson(request:Request,max:number):Promise<Record<string,unknown>>{assertContentLength(request,max);const text=await request.text();if(text.length>max)throw new ApiError(413,"PAYLOAD_TOO_LARGE","请求内容过大");try{return JSON.parse(text) as Record<string,unknown>;}catch{throw new ApiError(400,"JSON_INVALID","JSON 格式无效");}}
async function enforceAuthRateLimit(request:Request,env:Env){const ip=request.headers.get("cf-connecting-ip")??"unknown";const key=await sha256(ip);const bucket=new Date().toISOString().slice(0,16);await env.DB.prepare("INSERT INTO auth_attempts(key_hash,bucket,attempts) VALUES(?,?,1) ON CONFLICT(key_hash,bucket) DO UPDATE SET attempts=attempts+1").bind(key,bucket).run();const row=await env.DB.prepare("SELECT attempts FROM auth_attempts WHERE key_hash=? AND bucket=?").bind(key,bucket).first<{attempts:number}>();if((row?.attempts??0)>20)throw new ApiError(429,"AUTH_RATE_LIMIT","尝试次数过多",60);}
async function audit(env:Env,actor:string|null,targetType:string,targetId:string|null,action:string,requestId:string,request:Request){const ip=request.headers.get("cf-connecting-ip");await env.DB.prepare("INSERT INTO audit_events(id,actor_user_id,target_type,target_id,action,request_id,ip_hash,created_at) VALUES(?,?,?,?,?,?,?,?)").bind(crypto.randomUUID(),actor,targetType,targetId,action,requestId,ip?await sha256(ip):null,nowIso()).run();}
function passthrough(upstream:Response,requestId:string){const headers=new Headers();for(const name of ["content-type","retry-after","x-request-id","x-ratelimit-limit-requests","x-ratelimit-remaining-requests"]){const value=upstream.headers.get(name);if(value)headers.set(name,value);}headers.set("x-request-id",headers.get("x-request-id")??requestId);headers.set("cache-control","no-store");return new Response(upstream.body,{status:upstream.status,headers});}
function json(body:unknown,status:number,requestId:string,retryAfter?:number){const headers=new Headers(jsonHeaders);headers.set("x-request-id",requestId);if(retryAfter)headers.set("retry-after",String(retryAfter));return new Response(JSON.stringify(body),{status,headers});}
function nowIso(){return new Date().toISOString();}
function randomToken(bytes:number){return toBase64Url(crypto.getRandomValues(new Uint8Array(bytes)));}
function toBase64Url(bytes:Uint8Array){let binary="";for(const b of bytes)binary+=String.fromCharCode(b);return btoa(binary).replaceAll("+","-").replaceAll("/","_").replace(/=+$/u,"");}
function fromBase64Url(value:string){const base=value.replaceAll("-","+").replaceAll("_","/").padEnd(Math.ceil(value.length/4)*4,"=");const binary=atob(base);return Uint8Array.from(binary,c=>c.charCodeAt(0));}
function timingSafeEqual(left:string,right:string){if(left.length!==right.length)return false;let diff=0;for(let i=0;i<left.length;i++)diff|=left.charCodeAt(i)^right.charCodeAt(i);return diff===0;}

class ApiError extends Error {
  constructor(public status:number,code:string,message:string,public retryAfter?:number){super(message);this.body={code,message};}
  body:{code:string;message:string};
}
