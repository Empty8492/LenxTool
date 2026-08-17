# Worker v1 账号、共享订阅目录与发现 API 契约

状态：v1 基线已冻结；P0 身份/目录、P1 AI 策略与受限自动化规则、DISC-02 已知目录发现、P2 智能视图与外部集成策略均已实现
最后核对：2026-08-17
适用范围：LenxTool 桌面端与 `cloud/LenxTool.Worker` 之间的账号、会话、管理员策展目录、已知目录发现、AI 策略和自动化规则接口

本文是 P0/P1 Worker 契约的真相源。实现顺序和验收见 [P0 详细计划](../plans/RSS_P0_ADMIN_CATALOG.md)与 [P1 详细计划](../plans/RSS_P1_READING_INTELLIGENCE.md)，安全边界见 [威胁模型](../THREAT_MODEL.md)，云端只保存共享目录配置的决策见 [ADR-001](../decisions/ADR-001-admin-curated-rss.md)。

## 1. 兼容性与通用约定

- 基础路径固定为 `/v1`。v1 内只做向后兼容的字段新增；删除字段、改变字段类型/语义或收紧既有枚举必须另立迁移方案。
- 只接受 HTTPS、UTF-8 JSON。账号成功响应、写入响应和所有错误响应均发送 `Cache-Control: no-store`。目录 GET 使用 `Cache-Control: private, no-cache, no-transform`，发现 GET 使用 `Cache-Control: private, max-age=60, no-transform`；自动化规则、智能视图和外部集成策略快照使用 `Cache-Control: no-store, no-transform`。所有携带强 ETag 的 200/304 响应都必须保留 `no-transform`，防止 Cloudflare 边缘压缩修改表示并把强校验器弱化；认证快照同时发送各自的 `Vary` 维度，禁止公共代理跨身份或跨 schema 共享响应。
- 字段和查询参数使用 `camelCase`；枚举值使用 `UPPER_SNAKE_CASE`；时间使用 UTC ISO 8601，例如 `2026-07-21T08:30:00Z`。
- 服务端生成的账号、分类和 Feed ID 是不透明 UUID。客户端只能原样保存和回传，不能从 ID 推断排序或时间。
- 客户端可发送 `X-Request-Id`，长度 1～128，只允许可打印 ASCII；服务端不信任其唯一性。服务端始终在 `X-Request-Id` 响应头和错误体 `requestId` 中返回最终请求 ID。
- 除登录和刷新成功响应外，任何响应都不得返回 access/refresh token 明文。任何响应都不得返回密码摘要、邀请码摘要、令牌摘要、共享服务商 Key、Feed/文章正文、字幕、文件名、完整本地路径或本机状态。
- JSON 对象出现未知字段时返回 `400 VALIDATION_ERROR`，防止拼写错误被静默忽略。响应以后可以增加可选字段；桌面端必须忽略未知响应字段。

### 1.1 认证与角色

`Authorization: Bearer <access-token>` 用于所有非匿名端点。角色只取服务端已验证令牌对应的 D1 用户记录，客户端传入的 `role`、本地 `IsAdmin` 或隐藏按钮都不参与授权。

| 调用状态 | 结果 |
|---|---|
| 未提供 Bearer | `401 AUTH_REQUIRED` |
| access token 无效、过期或已撤销 | `401 TOKEN_INVALID` 或 `401 TOKEN_EXPIRED` |
| 用户已禁用 | `403 ACCOUNT_DISABLED` |
| 已认证 user 调用 admin 端点 | `403 ADMIN_REQUIRED` |
| 已认证 admin 调用 admin 端点 | 进入请求校验和业务处理 |

因此，“未登录”永远不是 403，“非管理员”永远不是 401。授权必须在读取幂等记录、资源是否存在或当前目录版本之前完成，避免向未授权用户泄露状态。

### 1.2 错误结构

所有非 2xx/304 响应使用同一结构，可直接映射到桌面端 [`AppError`](../../src/LenxTool.Core/Errors/AppError.cs)：

```json
{
  "error": {
    "code": "CATALOG_VERSION_CONFLICT",
    "title": "共享目录已更新",
    "userMessage": "其他管理员已经修改了共享目录。",
    "suggestion": "同步最新目录后重新应用本次修改。",
    "provider": "LenxTool Worker",
    "requestId": "018f...",
    "retryAfterSeconds": null,
    "isRetryable": true,
    "details": {
      "currentCatalogVersion": 42
    }
  }
}
```

- `code` 是稳定的机器码；UI 不得依赖 `title`、`userMessage` 或 `suggestion` 的原文。
- `details` 只能包含该错误明示的安全字段，例如当前目录版本、失败批次序号或字段名。不得返回 SQL、堆栈、内部主机、哈希、原始上游响应或请求正文。
- HTTP `Retry-After` 秒值同时映射到 `retryAfterSeconds`；无值时为 `null`。
- 桌面端将 `code`、HTTP 状态、其余公开字段映射为 `AppError`；服务端诊断细节只进入脱敏日志，不进入 `TechnicalDetails` 响应。

通用状态与错误码：

