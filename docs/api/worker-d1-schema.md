# Worker D1 Schema

最新权威迁移：[0009_smart_views.sql](../../cloud/LenxTool.Worker/migrations/0009_smart_views.sql)、[0010_integration_policies.sql](../../cloud/LenxTool.Worker/migrations/0010_integration_policies.sql)、[0011_integration_policy_metadata.sql](../../cloud/LenxTool.Worker/migrations/0011_integration_policy_metadata.sql)。生产发布必须按序先应用迁移，再部署读取新列的 Worker；0011 之前部署策略 schema v2 Worker 会使集成策略查询因列尚不存在而失败。

状态：P0 目录、P1 AI 策略和受限自动化规则、DISC-02 已知目录发现、P2 智能视图与外部集成策略 schema/读写均已实现
最后核对：2026-08-13
权威迁移：[0001_initial.sql](../../cloud/LenxTool.Worker/migrations/0001_initial.sql)、[0002_feed_catalog.sql](../../cloud/LenxTool.Worker/migrations/0002_feed_catalog.sql)、[0003_catalog_mutations.sql](../../cloud/LenxTool.Worker/migrations/0003_catalog_mutations.sql)、[0004_feed_full_text_policy.sql](../../cloud/LenxTool.Worker/migrations/0004_feed_full_text_policy.sql)、[0005_feed_ai_policy.sql](../../cloud/LenxTool.Worker/migrations/0005_feed_ai_policy.sql)、[0006_automation_rules.sql](../../cloud/LenxTool.Worker/migrations/0006_automation_rules.sql)、[0007_explicit_feed_view_kind.sql](../../cloud/LenxTool.Worker/migrations/0007_explicit_feed_view_kind.sql)、[0008_feed_discovery_index.sql](../../cloud/LenxTool.Worker/migrations/0008_feed_discovery_index.sql)、[0009_smart_views.sql](../../cloud/LenxTool.Worker/migrations/0009_smart_views.sql)、[0010_integration_policies.sql](../../cloud/LenxTool.Worker/migrations/0010_integration_policies.sql)、[0011_integration_policy_metadata.sql](../../cloud/LenxTool.Worker/migrations/0011_integration_policy_metadata.sql)
接口语义：[Worker v1 API 契约](worker-v1.md)

## 1. 数据边界

D1 是账号和管理员发布的共享订阅配置/受限规则的权威来源。它保存账号、会话摘要、额度、审计、分类、Feed/AI 策略、目录版本、自动化规则/版本、智能视图和九种集成的共享开关/主机 JSON，但不保存：

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
- `0006_automation_rules.sql` 增加独立规则集状态、当前规则和不可变版本历史；只保存受限定义与发布元数据，不保存匹配条目或执行结果。
- `0007_explicit_feed_view_kind.sql` 增加视图覆盖状态；历史非 `ARTICLE` 值回填为显式覆盖，历史 `ARTICLE` 因无法区分默认值与强制值而保持自动模式。
- `0008_feed_discovery_index.sql` 从所有未删除 `managed_feeds` 原位回填发现字段白名单，并用 Feed/分类触发器持续同步；同时增加按用户、UTC 分钟分桶的发现限流状态。它不复制 `original_url`、AI 策略、删除时间、正文或私人状态。
- `0009_smart_views.sql` 增加独立智能视图集状态、当前定义、不可变版本、幂等和事务 guard；只保存管理员发布的有界筛选定义，不保存筛选结果、文章或用户阅读状态。
- `0010_integration_policies.sql` 增加独立集成策略集状态、当前类型开关/精确 DNS 主机白名单、不可变版本、幂等和事务 guard；个人目标、凭据、探测结果与外部响应禁止进入 D1。
- `0011_integration_policy_metadata.sql` 为当前策略增加受信私网 `{host,port}`、provider 资源和 qBittorrent localhost HTTP 端口三个 JSON 列；旧行回填为空数组，不增加个人凭据、完整 URL、条目或第三方响应。
- 测试启动器先应用 0001、写入旧 schema 哨兵行，再应用全部迁移，从而验证带数据升级；再次调用迁移流程不会重复执行已记录文件。
- Wrangler 应用某个迁移失败时会回滚该迁移，并保留之前成功的迁移。生产恢复遵循第 7 节，不提交手写“向下迁移”去伪造 `d1_migrations` 历史。

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
| `view_kind_explicit` | 整数布尔值 0/1；0 表示自动识别，1 表示强制采用 `view_kind`（包括强制 `ARTICLE`） |
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

### 3.4 `feed_discovery_index` 与 `feed_discovery_rate_limits`

