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

5. 设置 Secret：

```powershell
npx wrangler secret put TOKEN_SECRET
npx wrangler secret put GROQ_API_KEY
npx wrangler secret put DEEPSEEK_API_KEY
```

`TOKEN_SECRET` 应使用密码管理器生成至少 32 字节随机值。生产环境不要复用开发 Secret。

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