| HTTP | 错误码 | 语义 |
|---:|---|---|
| 400 | `INVALID_JSON`、`VALIDATION_ERROR` | JSON 无效、未知字段、字段类型/范围/组合不合法 |
| 401 | `AUTH_REQUIRED`、`CREDENTIALS_INVALID`、`TOKEN_INVALID`、`TOKEN_EXPIRED`、`BOOTSTRAP_AUTH_INVALID` | 未认证或凭据不可用 |
| 403 | `ACCOUNT_DISABLED`、`ADMIN_REQUIRED` | 已认证但账号或角色无权执行 |
| 404 | `RESOURCE_NOT_FOUND` | 授权通过后资源不存在 |
| 409 | `CATALOG_VERSION_CONFLICT`、`CATALOG_VERSION_AHEAD`、`IDEMPOTENCY_KEY_REUSED`、`CATEGORY_NOT_EMPTY`、`DUPLICATE_CATEGORY`、`DUPLICATE_FEED`、`CATALOG_CAPACITY_EXCEEDED`、`BATCH_OPERATION_FAILED`、`BOOTSTRAP_ALREADY_COMPLETED` | 当前状态与请求前置条件冲突 |
| 413 | `PAYLOAD_TOO_LARGE` | 请求体超过端点上限 |
| 429 | `RATE_LIMITED` | 速率限制；可能带 `Retry-After` |
| 500 | `INTERNAL_ERROR` | 未分类服务端错误，不暴露内部细节 |
| 503 | `SERVICE_UNAVAILABLE` | D1 或必要依赖暂时不可用 |

### 1.3 目录版本、ETag 与分页

- D1 保存单调递增的 JSON 安全非负整数 `catalogVersion`；空目录为 0，上限为 `2^53-1`。SQLite INTEGER 可容纳更大值，但 Worker/JSON 无法在该范围外保证精确比较，因此应用层不得越过此上限。
- 每个成功的单项目录写入使版本增加 1；一个成功批次无论含多少操作只增加 1。校验失败、授权失败、冲突、事务回滚和幂等重放都不增加版本。
- 分类/Feed 的 `version` 表示该资源最后一次改变时的全局目录版本。
- 目录响应 ETag 为强 ETag：`"catalog-active-42"` 或 `"catalog-all-42"`。客户端必须把目录内容和版本在同一 SQLite 事务中原子替换。
- v1 目录是有界的原子快照，不分页：最多 200 个未删除分类、5,000 个未删除 Feed，序列化响应不超过 10 MiB。达到上限的新增返回 `409 CATALOG_CAPACITY_EXCEEDED`。避免分页是为了不在并发写入时组合出跨版本目录。
- 已知目录发现采用绑定查询和 scope 的不透明 `cursor`，`pageSize` 默认 20、范围 1～50；不得使用偏移量分页。完整目录快照仍不分页。

### 1.4 管理员目录写入的并发与幂等

每个 `/v1/admin/feed-*` 写端点必须同时携带：

```http
If-Match: "catalog-all-41"
Idempotency-Key: 018f87d4-0f7e-7ad0-9c06-b285e52e7664
```

- `If-Match` 必须精确匹配当前 `catalogVersion`，否则返回 `409 CATALOG_VERSION_CONFLICT`，`details.currentCatalogVersion` 给出安全的最新版本。
- `Idempotency-Key` 长度 16～128，只允许 `A-Z a-z 0-9 . _ : -`；由客户端为一次用户意图生成，重试必须复用。
- 幂等作用域是“操作者账号 + HTTP 方法 + 规范化路径 + key”。服务端保存请求规范化摘要、原始成功状态和成功响应至少 24 小时，不保存原始请求体。
- 授权和输入大小检查之后，先查幂等记录，再检查 `If-Match`。同 key、同请求摘要返回原成功响应，不重复写入、不增加版本、不重复业务审计；同 key、不同请求摘要返回 `409 IDEMPOTENCY_KEY_REUSED`。
- 请求摘要包含请求体、影响语义的查询参数和 `If-Match`。服务端错误不写成功幂等记录；客户端可用同 key 重试可重试错误。
- 单项和批量目录操作都在 D1 事务中完成“版本比较、业务写入、版本增加、审计、幂等结果”。
- user 在幂等/版本检查前即得到 `403 ADMIN_REQUIRED`，不会得知 key、资源或目录版本是否存在。

账号写端点不改变 `catalogVersion`：登录可重复签发新会话；refresh token 只允许使用一次；logout 对同一已撤销 refresh token返回相同的 204。

## 2. 公开 DTO

### 2.1 用户与额度

```json
{
  "user": {
    "id": "0e7468a4-...",
    "username": "reader",
    "role": "USER"
  },
  "quota": {
    "date": "2026-07-21",
    "ai": { "limit": 100, "used": 12, "reserved": 0, "remaining": 88 },
    "speechSeconds": { "limit": 3600, "used": 45, "reserved": 0, "remaining": 3555 }
  }
}
```

- `role` 仅为 `USER` 或 `ADMIN`。
- 额度整数均为 0～2,147,483,647；`remaining = max(0, limit - used - reserved)`。
- 日期按 Worker 的额度结算日（UTC）返回。

### 2.2 分类

```json
{
  "id": "4a5feea7-...",
  "name": "技术",
  "sortOrder": 100,
  "isEnabled": true,
  "aiPolicy": {
    "manualSummary": "INHERIT",
    "autoSummary": "INHERIT",
    "autoTranslation": "INHERIT",
    "translationTargetLanguage": null,
    "dailyEntryLimit": null,
    "maxConcurrency": null
  },
  "version": 42,
  "createdAt": "2026-07-21T08:30:00Z",
  "updatedAt": "2026-07-21T08:30:00Z"
}
```

### 2.3 Feed

```json
{
  "id": "d889d0c8-...",
  "originalUrl": "https://example.com/feed.xml",
  "normalizedUrl": "https://example.com/feed.xml",
  "displayName": "Example",
  "siteUrl": "https://example.com/",
  "categoryId": "4a5feea7-...",
  "viewKind": "ARTICLE",
  "isViewKindExplicit": false,
  "fullTextPolicy": "NONE",
  "refreshIntervalMinutes": 60,
  "sortOrder": 100,
  "isEnabled": true,
  "aiPolicy": {
    "manualSummary": "INHERIT",
    "autoSummary": "INHERIT",
    "autoTranslation": "INHERIT",
    "translationTargetLanguage": null,
    "dailyEntryLimit": null,
    "maxConcurrency": null
  },
  "version": 42,
  "createdAt": "2026-07-21T08:30:00Z",
  "updatedAt": "2026-07-21T08:30:00Z"
}
```

