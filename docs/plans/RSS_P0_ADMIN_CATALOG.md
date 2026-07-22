# P0 详细计划：管理员订阅与普通用户只读目录

状态：进行中，P0-01～P0-04 已完成
最后核对：2026-07-22
上位文档：[RSS 集成总路线图](RSS_MASTER_ROADMAP.md)
参考项目：RSSNext/Folo（发现、OPML、订阅状态和内容视图的行为参考），LenxTool 当前 Worker/SQLite/资讯中心（实现基础）

## P0 完成定义

管理员可以登录后新增、验证、编辑、停用、分类、排序和删除共享 RSS/Atom，并预览/导入/导出 OPML；普通用户只能同步共享目录、在本机抓取并阅读条目。断网返回缓存，坏源相互隔离，所有管理员写操作有服务端授权和审计。

P0 不包含全文抓取、图片离线缓存、AI 摘要/翻译、自动化规则和外部导出；这些在 P1/P2 完成。

## 阶段 P0-A：身份与服务端授权

### P0-01：冻结账号与目录 API 契约

**目标：** 先定义请求/响应、错误码、分页、目录版本和权限矩阵，避免 Worker 与 WPF 并行漂移。

**依赖：** Gate 0 可并行准备。
**预计范围：** S。
**主要文件：** 新 `docs/api/worker-v1.md`、`docs/THREAT_MODEL.md`、本计划。

**契约至少包含：**

- `POST /v1/auth/login`、`POST /v1/auth/refresh`、`POST /v1/auth/logout`、`GET /v1/me`。
- `GET /v1/feeds/catalog?afterVersion=`：user/admin 均可读，支持 ETag 或版本号。
- `/v1/admin/feed-categories` 与 `/v1/admin/feeds` 的 POST/PATCH/DELETE：仅 admin。
- 批量目录更新使用幂等请求 ID 和期望版本，冲突返回 409。
- 错误结构沿用 `AppError` 可映射字段；403 明确区分未登录和非管理员。

**验收：**

- [x] 每个端点列出角色、输入上限、成功响应、错误码和审计动作。
- [x] 写端点均有版本冲突和幂等规则。
- [x] 响应不包含密码摘要、令牌摘要、Feed 正文或本地信息。

**验证：** 用契约逐项对照 Worker 路由和桌面 DTO；文档链接检查。

**参考：** LenxTool Worker 现有 `/v1/auth/*`、`requireAdmin`、`audit`；Folo API 只用于理解客户端数据流，不采用其私有接口。

**完成记录（2026-07-21）：** 已新增 [Worker v1 API 契约](../api/worker-v1.md)，冻结账号/目录 DTO、角色矩阵、401/403、完整快照与 ETag、全局版本、`If-Match`、`Idempotency-Key`、原子批量写入和字段白名单；已逐项对照当前 Worker、D1 迁移与桌面 `AppError`，未实现项明确归入 P0-02～P0-10，并同步更新威胁模型。

### P0-02：补齐 Worker 身份端点和令牌测试

**目标：** 让桌面端可安全取得当前用户、刷新和退出，而不是仅保存一次登录响应。

**依赖：** P0-01。
**预计范围：** M。
**主要文件：** `cloud/LenxTool.Worker/src/index.ts`、`tests/auth.test.ts`、受控管理员初始化脚本、`WORKER_DEPLOYMENT.md`。

**验收：**

- [x] `GET /v1/me` 返回最小公开用户和额度状态。
- [x] 退出撤销当前 refresh token；轮换后旧 token 永久失效。
- [x] 禁用用户的 access/refresh 路径均被拒绝。
- [x] 提供受控终端执行的一次性首管理员初始化流程；不硬编码密码、邀请码或 token，重复执行安全失败。

**验证：** 登录、刷新重放、退出、禁用、过期、伪造 JWT 和首管理员重复初始化自动测试；typecheck。

**参考：** LenxTool 当前 Worker 令牌签发/轮换；不是 Folo 功能移植。

**完成记录（2026-07-21）：** 已实现 `GET /v1/me`、幂等 logout、同一 D1 batch 的 refresh 条件轮换、实时禁用检查和 `AppError` 可映射错误体；新增 Cloudflare 官方 Vitest pool + 真实本地 D1 迁移集成测试，覆盖登录、额度、并发重放、退出、禁用、过期/伪造 JWT、未知字段和首管理员重复初始化。首管理员流程使用临时 `BOOTSTRAP_TOKEN`、空库条件写入与受控 PowerShell 提示，成功后删除 Secret。

