# Cloudflare Worker 与 D1 部署

## 设计目标

Worker 只保存账号、邀请码、角色、额度、用量、refresh token 哈希和基础审计；不保存早报、字幕、音视频、文件或请求正文。共享 Groq/DeepSeek Key 仅保存在 Worker Secret。

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
npx wrangler d1 migrations apply lenx-tool --remote
```

5. 设置 Secret：

```powershell
npx wrangler secret put TOKEN_SECRET
npx wrangler secret put GROQ_API_KEY
npx wrangler secret put DEEPSEEK_API_KEY
```

`TOKEN_SECRET` 应使用密码管理器生成至少 32 字节随机值。生产环境不要复用开发 Secret。

## 首个管理员

项目没有固定管理员密码。首次部署由运维人员在受控终端生成随机 salt/hash，或使用一次性 bootstrap 脚本向 D1 插入首个 admin。完成后立即销毁明文密码与临时脚本。之后管理员通过 `/v1/admin/invites` 创建管理员或普通邀请码。

普通邀请默认 `aiDailyLimit=10`、`speechDailySeconds=600`；管理员邀请可覆盖。管理员账号不执行共享额度预留。

## API 概览

- `GET /health`：不依赖 Provider Secret 的健康检查。
- `POST /v1/auth/register`：邀请码注册。
- `POST /v1/auth/login`：登录。
- `POST /v1/auth/refresh`：refresh token 轮换。
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
npm.cmd install
npm.cmd run typecheck
npm.cmd test -- --run
npx wrangler deploy
```

生产发布前应增加 Miniflare/D1 集成测试和实际 Provider sandbox 测试；不要在单元测试中使用真实共享 Key。