- `viewKind` 的 v1 值为 `ARTICLE`、`PICTURE`、`AUDIO`、`VIDEO`、`NOTIFICATION`；`isViewKindExplicit=false` 表示由条目媒体自动识别，`true` 表示强制采用 `viewKind`，因此强制 `ARTICLE` 不再与自动模式混淆。缺失该布尔字段按 `false` 兼容。
- `aiPolicy` 是分类或 Feed 对全局/上级策略的覆盖：三个开关只能是 `INHERIT`、`ENABLED`、`DISABLED`；目标语言可为 `zh-Hans`、`en`、`ja`、`ko` 或 null；每日条目上限可为 1～1,000 或 null，并发上限可为 1～4 或 null。null/`INHERIT` 表示继续向上解析，不表示自动启用。
- 管理端只提交 `originalUrl`；`normalizedUrl` 由服务端生成并用于重复检测。目录写路由只做语法、方案和规范化校验，不发起网络请求。DNS、固定地址连接、重定向、响应/解压大小、MIME 和 XML 安全验证由桌面 P0-11 发现服务执行；P0-15 管理界面在提交写 API 前调用该服务。
- 普通目录响应只含未删除、已启用且分类已启用的 Feed；它不含抓取结果、正文、健康详情或用户私人状态。

### 2.4 已知目录发现页

```json
{
  "catalogVersion": 42,
  "query": "技术",
  "scope": "ACTIVE",
  "items": [
    {
      "normalizedFeedUrl": "https://example.com/feed.xml",
      "title": "技术日报",
      "siteUrl": "https://example.com/",
      "documentKind": null,
      "lastUpdatedAt": "2026-07-27T08:30:00Z",
      "health": "UNKNOWN",
      "evidence": [
        {
          "sourceId": "worker:known-catalog",
          "sourceKind": "KNOWN_CATALOG",
          "matchKind": "KEYWORD",
          "confidence": "MEDIUM"
        }
      ],
      "warnings": [],
      "catalog": {
        "feedId": "d889d0c8-...",
        "categoryId": "4a5feea7-...",
        "categoryName": "技术",
        "viewKind": "ARTICLE",
        "isEnabled": true
      }
    }
  ],
  "pagination": {
    "pageSize": 20,
    "totalItems": 1,
    "nextCursor": null
  }
}
```

- `title` 是共享目录显示名，`lastUpdatedAt` 是目录元数据更新时间；DISC-02 不把它们伪装成最新文章标题或发布时间。
- 已知目录没有经过桌面端内容探测，因此 `documentKind` 为 null、`health` 为 `UNKNOWN`、`warnings` 为空。后续发现协调器可与其他来源合并，但不得改变本端点的字段语义。
- `catalog.isEnabled` 表示 Feed 及其分类当前是否整体可用。`ACTIVE` 只返回 true；admin 的 `ALL` 也可返回 false。
- `matchKind` 为 `EXACT_FEED_URL`、`EXACT_SITE_URL`、`EXACT_TITLE` 或 `KEYWORD`；对应置信度分别为 `EXACT`、`EXACT`、`HIGH`、`MEDIUM`。

## 3. 端点总表

“审计动作”为 D1 安全审计事件名；“无”表示仅有脱敏运维访问日志，不写逐次 D1 审计。