### P0-03：D1 共享分类和 Feed 目录迁移

**目标：** 新增只保存订阅配置、不保存文章内容的权威目录表。

**依赖：** P0-01。
**预计范围：** M。
**主要文件：** 新 `migrations/0002_feed_catalog.sql`、迁移测试、新 schema 文档。

**模型要求：**

- 分类：ID、名称、排序、启用、版本、创建/更新时间。
- Feed：ID、原始 URL、规范化 URL、显示名、站点 URL、分类、视图类型、刷新间隔、排序、启用、软删除时间、版本。
- 规范化 URL 唯一；保留有意义的 query，不沿用当前会删除 query 的通用 URL 逻辑。
- 所有外键和 CHECK 约束明确；删除分类不能级联误删历史 Feed。

**验收：**

- [x] 本地 D1 从 0001 正常升级并可重复执行迁移流程。
- [x] 同一未删除 Feed 的规范化 URL 不能重复创建。
- [x] 表中没有文章正文、AI 内容或用户本地路径字段。

**验证：** migration apply、约束、回滚/失败测试。

**参考：** Folo 数据 schema 的订阅实体拆分思路；LenxTool 隐私最小化约束。

**完成记录（2026-07-21）：** 已新增 `0002_feed_catalog.sql` 与 [Worker D1 Schema](../api/worker-d1-schema.md)，创建单例全局版本、分类和 Managed Feed 表；用部分唯一索引约束未删除分类规范名/Feed 规范 URL，用 `ON DELETE RESTRICT` 保留分类下的 Feed 历史，并以 `CHECK` 固定枚举、范围、布尔和 HTTPS 基线。官方 workerd/D1 测试从带哨兵数据的 0001 升级，验证重复应用、query 保留、软删除、外键、约束失败回滚和隐私字段白名单，Worker 20/20 通过。

### P0-04：管理员目录 CRUD 与审计

**目标：** 提供分类和 Feed 的服务端管理端点。

**依赖：** P0-02、P0-03。
**预计范围：** M。
**主要文件：** Worker `src/index.ts` 或拆分后的 `routes/admin-feeds.ts`、`catalog-repository.ts`、管理员 API 测试。

**验收：**

- [x] admin 可新增、编辑、启停、排序、移动分类和软删除 Feed。
- [x] user/匿名对每个写端点均为 403/401，数据库无变化。
- [x] 每次成功写操作记录操作者、目标、动作、版本、请求 ID；不记录完整正文或凭据。

**验证：** 角色参数化测试、并发期望版本冲突、幂等重放和审计查询测试。

**参考：** LenxTool Worker 现有管理员邀请码/用户端点；Folo 订阅操作只作交互参考。

**完成记录（2026-07-22）：** 已新增独立目录路由模块和 `0003_catalog_mutations.sql`，实现分类/Feed 的新增、更新、启停、排序、移动与软删除。所有写入要求 admin、`If-Match` 和 `Idempotency-Key`；全局版本、资源写入、最小审计和幂等成功响应在同一 D1 batch 中提交，并以条件版本和内部 guard 防止并发半提交。官方 workerd/D1 集成测试覆盖全部 6 个端点的 user/匿名拒绝、NFKC 重复分类、规范 URL 重复、危险 URL、停用分类、非空分类删除、同版本并发、幂等重放/错用和版本冲突；Worker 27/27 通过。

### P0-05：只读目录发布与增量同步

**目标：** 为 user/admin 发布稳定、可缓存的共享目录。

**依赖：** P0-04。
**预计范围：** S。
**主要文件：** Worker 目录读取路由、目录 DTO、API 测试。

**验收：**

- [ ] 返回启用分类/Feed、目录版本和服务端时间，不返回软删除记录。
- [ ] 未变化时支持 304 或等价的版本未变化响应。
- [ ] 目录排序确定，同一版本序列化结果稳定。

**验证：** 空目录、增量版本、排序、缓存头和禁用 Feed 测试。

**参考：** Folo 客户端订阅 store 的同步思想；由 LenxTool 独立定义 API。

### 检查点 P0-A

- [ ] Worker typecheck/test 全部通过，测试不再只有用户名规范化 1 项。
- [ ] 权限矩阵中所有 admin 写端点均有 user/匿名拒绝证据。
- [ ] `THREAT_MODEL.md` 已覆盖目录投毒、越权、重放和审计。