`feed_discovery_index` 是 `managed_feeds` 的只读查询投影，不是第二个写入真相源。它只包含：

- Feed ID、规范 URL、显示名及仅供匹配的显示名规范值、公开站点 URL。
- 可空的分类 ID/名称及仅供匹配的分类规范名、Feed/分类启用状态。
- 公开视图类型与目录元数据更新时间。

首次迁移从所有未删除 Feed 回填；之后 `managed_feeds` 的新增、更新、软/硬删除触发器维护 Feed 行，分类名称、启用状态或软删除变更触发器维护关联分类快照。发现查询从不直接写这个投影。唯一规范 URL 约束与主目录的活动唯一性一致，标题、分类和活动状态索引支持参数化关键词检索及“匹配等级降序、更新时间降序、Feed ID 升序”的稳定游标。

该表明确不含 `original_url`、`deleted_at`、`view_kind_explicit`、刷新/排序策略、AI/全文策略、版本历史、文章、摘要、正文或用户状态。发现响应再使用更窄的公开字段映射，内部规范列也不出现在 JSON。

`feed_discovery_rate_limits` 只保存 `actor_user_id`、16 字符 UTC 分钟桶和有界计数，主键为用户与分钟桶；用户删除时计数级联删除，过期桶由查询路径渐进清理。它不保存查询文本、URL、IP、响应或 token。

### 3.5 目录写入元数据

- `catalog_idempotency` 以操作者、HTTP 方法、规范路径和 key 为作用域，只保存规范请求 SHA-256、成功状态、成功响应和 24 小时有效期；不保存原始请求正文。账号删除时对应记录级联删除。
- `audit_events.catalog_version` 记录成功目录操作对应的全局版本；审计仍只含操作者、目标、动作、请求 ID、脱敏 IP 摘要和时间。
- `catalog_mutation_guards` 是事务内临时约束表。成功 batch 在提交前删除 guard 行；任何业务写入、审计或幂等结果缺失都会触发约束失败并回滚整个 batch，因此正常静态状态下该表为空。

## 4. 自动化规则表

- `automation_rule_state` 只有 `singleton_id=1` 一行，保存非负 `rule_set_version`、更新时间和事务内 `last_mutation_id`；它与目录版本完全独立。
- `automation_rules` 保存当前规则版本、名称、优先级、冲突顺序、启用状态、ALL/ANY、条件/动作 JSON、创建/更新管理员和时间。数据库限制名称 1～120、优先级/冲突顺序 0～1000、JSON 有效性和最大存储长度；应用层继续验证字段/操作符组合、数量、文本、正则和动作载荷。
- `automation_rule_versions` 以 `(rule_id, version)` 为主键保存完整不可变快照、发布管理员与时间；规则删除会级联历史，但 v1 API 不提供删除，只允许发布停用版本。
- 当前规则索引按 `is_enabled, priority DESC, conflict_order, id` 支持 ACTIVE/ALL 稳定快照；历史索引按发布时间、规则 ID 和版本支持审计诊断。

每次成功 POST/PATCH 在同一 D1 batch 中比较并递增规则集版本、写当前规则、追加不可变版本、记录最小审计和幂等成功结果。失败、旧版本、普通用户 403 或幂等重放不会增加版本。条件/动作 JSON 仅保存管理员发布的有界规则配置，不会自动写入匹配文章、AI 结果、字幕或客户端路径；Worker 在落库前使用字段白名单和长度/数量上限重新规范化。

## 5. 外部集成策略表

- `integration_policy_state` 只有 `singleton_id=1` 一行，保存独立的非负 `policy_set_version`、更新时间和事务内 `last_mutation_id`。
- `integration_policies` 以九种受支持类型为主键，只保存启用开关、公开主机 JSON、精确私网端点 JSON、provider 资源 JSON、qBittorrent localhost HTTP 端口 JSON，以及发布管理员、时间和事务标记。四个 JSON 列各有 8,192 字符数据库上限，Worker/Core 再按规范 JSON UTF-8 字节统一校验 8 KiB，并限制完整策略集 40 KiB。Obsidian/Eagle 的四个数组必须为空；公开主机拒绝协议、端口、路径、通配符、所有 IP 表示、localhost、`.local` 和 `home.arpa`，显式私网端点可用精确 `home.arpa` 但仍拒绝 IP/localhost/`.local`。
- `integration_policy_versions` 保存每次完整替换后的有界不可变快照；`integration_policy_idempotency` 仅保存 24 小时有效的请求摘要和成功响应；`integration_policy_mutation_guards` 保证版本、当前策略、历史、审计和幂等结果属于同一原子 batch。