| 端点 | 角色 | 输入上限 | 成功响应 | 主要业务错误 | 审计动作 |
|---|---|---|---|---|---|
| `POST /v1/auth/login` | anonymous | JSON 4 KiB；用户名 3～40；密码 1～128 | 200 `AuthSession` | `CREDENTIALS_INVALID`、`ACCOUNT_DISABLED`、`RATE_LIMITED` | `auth.login.succeeded` / `auth.login.failed` |
| `POST /v1/auth/refresh` | anonymous | JSON 4 KiB；token 32～512 | 200 `TokenPair` | `TOKEN_INVALID`、`TOKEN_EXPIRED`、`ACCOUNT_DISABLED` | `auth.refresh.succeeded` / `auth.refresh.failed` |
| `POST /v1/auth/logout` | user/admin | JSON 4 KiB；token 32～512 | 204，无响应体 | 通用认证错误 | `auth.logout` |
| `GET /v1/me` | user/admin | 无请求体、无查询参数 | 200 `MeResponse` | 通用认证错误 | 无 |
| `POST /v1/bootstrap/admin` | 临时 bootstrap secret，且 D1 无用户 | JSON 8 KiB；用户名 3～40；密码 12～128；secret ≥32 | 201 `PublicUser` | `BOOTSTRAP_AUTH_INVALID`、`BOOTSTRAP_ALREADY_COMPLETED` | `bootstrap.admin.created` / `bootstrap.admin.failed` |
| `GET /v1/feeds/catalog` | user/admin | URL 2 KiB；`afterVersion` 0～2^53-1；`scope` 枚举 | 200 `CatalogSnapshot` 或 304 | `CATALOG_VERSION_AHEAD`、`ADMIN_REQUIRED` | 无 |
| `GET /v1/feeds/discoveries` | user/admin | URL 2 KiB；`query` 1～200；`pageSize` 1～50；不透明 `cursor` | 200 `FeedDiscoveryPage` 或 304 | `VALIDATION_ERROR`、`ADMIN_REQUIRED`、`RATE_LIMITED` | 无 |
| `POST /v1/admin/feed-categories` | admin | JSON 8 KiB；名称 1～80 | 201 `CatalogMutation<Category>` | 版本/幂等冲突、`DUPLICATE_CATEGORY`、容量超限 | `feed_category.created` |
| `PATCH /v1/admin/feed-categories/{id}` | admin | JSON 8 KiB；ID 36；名称 1～80 | 200 `CatalogMutation<Category>` | 版本/幂等冲突、`RESOURCE_NOT_FOUND`、重复分类 | `feed_category.updated` |
| `DELETE /v1/admin/feed-categories/{id}` | admin | 无请求体；ID 36 | 200 `CatalogDeletion` | 版本/幂等冲突、`RESOURCE_NOT_FOUND`、`CATEGORY_NOT_EMPTY` | `feed_category.deleted` |
| `POST /v1/admin/feeds` | admin | JSON 16 KiB；URL 2,048；名称 1～160 | 201 `CatalogMutation<Feed>` | 版本/幂等冲突、`DUPLICATE_FEED`、`RESOURCE_NOT_FOUND`、容量超限 | `feed.created` |
| `PATCH /v1/admin/feeds/{id}` | admin | JSON 16 KiB；ID 36 | 200 `CatalogMutation<Feed>` | 版本/幂等冲突、`RESOURCE_NOT_FOUND`、`DUPLICATE_FEED` | `feed.updated` |
| `DELETE /v1/admin/feeds/{id}` | admin | 无请求体；ID 36 | 200 `CatalogDeletion` | 版本/幂等冲突、`RESOURCE_NOT_FOUND` | `feed.deleted` |
| `POST /v1/admin/feed-catalog-batches` | admin | JSON 256 KiB；1～100 个操作 | 200 `CatalogBatchResult` | 版本/幂等冲突、`BATCH_OPERATION_FAILED` | `feed_catalog.batch` + 各操作动作 |
| `GET /v1/automation-rules` | user/admin | URL 2 KiB；`afterVersion` 0～2^53-1；`scope` 枚举 | 200 `AutomationRulesSnapshot` 或 304 | `AUTOMATION_VERSION_AHEAD`、`ADMIN_REQUIRED` | 无 |
| `POST /v1/admin/automation-rules` | admin | JSON 64 KiB；规则/条件/动作受限 | 201 `AutomationMutation` | `AUTOMATION_VERSION_CONFLICT`、`AUTOMATION_RULE_LIMIT_REACHED` | `automation_rule.created` |
| `PATCH /v1/admin/automation-rules/{id}` | admin | JSON 64 KiB；ID 36；规则/条件/动作受限 | 200 `AutomationMutation` | `AUTOMATION_VERSION_CONFLICT`、`RESOURCE_NOT_FOUND` | `automation_rule.updated` |
| `GET /v1/integration-policies` | user/admin | URL 2 KiB；`afterVersion` 0～2^53-1；`scope` 枚举；新客户端发送策略 schema 头 `2` | 200 `IntegrationPolicySnapshot` 或 304 | `INTEGRATION_POLICY_VERSION_AHEAD`、`INTEGRATION_POLICY_SCHEMA_UPGRADE_REQUIRED`、`ADMIN_REQUIRED` | 无 |
| `PUT /v1/admin/integration-policies` | admin | JSON 64 KiB；`policySchemaVersion=2`；最多 9 种策略及有界 endpoint/resource 列表 | 200 `IntegrationPolicyMutation` | `INTEGRATION_POLICY_VERSION_CONFLICT`、`INTEGRATION_POLICY_SCHEMA_UPGRADE_REQUIRED`、幂等/校验错误 | `integration_policy.replaced` |

所有端点还可能返回第 1.2 节的通用校验、认证、限流和服务不可用错误。

## 4. 账号与会话端点

### 4.1 `POST /v1/auth/login`

请求：

```json
{ "username": "reader", "password": "correct horse battery staple" }
```

- `username` NFKC 规范化后为 3～40 个 Unicode 字符，只允许 Unicode 字母、组合标记、数字、`.`、`_` 和 `-`；唯一性比较使用规范化后的小写值。
- `password` 为 1～128 个 Unicode 字符；服务端不得在规范化前后记录它。

成功：

```json
{
  "user": { "id": "0e7468a4-...", "username": "reader", "role": "USER" },
  "quota": {
    "date": "2026-07-21",
    "ai": { "limit": 100, "used": 12, "reserved": 0, "remaining": 88 },
    "speechSeconds": { "limit": 3600, "used": 45, "reserved": 0, "remaining": 3555 }
  },
  "accessToken": "<one-time-response-value>",
  "refreshToken": "<one-time-response-value>",
  "expiresInSeconds": 900
}
```

登录失败统一用 `CREDENTIALS_INVALID`，不得透露用户名是否存在。成功审计记录 actor、request ID 和脱敏 IP 哈希；失败审计不含密码或原始用户名，可记录规范化用户名的带服务端密钥摘要和原因类别。

### 4.2 `POST /v1/auth/refresh`

请求：

```json
{ "refreshToken": "<refresh-token>" }
```

成功：

```json
{
  "accessToken": "<new-access-token>",
  "refreshToken": "<new-refresh-token>",
  "expiresInSeconds": 900
}
```

成功必须在同一 D1 batch 中条件撤销旧 token 摘要、记录替代 token ID、保存新 token 摘要并写成功审计。旧 token 的任何后续使用均返回 `401 TOKEN_INVALID`，不会再次返回已经签发的明文 token。审计只记录 token 记录 ID 等元数据，不记录 token 或摘要。

### 4.3 `POST /v1/auth/logout`