## 阶段 P0-B：桌面会话与本地目录

### P0-06：桌面会话模型与安全令牌存储

**目标：** 建立 `IAccountSessionService`，access token 仅驻内存，refresh token 使用 DPAPI CurrentUser 保存。

**依赖：** P0-01、P0-02。
**预计范围：** M。
**主要文件：** 新 Core 契约/模型、新 Infrastructure 会话服务、`DpapiSecretStore.cs`、安全测试。

**验收：**

- [ ] 启动可恢复 refresh token 并取得 `/v1/me`；失败清除失效会话。
- [ ] 并发 401 只触发一次刷新，原请求最多安全重放一次。
- [ ] 日志和 SQLite 不出现 access/refresh token。

**验证：** 假 Worker 覆盖登录、刷新风暴、退出、失效和脱敏。

**参考：** LenxTool DPAPI/错误模型；Folo 认证实现不复制。

### P0-07：登录、退出和角色状态 UI

**目标：** 把当前“云服务未登录”占位变为真实账号状态，并仅向管理员展示管理入口。

**依赖：** P0-06。
**预计范围：** M。
**主要文件：** `SettingsViewModel` 所在文件、`MainWindow.xaml`、`ShellViewModel.cs`、App 测试。

**验收：**

- [ ] 支持登录、退出、会话过期提示和额度显示。
- [ ] 普通用户不显示订阅管理入口，但可进入资讯中心。
- [ ] 客户端角色只控制可见性；服务端仍是授权真相来源。

**验证：** admin/user/未登录 ViewModel 测试、键盘/焦点和真实 WPF 手测。

**参考：** LenxTool 现有设置页/Worker；不是 Folo 账户体系。

### P0-08：本地 Feed schema v3 与迁移

**目标：** 建立目录镜像、抓取状态和通用 Feed 条目表，保留 schema v2 全部数据。

**依赖：** P0-03 的模型已冻结。
**预计范围：** M。
**主要文件：** `SqliteDatabase.cs`、新 Feed 模型、SQLite 迁移测试。

**验收：**

- [ ] 创建 `feed_categories`、`feed_catalog`、`feed_fetch_state`、`feed_entries` 和必要索引/FTS 映射。
- [ ] 从 schema v2 原位升级，旧早报、热点、AI 报告、媒体任务和设置均可读。
- [ ] Feed 内外部 ID/规范化 URL/内容哈希职责分离，不用全局唯一哈希错误吞掉转载。

**验证：** 新建库、v2 升级、失败回滚、WAL 备份和旧数据回归测试。

**参考：** Folo 本地数据库仅作缓存分层参考；LenxTool 不采用失败即重建数据库的缓存策略。

### P0-09：本地目录仓储与原子替换

**目标：** 把服务端目录按版本原子写入本地，读者永远看到完整版本。

**依赖：** P0-05、P0-08。
**预计范围：** M。
**主要文件：** 新 `IFeedCatalogRepository.cs`、`FeedCatalogRepository.cs`、仓储测试。

**验收：**

- [ ] 同一事务替换分类/Feed 并提交目录版本。
- [ ] 中途失败保留上一完整版本；软删除 Feed 不立即删本地文章。
- [ ] 可查询启用目录、管理员完整目录和最后同步状态。

**验证：** 原子替换、版本倒退拒绝、空目录和删除保留测试。

**参考：** Folo subscription store 的状态汇合；LenxTool SQLite 事务模式。

### P0-10：目录同步服务

**目标：** 登录后和定时同步共享目录，断网使用最后成功版本。

**依赖：** P0-06、P0-09。
**预计范围：** M。
**主要文件：** 新 `IFeedCatalogSyncService.cs`、实现、DI 注册、假 HTTP 测试。

**验收：**

- [ ] 支持首次同步、304/未变化、版本更新、取消和退避。
- [ ] 离线不清空目录，界面显示最后同步时间和过期状态。
- [ ] user 无任何目录写请求；admin 同样通过管理 API 修改而非直接本地改库。

**验证：** 假 Worker 覆盖断网、超时、401 刷新、乱序版本和取消。

**参考：** Folo store 同步思路；LenxTool `NewsCenterService` 缓存回退。

### 检查点 P0-B

- [ ] admin/user 均能登录并同步同一目录；只有 admin 看见管理入口。
- [ ] 修改客户端角色或直接调用 admin API 不能越权。
- [ ] schema v2 用户数据升级后完整保留。

## 阶段 P0-C：安全抓取与条目缓存

