# Worker D1 Schema

状态：P0 目录 schema/读写已实现，P1-13 Worker AI 策略字段已接入
最后核对：2026-07-25
权威迁移：[0001_initial.sql](../../cloud/LenxTool.Worker/migrations/0001_initial.sql)、[0002_feed_catalog.sql](../../cloud/LenxTool.Worker/migrations/0002_feed_catalog.sql)、[0003_catalog_mutations.sql](../../cloud/LenxTool.Worker/migrations/0003_catalog_mutations.sql)、[0004_feed_full_text_policy.sql](../../cloud/LenxTool.Worker/migrations/0004_feed_full_text_policy.sql)、[0005_feed_ai_policy.sql](../../cloud/LenxTool.Worker/migrations/0005_feed_ai_policy.sql)
接口语义：[Worker v1 API 契约](worker-v1.md)

## 1. 数据边界

D1 是账号和管理员发布的共享订阅配置的权威来源。它保存账号、会话摘要、额度、审计、分类、Feed 配置和目录版本，但不保存：

- RSS/Atom/XML/HTML 响应或文章标题、摘要、正文、附件正文。
- AI 结果、字幕、音视频、用户文件名、Windows 路径或 DPAPI 数据。
- 本地抓取状态、已读、收藏、阅读进度或私人备注。

文章内容和私人状态只进入桌面端 SQLite。该边界与 [ADR-001](../decisions/ADR-001-admin-curated-rss.md) 一致。

## 2. 迁移规则

- D1 迁移使用顶层顺序 SQL 文件；已应用文件记录在默认 `d1_migrations` 表。
- `0001_initial.sql` 创建身份、额度和审计表。
- `0002_feed_catalog.sql` 只做加法，创建目录状态、分类、Managed Feed 和索引，不改写 0001 数据。
- `0003_catalog_mutations.sql` 增加条件目录写入标记、审计版本、幂等成功响应和事务 guard；不保存原始请求正文、凭据或文章内容。
- `0004_feed_full_text_policy.sql` 增加受限的全文获取枚举；`0005_feed_ai_policy.sql` 为分类和 Feed 增加显式 AI 策略覆盖、目标语言、每日条目和并发上限，自动开关默认继承全局关闭值。
- 测试启动器先应用 0001、写入旧 schema 哨兵行，再应用全部迁移，从而验证带数据升级；再次调用迁移流程不会重复执行已记录文件。
- Wrangler 应用某个迁移失败时会回滚该迁移，并保留之前成功的迁移。生产恢复遵循第 6 节，不提交手写“向下迁移”去伪造 `d1_migrations` 历史。