请求携带 Bearer 和当前 refresh token：

```json
{ "refreshToken": "<refresh-token>" }
```

服务端撤销属于当前账号的 refresh token 并返回 204。相同账号对同一已撤销 token 重试仍返回 204，避免产生 token 状态探针；其他账号的 token 或随机值也不向调用者暴露归属信息。access token 在短 TTL 结束前可继续通过签名验证，因此所有认证端点仍必须实时检查账号禁用状态。

### 4.4 `GET /v1/me`

返回第 2.1 节的 `user`、`quota`，并增加：

```json
{ "serverTime": "2026-07-21T08:30:00Z" }
```

响应不返回 token、token ID、密码/邀请码信息、D1 内部字段或其他用户信息。

### 4.5 `POST /v1/bootstrap/admin`

该端点只用于空 D1 的首次部署，不是日常注册入口。运维人员先用 Wrangler Secret 临时设置至少 32 个字符的 `BOOTSTRAP_TOKEN`，再在受控终端运行 [`bootstrap-admin.ps1`](../../cloud/LenxTool.Worker/scripts/bootstrap-admin.ps1)。脚本通过安全提示读取管理员密码和 bootstrap token，不接受这两项命令行参数。

请求：

```http
Authorization: Bootstrap <temporary-secret>
Content-Type: application/json
```

```json
{ "username": "owner", "password": "<12-to-128-characters>" }
```

- 服务端先验证 bootstrap secret，再读取账号状态；无效 secret 返回 `401 BOOTSTRAP_AUTH_INVALID`，不透露 D1 是否已有用户。
- 插入使用“D1 仍无任何用户”的条件写入。并发或重复执行最多一个请求成功，其他请求返回 `409 BOOTSTRAP_ALREADY_COMPLETED`。
- 密码复用正常账号 PBKDF2-SHA256 派生流程，迭代次数固定为 Cloudflare Workers Web Crypto 生产运行时允许的上限 100,000；本地运行时可能接受更高值，因此安全契约测试必须锁定该上限。响应和审计不包含密码、secret、salt 或 hash。
- 201 只返回 `{ "user": PublicUser }`，不签发会话。运维人员随后通过正常登录验证。
- 成功后必须立即执行 `wrangler secret delete BOOTSTRAP_TOKEN`。secret 缺失或短于 32 个字符时，该端点表现为 404。

## 5. 目录读取

### 5.1 `GET /v1/feeds/catalog?afterVersion=41&scope=ACTIVE`

- `afterVersion` 可省略；省略或 0 表示需要完整快照，即使当前空目录版本也是 0。
- `scope` 默认为 `ACTIVE`。user 只能请求 `ACTIVE`；admin 可请求 `ACTIVE` 或 `ALL`。`ALL` 包含未删除的停用分类和 Feed，用于管理界面。
- 若 `afterVersion` 大于 0 且等于当前版本，返回 304、ETag 和 `X-Request-Id`，无响应体。
- 若 `afterVersion` 小于当前版本，返回当前完整快照；v1 不返回增量补丁。
- 若 `afterVersion` 大于当前版本，返回 `409 CATALOG_VERSION_AHEAD`，防止用服务器旧状态覆盖本地更新版本。
- 客户端也可发送 `If-None-Match`。它必须与 `scope` 和 `afterVersion` 一致；矛盾的条件返回 `400 VALIDATION_ERROR`。
- `generatedAt` 是该目录版本写入 `feed_catalog_state` 的 UTC 时间，不是每次请求的当前时间；因此同一版本和 scope 的 JSON 序列化保持稳定。

200 响应：

```json
{
  "catalogVersion": 42,
  "scope": "ACTIVE",
  "generatedAt": "2026-07-21T08:30:00Z",
  "aiPolicyDefaults": {
    "manualSummary": "ENABLED",
    "autoSummary": "DISABLED",
    "autoTranslation": "DISABLED",
    "translationTargetLanguage": "zh-Hans",
    "dailyEntryLimit": 20,
    "maxConcurrency": 1
  },
  "categories": [],
  "feeds": []
}
```

策略解析顺序为 Feed 覆盖 → 所属分类覆盖 → `aiPolicyDefaults`。自动摘要和自动翻译在全局默认中均关闭；Worker 只发布版本化配置，正文、摘要和译文不上传 D1，实际计算由桌面端在本机触发。

排序是契约的一部分：分类按 `sortOrder`、`name`、`id`；Feed 按分类顺序、`sortOrder`、`displayName`、`id`，未分类 Feed 排在已分类 Feed 之后。即使客户端重新排序，也不能依赖 D1 的未指定行顺序。

### 5.2 `GET /v1/feeds/discoveries?query=技术&pageSize=20&scope=ACTIVE`

