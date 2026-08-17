# Cloudflare Worker 与 D1 部署

## 设计目标

Worker 只保存账号、邀请码、角色、额度、用量、refresh token 哈希、基础审计，以及管理员发布的共享分类、Feed 和 AI 开关/上限配置；不保存文章正文、摘要、译文、早报正文、字幕、音视频、文件、本地路径或请求正文。共享 Groq/DeepSeek Key 仅保存在 Worker Secret。目录表与约束见 [Worker D1 Schema](api/worker-d1-schema.md)。

## 资源准备

1. 安装 Node.js 与 Wrangler，并登录 Cloudflare。
2. 创建 D1 数据库：

```powershell
cd cloud\LenxTool.Worker
npx wrangler d1 create lenx-tool
```

3. 将返回的 `database_id` 写入 `wrangler.toml`，不要把 Secret 写入该文件。
4. 应用迁移：

```powershell
npx wrangler d1 migrations list lenx-tool --remote
npx wrangler d1 time-travel info lenx-tool
npx wrangler d1 migrations apply lenx-tool --remote
npx wrangler d1 migrations list lenx-tool --remote
```

应用前记录 Time Travel 当前书签，应用后确认无待处理迁移。迁移 SQL 失败时 Wrangler 会回滚当前迁移并保留之前成功的迁移；如果迁移成功后才发现生产语义问题，按 [Schema 恢复说明](api/worker-d1-schema.md#6-发布与恢复) 停止写入并人工确认恢复。Time Travel 恢复会原地覆盖数据库，不能作为自动重试步骤。

Windows 工作树中的 D1 migration 必须保持 LF。仓库根 `.gitattributes` 固定 `cloud/LenxTool.Worker/migrations/*.sql text eol=lf`，`npm test` 会先运行 `verify:migrations` 并拒绝任何 CR 字节；不得用 `d1 execute --file` 加手工迁移账本绕过该门禁。

5. 设置 Secret：

```powershell
npx wrangler secret put TOKEN_SECRET
npx wrangler secret put GROQ_API_KEY
npx wrangler secret put DEEPSEEK_API_KEY
```

`TOKEN_SECRET` 应使用密码管理器生成至少 32 字节随机值。生产环境不要复用开发 Secret。

## P2-D 生产部署顺序

P2-16～P2-19 的生产窗口必须固定为 `D1 migration 0011 → Worker v2 → Desktop v2`，不能先让新桌面写入高级策略，也不能让旧 Worker 接收 schema v2 写入。

**当前生产检查点（2026-08-17）：** `lenx-tool` D1 已完成 0001～0011，远端无待应用迁移；Worker v2 已发布到 `https://lenx-tool-api.lenx-tool-worker.workers.dev`，当前 100% 版本 `93ea2bc7-e4bc-4976-bb2c-d429fc77dbbc`（源码基线提交 `7ce9827`，版本变化来自最终 Secret 删除），`/health` 为 200，随机 `TOKEN_SECRET` 已配置。首轮 bootstrap 暴露的 PBKDF2 生产上限不一致已修复并通过远程预览；随后首管理员条件写入、正常登录和 `/v1/me` ADMIN 身份均验证成功。D1 当前恰好 1 个启用管理员、成功 bootstrap 审计恰好 1 条；临时 `BOOTSTRAP_TOKEN` 已删除且端点恢复 404。Provider Secret、管理员策略和 Desktop v2 生产配置尚未完成；迁移恢复书签只保存在本机忽略证据中。

1. **冻结与备份：** 记录当前源码/Worker commit、远端迁移列表、ACTIVE/ALL 版本和 v1 ETag；保存 D1 Time Travel/备份书签，并确认 0011 尚未应用。
2. **迁移：** 只执行 `cloud/LenxTool.Worker/migrations/0011_integration_policy_metadata.sql`，随后重新列出远端迁移并检查三组扩展列的默认值、长度预算和旧 `allowed_hosts` 数据投影。迁移失败或列数据异常时停止，不重复应用。
3. **Worker v2：** 部署 Worker 后先验证 `/health`、登录、管理员 v2 GET/PUT、v2 `ETag`/`If-Match`、`policySchemaVersion=2`，再验证旧客户端的兼容投影；存在私网 endpoint、resource 或 loopback 端口时，旧管理端必须收到升级要求且不能清空高级字段。
4. **Desktop v2：** 只在 Worker v2 验证通过后发布桌面候选；管理员先保存 schema v2 策略，随后按 [P2-D 执行手册](plans/RSS_P2_VIEWS_INTEGRATIONS.md#p2-d-执行手册) 执行四个 provider 的真实矩阵。
5. **回滚：** 任何迁移语义、ETag、兼容投影或权限异常都先停止写入并保留证据，由发布负责人根据备份/Time Travel 决定恢复。不要通过删除迁移记录、强制覆盖 ETag 或反复重放来“修复”。

部署完成的证据至少包括：迁移列表前后、Worker 部署版本、脱敏的 v1/v2 响应头、策略版本、旧客户端升级提示和 P2-D provider 矩阵结果；凭据、完整响应正文、请求正文和 D1 数据库副本不得进入仓库或普通日志。

## 首个管理员

项目没有固定管理员密码，也不提交初始化凭据。首次部署按以下顺序执行：

1. 用密码管理器生成至少 32 个随机字符的一次性值，然后让 Wrangler 通过交互提示读取它。不要把值写在命令参数、`wrangler.toml`、脚本或聊天记录中：

```powershell
npx wrangler secret put BOOTSTRAP_TOKEN
```

`wrangler secret put` 会创建并立即部署带该 Secret 的 Worker 版本。确认 Worker 代码和 D1 迁移已部署后，在受控 PowerShell 终端执行：

```powershell
.\scripts\bootstrap-admin.ps1 -BaseUrl "https://<your-worker-host>"
```

脚本会分别安全提示管理员用户名、密码和 `BOOTSTRAP_TOKEN`；密码与 token 不接受命令行参数，不写入文件。端点仅在 D1 没有任何用户时允许一次条件插入，并发或重复执行返回 409。

密码派生使用 PBKDF2-SHA256 100,000 次，这是 Cloudflare Workers Web Crypto 生产运行时允许的最高迭代数。不得只依据本地 Miniflare 把它调高：本地环境可能接受更高值，而生产运行时会抛出 `NotSupportedError`，bootstrap 对外只返回脱敏 500。若出现该症状，应停止重试、确认 D1 仍为 0 用户并立即删除临时 `BOOTSTRAP_TOKEN`，修复和部署后再生成新的单次 token。

2. 使用新管理员账号调用正常登录端点验证密码，然后立即删除临时 Secret：

```powershell
npx wrangler secret delete BOOTSTRAP_TOKEN
```

当前 Wrangler 会为 secret put/delete 创建并立即部署新版本；删除后 `/v1/bootstrap/admin` 表现为 404。不要保留 `BOOTSTRAP_TOKEN` 作为灾难恢复入口。后续管理员通过 `/v1/admin/invites` 创建管理员或普通邀请码。

普通邀请默认 `aiDailyLimit=10`、`speechDailySeconds=600`；管理员邀请可覆盖。管理员账号不执行共享额度预留。

## API 概览

- `GET /health`：不依赖 Provider Secret 的健康检查。
- `POST /v1/auth/register`：邀请码注册。
- `POST /v1/auth/login`：登录。
- `POST /v1/auth/refresh`：refresh token 轮换。
- `POST /v1/auth/logout`：撤销当前 refresh token，重复退出安全返回 204。
- `GET /v1/me`：返回最小公开用户、角色和当日额度。
- `POST /v1/bootstrap/admin`：仅空库和临时 Secret 可用的一次性首管理员初始化。
- `POST /v1/admin/invites`：管理员创建邀请。
- `PATCH /v1/admin/users/{id}`：调整额度或禁用账号。
- `POST /v1/proxy/ai`：共享 DeepSeek 流式代理。
- `POST /v1/proxy/transcriptions`：共享 Groq 音频流式代理，需要 `x-audio-duration-seconds`。

## 并发额度

请求开始前执行条件 UPDATE，将额度记入 reserved；只有 `used + reserved + amount <= limit` 才成功。上游成功后从 reserved 转入 used，失败则释放。这个过程防止同一用户并发请求绕过每日额度。

## 安全运维

- 定期轮换 Provider Secret 和 TOKEN_SECRET；TOKEN_SECRET 轮换会使现有 access token 失效。
- 审计表只记录 actor、target、action、request ID 和 IP 哈希。
- Cloudflare 日志中禁止输出 Authorization、Cookie、请求正文或完整上游响应。
- 对 `/v1/auth/*` 保持速率限制；连续失败可在边缘防火墙增加更严格规则。
- 禁用用户时立即撤销未撤销 refresh token。
- 部署前运行：

```powershell
npm.cmd ci
npm.cmd run typecheck
npm.cmd test
npx wrangler deploy
```

Worker 测试使用 Cloudflare 官方 Vitest pool，在本地 workerd 中应用真实 D1 迁移并调用 Worker 路由；测试绑定仅含固定测试占位值。生产发布前仍需执行远端 D1 并发压测和 Provider sandbox 测试；自动测试不得使用真实共享 Key。