成功 schema v2 PUT 比较并只递增一次策略集版本，整组替换当前策略并记录 `integration_policy.replaced`。普通用户只能读取 ACTIVE，管理员可读取 ALL 并发布。ACTIVE endpoint/resource 元数据会下发同一 Worker 的登录账号，因此部署方必须把账号视为同一信任域，且首版每种 provider 只允许一个本机目标；D1 仍不保存个人 TargetId、完整 URL/路径、Eagle loopback 地址或资源库修订、Zotero User ID 或目标修订、Readwise 固定队列目标、API key/access token、Cookie、DPAPI 密文、DNS 结果、健康检查状态、第三方返回 ID/URL、条目或外部响应。最新迁移为 `0011_integration_policy_metadata.sql`。

严格本机校验部署前由旧入口写入的 Obsidian/Eagle 精确 DNS 行不需要追加破坏性迁移：Worker 读取时验证旧数组并仅投影空主机；查询范围内仍有非空旧行时会忽略相等的缓存条件并返回 200，任一损坏数组则在 304 判断前失败关闭为 503。管理员下一次以 schema v2 ALL 快照执行完整 PUT 时会把当前两行自愈为 `[]`，之后相等条件恢复 304。未发送 schema 头的旧 ACTIVE 只得到兼容投影且隐藏仅依赖私网/loopback 的启用项；旧 ALL/PUT 在存在高级约束时要求升级，避免旧管理端回写清空扩展列。历史版本快照保持不可变。

## 6. 数据库约束与应用约束

数据库直接保证：

- 单例目录版本、非负版本、整数范围、布尔值、Feed 视图枚举和 HTTPS 基线。
- AI 开关、目标语言、每日条目上限和并发上限均受 CHECK 约束；数据库不保存提示词、文章正文、摘要或译文。
- 未删除分类规范名唯一、未删除 Feed 规范 URL 唯一。
- Feed 只能引用存在的分类；分类硬删除不会级联删除 Feed。
- 发现索引只接受字段白名单、HTTPS URL、视图枚举、布尔值和有界时间/名称；规范 Feed URL 唯一。
- 发现限流按有效用户与 UTC 分钟唯一，计数保持在数据库约束范围内。
- 集成策略只接受固定类型、布尔开关和有效 JSON 白名单；主机语义由 Worker 的精确 DNS 校验器负责。
- D1 batch 中后续约束失败会回滚该批次先前写入。

P0-04 路由保证：

- ID 是规范 UUID；时间是合法 UTC/RFC 3339，而不只是满足长度。
- 分类名称的 NFKC + Unicode case fold、URL 完整语法/用户信息/端口/控制字符校验。
- 启用 Feed 时分类未删除且已启用。
- `If-Match`、全局版本递增、幂等、容量上限、业务审计和错误码原子一致。

Schema 约束是最后防线，不替代 API 边界校验。

## 7. 索引与查询形状

- 分类和 Feed 的活动唯一索引同时承担重复检测。
- `ix_*_catalog_order` 支持按分类、启用状态、排序号和 ID 生成确定性目录。
- `ix_*_version` 支持版本相关诊断和后续增量逻辑。
- `ix_feed_discovery_*` 支持标题/分类候选与活动状态过滤；排名和游标终局次序由参数化查询显式给出，不依赖索引的未指定顺序。
- `ix_feed_discovery_rate_limit_bucket` 支持清理过期分钟桶。
- P0 v1 返回有界原子快照，不为目录增加偏移量分页表或文章表。
- P1 规则返回最多 100 条、4 MiB 的有界原子快照，不建立匹配条目或动作执行结果表。
- P2 集成策略返回最多九种类型的原子快照；活动索引支持 ACTIVE 过滤，个人目标与健康状态始终留在客户端。

## 8. 发布与恢复

在 `cloud/LenxTool.Worker` 中：

```powershell
npx wrangler d1 migrations list lenx-tool --remote
npx wrangler d1 time-travel info lenx-tool
npx wrangler d1 migrations apply lenx-tool --remote
npx wrangler d1 migrations list lenx-tool --remote
```

应用前记录当前时间/书签，应用后确认无待处理迁移。Wrangler 会在应用迁移后捕获备份；迁移 SQL 失败会回滚当前迁移。

若迁移成功但随后发现生产语义问题，先停止相关 Worker 写入并保留时间、书签和错误证据。D1 Time Travel 可恢复到迁移前的分钟级时间点，但恢复会原地覆盖数据库、取消进行中的查询，是破坏性运维动作，必须单独确认后执行。依据：[Cloudflare D1 Time Travel](https://developers.cloudflare.com/d1/reference/time-travel/)。