- `query` 必填；NFKC 规范化、去除首尾空白并合并空白后须为 1～200 个 Unicode 字符。控制/格式字符、重复参数、未知参数以及作为通配符的 `%`/`_` 都不会绕过校验或参数化查询；后两者按普通字面字符搜索。
- `scope` 默认为 `ACTIVE`。user 只能请求 `ACTIVE`；admin 可请求 `ALL`，但目录写入仍只能走第 6～8 节的管理员端点。此发现路径不接受 POST/PATCH/DELETE。
- `pageSize` 默认 20、范围 1～50；`cursor` 最长 1,024 个 base64url 字符，绑定规范化后的查询与 scope，客户端只能原样回传。游标分页按“匹配等级降序、目录更新时间降序、Feed ID 升序”继续，不使用偏移量。
- 排名依次为：精确规范 Feed URL、精确站点 URL、精确标题、标题前缀、标题包含、精确分类、分类包含、其他 URL 包含。同等级使用上述稳定次序；响应以类型化 `evidence` 说明匹配，不返回内部数值分数。
- `totalItems` 是当前查询/scope 的全部匹配数，与当前游标位置无关。空结果返回 200、空 `items`、`totalItems=0` 和 `nextCursor=null`。
- 响应发送 `Cache-Control: private, max-age=60`、`Vary: Authorization` 和绑定目录版本、查询、scope、页大小、游标的强 ETag。完全匹配的 `If-None-Match` 返回 304。
- 每个已认证用户每 UTC 分钟最多 60 次发现 GET；第 61 次返回 `429 RATE_LIMITED`、`Retry-After: 60` 和 `retryAfterSeconds=60`。限流计数不改变目录版本，也不写业务审计。
- 响应只来自发现字段白名单：规范 Feed URL、显示名、站点 URL、分类公开元数据、视图类型、启用状态和目录更新时间。原始管理员 URL、规范名内部列、删除时间、AI 策略、文章/摘要/正文、token、密码和用户私人状态均不得返回。

## 6. 管理员分类端点

以下三个端点均要求第 1.4 节的 `If-Match` 和 `Idempotency-Key`。

### 6.1 `POST /v1/admin/feed-categories`

```json
{ "name": "技术", "sortOrder": 100, "isEnabled": true }
```

- `name` 去除首尾空白后 1～80 个 Unicode 字符；同一未删除分类内按 NFKC + Unicode case fold 唯一。
- `sortOrder` 为 0～1,000,000；`isEnabled` 必须是布尔值。
- 返回 201：`{ "catalogVersion": 42, "category": { ... } }`。

### 6.2 `PATCH /v1/admin/feed-categories/{id}`

```json
{ "name": "工程", "sortOrder": 200, "isEnabled": false }
```

字段均可选，但至少出现一个。停用分类会使其 Feed 从 `ACTIVE` 目录消失；不会删除 Feed 或本地文章。返回 200 `CatalogMutation<Category>`。

### 6.3 `DELETE /v1/admin/feed-categories/{id}`

无请求体。只有不存在未删除 Feed 的分类可以软删除，否则返回 `409 CATEGORY_NOT_EMPTY`。成功：

```json
{ "catalogVersion": 42, "deletedId": "4a5feea7-...", "resourceType": "FEED_CATEGORY" }
```

重复使用原幂等 key 返回原 200；新 key 删除已删除资源返回 `404 RESOURCE_NOT_FOUND`。

## 7. 管理员 Feed 端点

以下三个端点均要求第 1.4 节的 `If-Match` 和 `Idempotency-Key`。

### 7.1 `POST /v1/admin/feeds`

```json
{
  "originalUrl": "https://example.com/feed.xml",
  "displayName": "Example",
  "siteUrl": "https://example.com/",
  "categoryId": "4a5feea7-...",
  "viewKind": "ARTICLE",
  "isViewKindExplicit": false,
  "refreshIntervalMinutes": 60,
  "sortOrder": 100,
  "isEnabled": true
}
```

- `originalUrl` 必填，绝对 HTTPS URL，最长 2,048；默认拒绝userinfo、fragment、非 443 显式端口和控制字符。未来只有显式可信主机策略可允许 HTTP。
- `displayName` 去除首尾空白后 1～160 个字符；`siteUrl` 可为 null，否则为最长 2,048 的绝对 HTTPS URL。
- `categoryId` 可为 null；非 null 时必须指向未删除分类。不能启用位于停用分类下的 Feed。
- `viewKind` 必须是第 2.3 节枚举；`isViewKindExplicit` 必须是布尔值；`refreshIntervalMinutes` 为 5～1,440；`sortOrder` 为 0～1,000,000。
- 兼容旧 v1 客户端：显式提交 `isViewKindExplicit` 时严格采用该布尔值；字段缺失但提交了 `viewKind` 时按显式覆盖处理。创建时两者都缺失表示自动 `ARTICLE`；PATCH 两者都缺失则保持原状态。JSON `null` 不是合法布尔值。
- `normalizedUrl` 在所有未删除 Feed 中唯一；冲突返回 `409 DUPLICATE_FEED`，不返回冲突 Feed 的私有信息。
- 返回 201：`{ "catalogVersion": 42, "feed": { ... } }`。

### 7.2 `PATCH /v1/admin/feeds/{id}`

请求字段与创建相同且均可选，但至少出现一个。`normalizedUrl`、ID、版本和时间字段不可写。修改 URL 重新执行纯语法规范化与重复检测，不在此路由抓取网络。返回 200 `CatalogMutation<Feed>`。

### 7.3 `DELETE /v1/admin/feeds/{id}`

无请求体。执行软删除并立刻从所有目录 scope 隐藏；不会远程删除客户端已缓存文章。本地保留期由桌面端策略处理。成功：

```json
{ "catalogVersion": 42, "deletedId": "d889d0c8-...", "resourceType": "FEED" }
```

## 8. 原子批量目录更新

### 8.1 `POST /v1/admin/feed-catalog-batches`

请求：

```json
{
  "operations": [
    {
      "operationId": "category-1",
      "type": "CREATE_CATEGORY",
      "input": { "name": "技术", "sortOrder": 100, "isEnabled": true }
    },
    {
      "operationId": "feed-1",
      "type": "CREATE_FEED",
      "input": {
        "originalUrl": "https://example.com/feed.xml",
        "displayName": "Example",
        "categoryRef": { "operationId": "category-1" },
        "viewKind": "ARTICLE",
        "isViewKindExplicit": false,
        "refreshIntervalMinutes": 60,
        "sortOrder": 100,
        "isEnabled": true
      }
    }
  ]
}
```