### P0-11：Feed URL 发现与 SSRF 防护

**目标：** 管理员输入站点或 Feed URL 时，安全发现并验证 RSS/Atom。

**依赖：** P0-04。
**预计范围：** M。
**主要文件：** 新 `IFeedDiscoveryService.cs`、`FeedDiscoveryService.cs`、URL 安全策略、网络测试。

**验收：**

- [ ] 默认仅 HTTPS；显式策略才允许 HTTP。
- [ ] DNS 解析后拒绝环回、链路本地、私网、保留地址和重定向到这些地址；内网 Feed 必须单独可信主机配置。
- [ ] 限制重定向次数、响应字节、解压后大小、MIME、连接/总超时；XML 禁用 DTD 和外部实体。

**验证：** 私网 IP、DNS 重绑定模拟、重定向链、巨型/压缩响应、XXE 和错误 MIME 测试。

**参考：** LenxTool 现有受控 URL/图片下载边界；Folo 发现入口只作交互参考。

### P0-12：RSS 2.0 / Atom 解析与稳定标识

**目标：** 将常见 Feed 解析为统一 `FeedEntry`，不执行不可信 HTML。

**依赖：** P0-08、P0-11。
**预计范围：** M。
**主要文件：** 新解析器/模型、解析 fixture、Core/Infrastructure 测试。

**验收：**

- [ ] 支持 RSS 2.0、Atom、CDATA、常见日期、作者、分类和 enclosure。
- [ ] 稳定 ID 优先使用 Atom id/guid，其次规范化 URL，最后 Feed+内容指纹。
- [ ] URL 规范化保留签名/身份相关 query；只移除明确追踪参数，不做破坏性通用裁剪。

**验证：** 真实样例 fixture、缺字段、重复、非法日期、命名空间和恶意 XML 测试。

**参考：** Folo 统一条目模型；LenxTool 当前 `DownloadXmlAsync` 和 `NewsArticle` 解析需被通用化而非直接扩写。

### P0-13：条件抓取、调度与退避

**目标：** 使用 ETag/Last-Modified 和有界并发刷新目录内 Feed。

**依赖：** P0-09、P0-11、P0-12。
**预计范围：** M。
**主要文件：** 新 `IFeedRefreshService.cs`、调度实现、抓取状态仓储、网络测试。

**验收：**

- [ ] 304 不重写条目；200 成功更新条件头和下次抓取时间。
- [ ] 单源超时/解析失败不影响其他源；连续失败指数退避并带上限。
- [ ] 应用退出取消抓取；同一 Feed 不并发重复刷新。

**验证：** 200/304/429/5xx/超时/取消/并发去重测试。

**参考：** LenxTool 现有单源隔离与缓存回退；Folo 本地缓存流。

### P0-14：Feed 条目仓储、FTS 与保留

**目标：** 事务保存条目并接入统一全文搜索，暂不实现收藏保护策略的 UI。

**依赖：** P0-08、P0-12。
**预计范围：** M。
**主要文件：** 新 `IFeedEntryRepository.cs`、实现、`content_fts` 迁移/同步、SQLite 测试。

**验收：**

- [ ] 批量 upsert 与 FTS 同事务；重复抓取不增加条目。
- [ ] 查询支持分页、Feed、分类、日期和未读占位筛选。
- [ ] 180 天清理先只作用于无私人状态条目；正式启用前由 P1 收藏状态测试兜底。

**验证：** 去重、分页稳定性、FTS、事务回滚和清理边界测试。

**参考：** LenxTool `NewsRepository`/FTS5；Folo entry cache 只作数据流参考。

### 检查点 P0-C

- [ ] 至少 20 个 RSS/Atom fixture 通过，覆盖中文和异常编码。
- [ ] 任一 Feed 失败不会阻断其他 Feed 或清空已有缓存。
- [ ] SSRF、XXE、巨型响应和重定向绕过均有拒绝测试。

## 阶段 P0-D：管理员界面与普通用户阅读

### P0-15：管理员订阅管理页

**目标：** 提供 Feed/分类列表、验证、新增、编辑、启停、排序和删除交互。

**依赖：** P0-04、P0-07、P0-10、P0-11。
**预计范围：** M。
**主要文件：** 新 `FeedAdminViewModel.cs`、管理页 XAML/模板、API client、App 测试。

**验收：**