依据：[Cloudflare D1 migrations](https://developers.cloudflare.com/d1/reference/migrations/)、[Wrangler D1 migration commands](https://developers.cloudflare.com/d1/wrangler-commands/#d1-migrations-apply)、[Workers D1 test API](https://developers.cloudflare.com/workers/testing/vitest-integration/test-apis/#d1)。

## 3. 目录表

### 3.1 `feed_catalog_state`

该表只有 `singleton_id = 1` 一行：

| 字段 | 约束与职责 |
|---|---|
| `singleton_id` | 整数主键，`CHECK = 1`，防止出现多个全局版本源 |
| `catalog_version` | 非负 SQLite INTEGER；初始值 0，应用层限制为 JSON 安全整数 `0～2^53-1` |
| `updated_at` | 20～40 字符的 UTC 时间文本 |
| `last_mutation_id` | 可空 36 字符内部事务标记；用于把条件版本更新与同一 D1 batch 的业务写入绑定，不进入公开 DTO |

P0-04 的每个成功单项写入会在同一事务中比较并递增该版本；失败、冲突和幂等重放不递增。

### 3.2 `feed_categories`

| 字段 | 约束与职责 |
|---|---|
| `id` | 36 字符资源 ID；P0-04 生成规范 UUID |
| `name` | 去除首尾空白后 1～80 个 Unicode 字符 |
| `name_norm` | 内部唯一键；P0-04 计算 NFKC + Unicode case fold，不出现在公开 DTO |
| `sort_order` | 整数 0～1,000,000 |
| `is_enabled` | 整数布尔值 0/1 |
| `ai_*_policy` | 手动摘要、自动摘要、自动翻译的 `INHERIT` / `ENABLED` / `DISABLED` 覆盖；默认 `INHERIT` |
| `ai_translation_target_language` | null 或 `zh-Hans` / `en` / `ja` / `ko` |
| `ai_daily_entry_limit` | null 或整数 1～1,000；null 表示继承 |
| `ai_max_concurrency` | null 或整数 1～4；null 表示继承 |
| `deleted_at` | null 或 UTC 时间；只用于软删除，不出现在公开 DTO |
| `version` | 非负整数；资源最后变更时的全局目录版本 |
| `created_at` / `updated_at` | 20～40 字符的 UTC 时间文本 |

部分唯一索引 `ux_feed_categories_name_norm_active` 只覆盖 `deleted_at IS NULL` 的行，因此未删除分类名称唯一，同时允许保留被软删除分类的历史记录。

### 3.3 `managed_feeds`

| 字段 | 约束与职责 |
|---|---|
| `id` | 36 字符资源 ID |
| `original_url` | 1～2,048 字符 HTTPS URL；保留管理员提交的共享配置 |
| `normalized_url` | 1～2,048 字符、无 fragment 的规范 HTTPS URL；活动 Feed 唯一 |
| `display_name` | 去除首尾空白后 1～160 个字符 |
| `site_url` | null 或 1～2,048 字符 HTTPS URL |
| `category_id` | 可空；外键指向 `feed_categories.id`，`ON DELETE RESTRICT` |
| `view_kind` | `ARTICLE`、`PICTURE`、`AUDIO`、`VIDEO`、`NOTIFICATION` |
| `refresh_interval_minutes` | 整数 5～1,440 |
| `sort_order` | 整数 0～1,000,000 |
| `is_enabled` | 整数布尔值 0/1 |
| `ai_*_policy` | 手动摘要、自动摘要、自动翻译的 `INHERIT` / `ENABLED` / `DISABLED` 覆盖；默认 `INHERIT` |
| `ai_translation_target_language` | null 或 `zh-Hans` / `en` / `ja` / `ko` |
| `ai_daily_entry_limit` | null 或整数 1～1,000；null 表示继承 |
| `ai_max_concurrency` | null 或整数 1～4；null 表示继承 |
| `deleted_at` | null 或 UTC 时间；Feed 删除只做软删除 |
| `version` | 非负整数；资源最后变更时的全局目录版本 |
| `created_at` / `updated_at` | 20～40 字符的 UTC 时间文本 |

`ux_managed_feeds_normalized_url_active` 是 `deleted_at IS NULL` 条件下的唯一索引。同一活动规范 URL 不能重复；软删除后可以重新创建，但历史行仍保留。`normalized_url` 按原值存储并参与唯一比较，允许保留有意义的 query；P0-04 的规范化器不得复用会无条件删除 query 的通用 URL 逻辑。

D1 默认在查询和迁移中强制外键。分类关系使用 `RESTRICT`，因此硬删除仍被任何历史 Feed 引用的分类会立即失败，不会级联丢失 Feed。依据：[Cloudflare D1 foreign keys](https://developers.cloudflare.com/d1/sql-api/foreign-keys/) 和 [SQLite partial unique indexes](https://www.sqlite.org/partialindex.html#unique_partial_indexes)。

### 3.4 目录写入元数据

- `catalog_idempotency` 以操作者、HTTP 方法、规范路径和 key 为作用域，只保存规范请求 SHA-256、成功状态、成功响应和 24 小时有效期；不保存原始请求正文。账号删除时对应记录级联删除。
- `audit_events.catalog_version` 记录成功目录操作对应的全局版本；审计仍只含操作者、目标、动作、请求 ID、脱敏 IP 摘要和时间。
- `catalog_mutation_guards` 是事务内临时约束表。成功 batch 在提交前删除 guard 行；任何业务写入、审计或幂等结果缺失都会触发约束失败并回滚整个 batch，因此正常静态状态下该表为空。

## 4. 数据库约束与应用约束

数据库直接保证：

- 单例目录版本、非负版本、整数范围、布尔值、Feed 视图枚举和 HTTPS 基线。
- AI 开关、目标语言、每日条目上限和并发上限均受 CHECK 约束；数据库不保存提示词、文章正文、摘要或译文。
- 未删除分类规范名唯一、未删除 Feed 规范 URL 唯一。
- Feed 只能引用存在的分类；分类硬删除不会级联删除 Feed。
- D1 batch 中后续约束失败会回滚该批次先前写入。

P0-04 路由保证：

- ID 是规范 UUID；时间是合法 UTC/RFC 3339，而不只是满足长度。
- 分类名称的 NFKC + Unicode case fold、URL 完整语法/用户信息/端口/控制字符校验。
- 启用 Feed 时分类未删除且已启用。
- `If-Match`、全局版本递增、幂等、容量上限、业务审计和错误码原子一致。

Schema 约束是最后防线，不替代 API 边界校验。

## 5. 索引与查询形状

- 分类和 Feed 的活动唯一索引同时承担重复检测。
- `ix_*_catalog_order` 支持按分类、启用状态、排序号和 ID 生成确定性目录。
- `ix_*_version` 支持版本相关诊断和后续增量逻辑。
- P0 v1 返回有界原子快照，不为目录增加偏移量分页表或文章表。

## 6. 发布与恢复

在 `cloud/LenxTool.Worker` 中：

```powershell
npx wrangler d1 migrations list lenx-tool --remote
npx wrangler d1 time-travel info lenx-tool
npx wrangler d1 migrations apply lenx-tool --remote
npx wrangler d1 migrations list lenx-tool --remote
```

应用前记录当前时间/书签，应用后确认无待处理迁移。Wrangler 会在应用迁移后捕获备份；迁移 SQL 失败会回滚当前迁移。

若迁移成功但随后发现生产语义问题，先停止相关 Worker 写入并保留时间、书签和错误证据。D1 Time Travel 可恢复到迁移前的分钟级时间点，但恢复会原地覆盖数据库、取消进行中的查询，是破坏性运维动作，必须单独确认后执行。依据：[Cloudflare D1 Time Travel](https://developers.cloudflare.com/d1/reference/time-travel/)。