- `operations` 含 1～100 项；`operationId` 在批次内唯一，长度 1～64，只允许 `A-Z a-z 0-9 . _ : -`。
- `type` 为 `CREATE_CATEGORY`、`PATCH_CATEGORY`、`DELETE_CATEGORY`、`CREATE_FEED`、`PATCH_FEED` 或 `DELETE_FEED`。各 `input` 复用单项端点上限；PATCH/DELETE 同时提供对应 `categoryId` 或 `feedId`。
- 创建 Feed 可用 `categoryId` 引用既有分类，或用 `categoryRef.operationId` 引用同批次更早创建的分类，二者只能出现一个。
- 操作严格按数组顺序执行，但整个批次原子提交。任一操作失败则无目录、版本、审计或幂等成功记录落库，返回 `409 BATCH_OPERATION_FAILED`，`details` 只含 `operationIndex`、`operationId` 和内层稳定错误码。
- 成功只增加一次 `catalogVersion`；所有新建/变更资源的 `version` 都等于该版本。响应按输入顺序返回结果：

```json
{
  "catalogVersion": 42,
  "results": [
    { "operationId": "category-1", "resourceType": "FEED_CATEGORY", "resourceId": "4a5feea7-..." },
    { "operationId": "feed-1", "resourceType": "FEED", "resourceId": "d889d0c8-..." }
  ]
}
```

成功事务写一个 `feed_catalog.batch` 父审计事件和每项对应动作，均共享 request ID、批次幂等 key 和最终版本；审计不保存 Feed 正文、请求体或 token。

## 9. 审计与响应字段白名单

每个业务审计事件只允许：

- 事件 ID、UTC 时间、actor user ID、target type/ID、稳定 action、请求 ID。
- 目录版本、批次操作数量、结果类别、脱敏 IP 哈希和必要的安全原因码。
- 幂等 key 的带服务端密钥摘要；不得保存原始 key 或规范化请求摘要以外的正文。

目录和账号响应采用本文 DTO 白名单。特别禁止：

- `password_hash`、`invite_hash`、`token_hash`、JWT 签名材料、Secret Binding 和任何 API Key。
- Feed XML/Atom/HTML 正文、文章标题/正文、抓取响应、Cookie、Authorization 和 DNS/内网诊断。
- Windows 用户名、设备名、文件名、完整路径、SQLite 行 ID、DPAPI 数据和本地阅读/收藏状态。

`originalUrl`、`normalizedUrl`、`siteUrl`、分类和显示名属于管理员发布的共享配置，可以出现在目录；它们仍按不可信数据处理，客户端显示时编码且不会自动导航。

## 10. 当前实现对照

2026-07-27 对照 [Worker 路由](../../cloud/LenxTool.Worker/src/index.ts)、D1 迁移和桌面客户端后的实现状态如下。此表用于防止把“契约已冻结”误写成“功能已实现”。

| 契约项 | 当前实现 | 后续归属 |
|---|---|---|
| 登录、`GET /v1/me`、logout | 已按公开用户/额度 DTO 实现；logout 幂等撤销 refresh token | P0-02 已完成 |
| refresh 轮换 | 条件撤销与新 token 写入在同一 D1 batch；并发重放只有一个成功者 | P0-02 已完成 |
| 首管理员初始化 | 临时 Secret + 空库条件写入 + 受控终端脚本；重复执行安全失败 | P0-02 已完成 |
| 统一 `AppError` 可映射错误体 | 身份及现有路由已统一，并补 401/403、请求 ID 与可重试字段测试 | P0-02 已完成，后续端点复用 |
| 分类/Feed D1 表与全局版本 | 已由 [0002 迁移与 schema 文档](worker-d1-schema.md) 实现；活动唯一索引、范围约束和分类 `RESTRICT` 外键已有迁移测试 | P0-03 已完成 |
| 分类/Feed 管理员路由、幂等记录 | 已实现 6 个单项 CRUD 路由，以及 1～100 项、单版本增量、带逐项结果和父/子审计的原子批量路由 | P0-04/P0-16 已完成 |
| 只读目录、ETag、RBAC/版本并发测试 | 已实现 ACTIVE/ALL 原子快照、强 ETag、304、超前版本拒绝和权限/排序/缓存测试 | P0-05 已完成 |
| 桌面账号/目录 DTO | 已实现安全会话、ACTIVE/ALL 同步、单项管理客户端、批量客户端和 OPML 工作流 | P0-06～P0-10/P0-15/P0-16 已完成 |
| AI 策略与自动化规则 | 分类/Feed 策略字段、ACTIVE/ALL 规则快照、POST/PATCH、独立版本/ETag、幂等、不可变版本和桌面管理/同步均已实现 | P1-13～P1-16 已完成 |
| 外部集成共享策略 | ACTIVE/ALL 快照、整组 PUT、独立版本/ETag、幂等、不可变版本和桌面管理/同步均已实现；个人目标与凭据不进入 Worker/D1 | P2-08 已完成 |

实现不得为了迁就当前单文件 Worker 的偶然行为而改变本文语义。确需变更时，先更新契约、威胁模型和受影响测试，再修改服务端与桌面端。

## 11. P1 自动化规则契约

`GET /v1/automation-rules?scope=ACTIVE|ALL&afterVersion=n` 使用独立于目录的 `ruleSetVersion`。user 只能读取 ACTIVE，admin 可读取 ALL；强 ETag 为 `"automation-active-n"` 或 `"automation-all-n"`，当前版本返回 304，客户端版本超前返回 `409 AUTOMATION_VERSION_AHEAD`。响应最多 4 MiB，规则按优先级降序、冲突顺序和 ID 稳定排序，最多 100 条。