- [ ] 保存前显示探测到的标题、站点、类型和警告。
- [ ] 409 版本冲突提示刷新后重试，不覆盖他人更新。
- [ ] user 无导航入口；即使构造命令也由服务端拒绝。

**验证：** admin/user ViewModel 测试、错误态、键盘、窄窗和高 DPI 手测。

**参考：** Folo `UnifiedDiscoverForm` 的单入口体验；界面使用 LenxTool 语义样式独立实现。

### P0-16：OPML 预览、选择导入与导出

**目标：** 管理员可先预览再批量导入，且可导出当前共享目录。

**依赖：** P0-15。
**预计范围：** M。
**主要文件：** 新 OPML codec、导入 ViewModel/对话框、文件服务、测试。

**验收：**

- [ ] 解析分组、标题、xmlUrl/htmlUrl；默认不自动提交。
- [ ] 预览标记新增、重复、无效和冲突项，可选择性导入。
- [ ] 批量提交原子或返回逐项结果；导出不含凭据和本地抓取状态。

**验证：** 嵌套分组、重复、畸形 XML、XXE、中文和大文件上限测试；WPF 手测。

**参考：** Folo `DiscoverImport.tsx`、`OpmlSelectionModal.tsx` 和数据控制导出行为。

### P0-17：普通用户时间线与筛选

**目标：** 将资讯中心从“按日期选一篇早报”扩展为可分页的 Feed 时间线，同时保留热点趋势页。

**依赖：** P0-13、P0-14。
**预计范围：** M。
**主要文件：** `NewsCenterViewModel.cs`、`MainWindow.xaml`、新分页查询模型、App 测试。

**验收：**

- [ ] 支持全部/分类/Feed、日期和关键词筛选，滚动分页稳定。
- [ ] 列表虚拟化，1 万条缓存仍可交互；选择条目使用现有原生阅读器。
- [ ] 断网显示最后刷新/同步时间；无任何订阅编辑控件。

**验证：** ViewModel 分页/筛选测试、10k 假数据性能检查、900×620～4K/DPI/键盘手测。

**参考：** Folo 内容视图/订阅列表；LenxTool 当前资讯页、RichArticleView 和热点页。

### P0-18：Feed 健康与管理员诊断

**目标：** 管理员查看本机每个 Feed 的抓取结果并执行安全重试。

**依赖：** P0-13、P0-15。
**预计范围：** S。
**主要文件：** 抓取状态查询、`FeedAdminViewModel.cs`、管理页模板、测试。

**验收：**

- [ ] 显示最后成功/失败、连续失败、下次重试、HTTP/解析错误类别。
- [ ] 错误脱敏，不展示令牌、完整响应或内网解析详情。
- [ ] 手动重试受并发限制，不能绕过 SSRF/大小/超时策略。

**验证：** 状态映射、重试去重和脱敏测试；WPF 手测。

**参考：** LenxTool 可观测性与单源隔离要求；该能力是 LenxTool 运维需求，不是直接复制 Folo。

### P0-19：首页真实数据与旧早报兼容

**目标：** 去除 `DashboardViewModel` 演示数据，并把固定早报平滑纳入目录模型。

**依赖：** P0-14、P0-17。
**预计范围：** M。
**主要文件：** `DashboardViewModel.cs`、首页 XAML、聚合查询服务、App 测试。

**验收：**

- [ ] 首页显示真实最新 Feed、热点、最近任务和收藏计数，无固定日期/标题。
- [ ] `https://daily.juya.uk/rss.xml` 作为可管理的初始 Feed 或兼容种子存在。
- [ ] schema v2 旧早报历史仍可搜索和阅读，不重复显示迁移后的同一条目。

**验证：** 空库、旧库升级、离线、真实数据 ViewModel 测试和 WPF 手测。

**参考：** LenxTool 现有首页/每日早报；Folo 不提供这部分迁移方案。

### P0 最终检查点

- [ ] admin 完成登录 → 新增 Feed → 分类 → 发布 → 刷新 → 阅读 → 停用 → 审计的端到端流程。
- [ ] user 同步并阅读同一目录，对所有写端点均被拒绝。
- [ ] OPML 导入/导出、断网缓存、坏源隔离、v2 升级和 10k 条目性能通过。
- [ ] 更新 `SPECIFICATION.md`、`ARCHITECTURE.md`、`THREAT_MODEL.md`、`USER_GUIDE.md`、`TEST_REPORT.md`。
- [ ] 仅在上述证据齐全后，才在 `IMPLEMENTATION_PLAN.md` 将 P0 标记完成。