管理员 POST/PATCH 必须同时发送 `If-Match: "automation-all-n"` 和 16～128 字符的 `Idempotency-Key`。成功只递增一次规则集版本，并分别返回 201/200；同 key/同请求重放原成功响应，同 key/不同请求或旧版本返回 409。更新会把规则自身 `version` 加 1，并把完整快照追加到不可变历史；v1 不提供删除端点，停用通过 PATCH `isEnabled=false` 完成。

规则定义只允许：

- 匹配模式 `ALL` / `ANY`；字段 `FEED`、`CATEGORY`、`TITLE`、`AUTHOR`、`CONTENT`、`LANGUAGE`、`PUBLISHED_AT`、`HAS_AUDIO`、`HAS_VIDEO`。
- 操作符 `EQUALS`、`CONTAINS`、`REGEX`、`BEFORE`、`AFTER`、`EXISTS`，并按字段限制合法组合。
- 动作 `ADD_TAG`、`HIDE`、`MARK_READ`、`GENERATE_SUMMARY`、`TRANSLATE`、`SEND_TO_MEDIA`、`NOTIFY`。
- 每条 1～16 个条件、1～8 个动作；名称最长 120，普通文本最长 512，正则最长 256。除 `ADD_TAG` 外同类动作不能重复；动作顺序唯一。无参数动作拒绝任意值，翻译仅接受 `zh-Hans`、`en`、`ja`、`ko`。

Worker 只验证、版本化和发布规则，不执行正文匹配，也不把命中条目、AI 结果、字幕或本地文件写入 D1。桌面端下载 ACTIVE 快照后再次通过 Core 验证器/解释器，并在本机 SQLite 中计划和执行受限动作。

## 12. P2 外部集成策略契约

`GET /v1/integration-policies?scope=ACTIVE|ALL&afterVersion=n` 使用独立于目录、规则和智能视图的 `policySetVersion`。user 只能读取 ACTIVE，admin 可读取 ALL；schema v2 客户端必须发送 `X-LenxTool-Integration-Policy-Schema: 2`，响应包含 `policySchemaVersion: 2`，强 ETag 为 `"integration-policies-v2-active-n"` 或 `"integration-policies-v2-all-n"`。响应设置 `Vary: X-LenxTool-Integration-Policy-Schema` 和 `Cache-Control: no-store, no-transform`；当前版本返回 304，客户端版本超前返回 `409 INTEGRATION_POLICY_VERSION_AHEAD`。

管理员 PUT 必须同时发送 `If-Match: "integration-policies-v2-all-n"` 和 16～128 字符的 `Idempotency-Key`，请求体包含 `policySchemaVersion: 2` 与完整 `policies` 集合。schema 版本和完整规范请求都进入幂等摘要。成功整组替换并只递增一次版本；同 key/同请求重放原成功响应，同 key/不同请求或旧版本返回 409。支持类型固定为 `OBSIDIAN`、`EAGLE`、`ZOTERO`、`READWISE`、`CUBOX`、`READECK`、`OUTLINE`、`QBITTORRENT`、`WEBHOOK`。

每种 schema v2 策略只允许 `kind`、`isEnabled`、`allowedHosts`、`trustedPrivateEndpoints`、`allowedResources` 和 `allowedLoopbackHttpPorts`。四个数组分别限制为 32、32、32、16 项，规范 JSON 单列不超过 8 KiB，完整策略集不超过 40 KiB：

- `allowedHosts` 只允许公网 HTTPS/443 使用的精确 DNS；拒绝协议、端口、路径、通配符、所有 IP 表示、localhost、`.local` 和 `home.arpa` 等保留后缀。
- `trustedPrivateEndpoints` 只对 Readeck、Outline、qBittorrent、Webhook 开放，保存精确私网 DNS 与端口。它允许管理员显式使用 `home.arpa`，但拒绝 IP、localhost、`.local` 和通配符；桌面执行期还必须确认全部 DNS 结果均为私网并固定连接地址。
- `allowedResources` 只对 Outline 与 qBittorrent 开放：前者是非空规范 UUID collection ID，后者是 1～128 字符的显式非控制符 category。网络端点与资源按 kind 形成全局许可，因此首版每种 provider 只允许一个本机目标。
- `allowedLoopbackHttpPorts` 只对 qBittorrent 开放；桌面仅可组合为精确 `http://localhost:<port>/`，不能扩展为 LAN HTTP。
- Obsidian 与 Eagle 四个数组必须全部为空。其他网络类型启用时至少要有一个公网、私网或 qBittorrent loopback 目标；Outline/qBittorrent 启用时还必须有至少一个资源。

Worker/D1 只发布同一信任域内的共享许可元数据，不保存个人 TargetId、完整 URL/路径、Zotero User ID 或目标修订、Readwise token/固定队列目标、API key、Cookie、DPAPI 密文、DNS 结果、健康检查状态、第三方返回 ID/URL、条目或外部响应。策略发布本身不会触发健康探测或第三方写入。

未发送 schema 头的旧 GET 只得到 v1 三字段兼容投影和旧 ETag。ACTIVE 中仅依赖私网/loopback 的启用项会隐藏，避免旧桌面因空 `allowedHosts` 拒绝整份快照；ALL 中存在这类项时直接返回 `400 INTEGRATION_POLICY_SCHEMA_UPGRADE_REQUIRED`，旧格式 PUT 也一律要求升级，防止加载再发布时丢失扩展授权。严格本机校验上线前由旧入口写入的 Obsidian/Eagle 精确 DNS 仍会先验证再投影为空；损坏数组或扩展列失败关闭为 503。生产部署顺序固定为 `0011 migration → Worker schema v2 → Desktop schema v2`。
