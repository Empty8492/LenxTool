# Lenx Tools 架构说明

## 1. 分层与依赖方向

```text
LenxTool.App  ───────────────┐
    WPF / ViewModel / DI      │
                              ▼
LenxTool.Infrastructure ──> LenxTool.Core
 SQLite / HTTP / AI / OS      Models / Contracts / Errors

LenxTool.Core 不引用 WPF、SQLite、HTTP 或任何基础设施包。
```

`App` 是唯一组合根：注册 ViewModel、仓储、命名 HttpClient、后台服务和窗口。ViewModel 只依赖 Core 接口。Infrastructure 将外部失败统一映射为 `AppError`，避免页面理解供应商响应结构。

## 2. 运行时数据流

### 资讯

```text
页面命令 -> NewsCenterViewModel -> INewsCenterService
  -> 并发调用 ITrendSource / IDailyBriefSource（每源独立超时）
  -> 规范化 + 指纹去重 -> 事务写 SQLite
  -> FTS5 查询/筛选 -> 页面模型
  -> 失败时读取缓存并附带 stale 状态
```

每日早报同时保存用于搜索的纯文本和用于阅读的 RSS 富内容。原生 WPF 阅读控件只解析允许的图片、标题、列表和 HTTP/HTTPS 链接，忽略脚本、样式、iframe、object 与 embed；点击链接时才交给系统浏览器。日期选择只在已缓存文章集合中切换，不执行远端 HTML 或脚本。

AI 报告使用自备 DeepSeek Key 经请求级 Bearer 授权调用 `deepseek-v4-flash`。早报正文和热点标题作为不可信资料放入有长度上限的 DATA 区域；模型无工具权限，输出只按纯文本处理。成功结果与模型、请求数、token 用量一并事务写入 `ai_reports` 和 FTS5。

### 媒体任务

```text
导入文件 -> IMediaJobQueue -> 持久化 media_jobs
  -> IAudioExtractor -> 16 kHz mono WAV
  -> ITranscriptionService（Groq / Worker / Local Whisper）
  -> IAudioChunkPlanner（重叠分片）
  -> ISegmentMerger（交接、去重、置信过滤）
  -> 可选 ISubtitleTranslator
  -> ISubtitleExporter -> 原文/译文/双语 SRT 与纯文本 TXT
```

每个阶段更新数据库进度并观察同一个 `CancellationToken`。临时文件使用单任务目录，成功或失败后尽力清理；清理错误写脱敏日志。

### 更新

```text
镜像列表 -> HTTPS 下载签名清单 -> 内置公钥验签
  -> 语义版本/最低版本策略 -> 用户确认
  -> 后台下载到 Updates/staging -> 大小 + SHA-256 + 发布签名
  -> 启动安装器 /VERYSILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS
```

更新永不覆盖 `%LocalAppData%\LenxTool`。镜像是清单数据，不写死在更新算法中。

## 3. 数据库

数据库路径：`%LocalAppData%\LenxTool\Data\lenx.db`。连接初始化启用外键、WAL、busy timeout 和完整同步策略。迁移在单事务中执行，执行前通过 SQLite 在线备份 API 生成包含已提交 WAL 内容的一致性单文件快照；失败则不提升 `schema_versions`。

核心表：

- `news_articles`：每日早报条目、搜索纯文本、原始 RSS 富内容、来源日期、内容指纹。
- `trend_items`：平台、排名、热度、URL、指纹、采集时间。
- `ai_reports`：对象类型/ID、报告类型、模型和安全渲染内容。
- `media_jobs`：输入、输出、状态、进度、引擎、模型、用量和结构化错误。
- `subtitle_segments`：任务、序号、时间、原文、译文、置信指标。
- `feed_catalog_state`、`feed_categories`、`feed_catalog`：本地目录版本、作用域、分类和共享 Feed 镜像。
- `feed_fetch_state`：按 Feed 隔离的条件请求、调度和连续失败状态。
- `feed_entries`：通用 RSS/Atom 条目；外部 ID 只在 Feed 内唯一，规范化 URL 与内容哈希不承担全局唯一职责。
- `favorites`：通用实体收藏和备注。
- `tags`、`entity_tags`：标签与多态关联。
- `app_settings`：非秘密设置。
- `schema_versions`：已应用迁移及校验和。
- `content_fts`：Feed、早报、热点、AI 报告、字幕、标签和收藏的统一 FTS5 内容索引。
- `feed_entry_search_documents`：Feed 条目的搜索文档投影；schema v5 触发器负责与 `content_fts` 同事务同步。
- `subtitle_search_documents`：按媒体任务聚合字幕原文和译文；字幕批量替换在同一事务中重建该任务的单个搜索文档，媒体任务删除触发索引清理。
- `user_entry_states`：按 `(entry_id, local_profile)` 保存本机已读、收藏、阅读进度、私人备注和更新时间；不建立 Feed 外键，确保目录软删除或条目清理不会误删私人状态。
- `entry_assets`：按条目和来源 URL 建立本地资源索引，记录内容哈希、MIME、大小及创建/访问时间；物理缓存以 SHA-256 命名并在读取时校验。
- `feed_full_text_content`、`feed_full_text_jobs`、`feed_full_text_host_state`：全文缓存、可恢复任务租约和单主机退避。
- `feed_ai_automation_jobs`、`feed_ai_automation_daily_entries`：本地 AI 自动处理队列和每日不同条目额度。
- `feed_automation_runs`、`feed_automation_action_runs`、`feed_automation_rule_state`、`feed_automation_rules`：规则快照、确定性运行账本、动作租约和状态。
- `feed_media_deliveries`：Feed 附件到既有媒体任务的幂等来源台账。
- `app_notifications`：内容命中、系统健康和任务完成三类本地通知的稳定关联、标题及创建/已读状态；不保存正文、结果内容、异常详情或任意 URI。
- `local_scheduled_tasks`：本地计划定义、错过执行策略、启用状态和下一次 UTC 游标；不保存任务输入、输出或执行历史。
- `local_schedule_runs`：以计划 ID 和计划 UTC 时刻唯一标识实际执行窗口，保存封闭状态、尝试次数、租约和最小时间审计；不保存任务输入、输出、正文、异常详情或凭据。

本地数据库当前版本为 schema v23。v3～v7 依次建立字幕翻译用量、Feed 目录/条目、Feed FTS、私人阅读状态和离线资源；v8～v11 建立全文策略/队列、Feed AI 缓存策略和本地自动处理；v12～v14 建立规则运行账本、隐藏状态与 ACTIVE 规则缓存；v15 建立 Feed 媒体投递台账；v16 建立应用内通知收件箱；v17 回填字幕、标签和收藏搜索文档，并为收藏和标签建立同步触发器；v18 为 Feed 视图分类增加独立显式覆盖状态；v19 为通知增加三类封闭类别；v20 保存最近一次验证通过的 ACTIVE 智能视图筛选定义；v21 增加带幂等键、租约、重试、取消和封闭错误码的持久化条目导出任务；v22 保存本地计划定义、错过执行策略、启用状态与下一次 UTC 游标；v23 增加唯一计划窗口、租约、尝试次数和完成/取消历史。旧 v2 数据会依次应用全部迁移，任何一步失败均在事务中回滚且不提升版本。

统一搜索仓储只接受参数化查询对象，先约束关键词、日期、筛选组合、偏移和页大小，再通过七类实体 CTE 投影到同一结果模型。排序固定为 FTS 排名、实体时间倒序、实体类型和稳定文档 ID；仓储读取 `limit + 1` 决定下一页，UI 使用原始页数量推进偏移并按稳定结果身份防重。应用内导航服务只传递路由和实体 ID：Feed 结果由资讯 ViewModel 精确读取并打开，字幕结果由历史 ViewModel 精确定位任务，其他外部地址仍经 HTTP/HTTPS 安全命令打开。

`IFeedCatalogRepository` 是共享目录的本地边界。服务端快照写入时，分类、Feed、作用域、目录版本、生成时间和最后同步时间在同一事务提交；版本倒退在删除前拒绝，失败回滚整批替换。目录移除不会删除 `feed_entries`，仍存在 Feed 的 `feed_fetch_state` 会跨替换保留。读取状态、分类和 Feed 使用同一读事务，ACTIVE 投影过滤停用资源；若本地只同步过 ACTIVE，ALL 查询返回不可用而不是伪造管理员完整目录。

`IFeedRefreshService` 只从 ACTIVE 投影选择到期 Feed，并通过 `FeedNetworkPolicy` 与固定地址传输执行条件 GET。调度有全局并发上限和 Feed 级单飞门闩；每次重定向重新做 SSRF 校验，跨 authority 不携带条件验证器。200 的提交顺序固定为“解析 → `IFeedEntryRepository` 单事务 upsert/FTS → `IFeedFetchStateRepository` 保存 ETag/Last-Modified 与下次时间”，因此条目写失败不会提交新验证器；状态保存失败最多造成下一次幂等重抓。304 不调用条目写入。仓储查询按稳定时间/ID 顺序分页，并以目录表关联 Feed/分类。

P1-20 的保留候选由 `FeedRetentionSql` 统一投影，严格排除 favorites、entity tags、任意 `user_entry_states` 以及 PENDING/RUNNING/RETRY 的全文、AI、规则动作和媒体任务。`FeedEntryRepository` 把最多 5000 个候选装入连接级临时表，在同一事务中清理关联规则账本、媒体投递台账、资源索引和条目；全文/AI 队列与 FTS 继续由外键和触发器同步。`DatabaseMaintenanceService` 循环提交有界批次，随后删除索引不再引用的图片文件并执行既有 LRU 预算；清理取消不会回滚已经提交的安全批次。容量扫描在窗口显示后后台读取数据库/WAL/SHM、图片和模型目录。`PRAGMA optimize` 后只有可用空间至少为数据库两倍且不低于 32 MiB 时才执行 `VACUUM`，SQLite busy/full 被视为可恢复的“跳过压缩”。设置 ViewModel 只能经预览得到固定截止点，再由第二次显式确认执行清理。

资讯中心的 Feed 时间线只读取 `IFeedEntryRepository` 和 ACTIVE 目录投影，不取得目录写服务。ViewModel 每页请求 50 条，按分类、Feed、本地日期边界和有界 FTS 关键词构造查询；筛选代次会丢弃过期分页结果，追加页同时记录仓储偏移并按条目 ID 去重。目录同步事件的版本高于已加载快照时，ViewModel 会回到 UI 上下文重新读取 ACTIVE 目录、重建筛选项并保留仍有效的分类/Feed 选择。固定高度的 `PagedListBox` 使用 `VirtualizingStackPanel` Recycling 模式，在接近底部时请求下一页；选择项映射为现有 `RichArticleView` 的只读模型，正文仍走原生净化渲染。同步或网络失败只更新“离线缓存/最后抓取/目录同步”状态，不清空已显示条目。

首页 `DashboardViewModel` 是只读聚合层：并行读取 ACTIVE Feed 首屏、目录显示名、旧 `news_articles`、热点、媒体任务和 favorites 计数，不触发网络请求或目录写入。旧早报保留在原表中以维持 schema v2 搜索/阅读兼容；首页和历史搜索在展示层按规范 URL/内容指纹合并重复条目。空目录的新建 Feed 表单使用 `FeedCompatibilitySeed` 预填 `https://daily.juya.uk/rss.xml`，因此兼容来源仍可经管理员安全发现流程纳入共享目录。

参数化语句与事务由仓储负责；页面无法取得原始数据库连接。

P2-20 已在 Core 建立本地日历计划换算，并由 schema v22 和 `ILocalScheduledTaskRepository` 保存计划定义、错过执行策略、启用状态与下一次 UTC 游标。计划显式保存时区 ID，once/daily/weekly/monthly 都计算严格晚于给定时刻的下一次 UTC；月末收敛到短月最后一天，春季 DST 缺口移动到第一个有效分钟，秋季重叠固定选择较早的 UTC 时刻。禁用会清空待执行游标，重新启用从变更时刻重算；过期单次计划可以保持禁用但不能重新启用。写入使用单调更新时间守卫：较旧状态拒绝，相同时间戳只接受 payload 完全相同的幂等重放。

schema v23 和 `ILocalScheduleRunRepository` 将实际执行窗口与计划定义分离。`scheduled_for` 严格早于调用方给定的 `missedBeforeUtc` 才属于漏跑：RunOnce 只领取持久游标代表的一个窗口并把下一游标推进到 `nowUtc` 之后，Skip 只推进游标且不写伪执行历史；等于边界的窗口仍正常领取。窗口插入与计划游标推进处于同一 `BEGIN IMMEDIATE` 事务，插入失败会一起回滚；`(schedule_id, scheduled_for)` 主键和活动窗口排除保证重复启动只产生一个逻辑窗口。领取优先恢复 PENDING 或租约到期窗口，每次接管生成新令牌并增加尝试次数；到期即失权，旧令牌的续租、完成、取消和释放都会拒绝。续租时间单调不缩短，相同更新时间只允许相同到期时间幂等重放。单次计划在窗口落盘或按 Skip 推进后自动禁用，但已落盘窗口仍可独立恢复。

`LocalScheduleProcessor` 只领取已注册 `ILocalScheduledTaskHandler.ScheduleId` 对应的窗口，并在注册时拒绝非法/重复 ID 和非幂等实现；持久租约只能提供至少一次执行，具体处理器不能取得租约令牌或自行提交窗口状态。处理器按租约三分之一周期续租，业务完成前再次检查计划代际，异常把窗口释放为 PENDING，宿主停止尽力释放，所有权丢失则旧 owner 静默退出。计划领取与游标推进使用同一时间戳，之后任意计划写入都会使 `local_scheduled_tasks.updated_at` 大于窗口 `created_at`，形成无需新字段的持久取消代际；即使先禁用后快速重新启用，旧窗口仍必须取消。Complete/Release 的单条 SQL 还要求计划行存在且代际未变，删除、新代际和迟到 owner 都不能越过最终护栏；未持有或过期的旧代际/孤儿窗口会直接收敛为 Cancelled。

生产组合根已注册两个计划仓储、通用处理器和 `LocalScheduleBackgroundService`。当前没有注册具体 `ILocalScheduledTaskHandler`，因此后台安全空转，不读取或执行未知计划；P2-21 将提供每日/每周摘要处理器、稳定计划 ID、输入边界和管理 UI。在此之前定时摘要仍不是可用产品功能。

## 4. 密钥与认证

- 自备 Groq/DeepSeek Key 写入 `%LocalAppData%\LenxTool\Secrets\secrets.dat`，使用 Windows DPAPI `CurrentUser` 加密并通过产品 entropy 隔离。
- `IAccountSessionService` 是桌面会话边界：短期 access token 只驻进程内存，refresh token 复用 `ISecretStore` 以 DPAPI CurrentUser 保存；启动恢复后必须通过 `/v1/me` 重新取得最小用户与额度快照。
- `WorkerAccountSessionService` 用会话代次和单飞刷新协调并发 401；同一失效会话只轮换一次，每个原请求最多携带新 access token 重放一次。失败、重放或退出会先清除内存状态，再尽力更新 DPAPI 文件。
- 日志过滤 Authorization、Cookie、password、api key、refresh token、音频 multipart 正文和大段模型内容。
- Worker 中真实共享 Key 仅来自 Secret Binding。D1 保存密码摘要、邀请码摘要、角色、额度、聚合用量、刷新令牌摘要、共享目录/AI 策略、受限规则定义/版本与审计元数据，不保存匹配条目或内容处理结果。
- 额度使用“预留—结算—释放”状态机；D1 原子条件更新确保并发请求不能超过余额。

## 5. 统一错误

`AppError` 包含 `Code`、`Title`、`UserMessage`、`Suggestion`、`TechnicalDetails`、`Provider`、`RequestId`、`RetryAfter`、`IsRetryable`。HTTP 映射：

- 400：请求参数或文件不被服务接受。
- 401/403：凭据无效、过期或账号无权限。
- 429：服务限流；Groq 额外解析限制、已用量和 Retry-After。
- 500～599：服务商暂时故障。
- 超时：服务响应超时，可重试或切换本地。
- 网络中断：展示缓存或提示检查网络。

UI 错误卡根据能力显示重试、复制脱敏详情、打开设置、切换本地 Whisper、打开日志。

## 6. UI 架构与视觉系统

- `ShellViewModel` 管理路由、全局命令和状态，不直接执行页面业务。
- `FeedAdminViewModel` 只面向管理员会话编排安全发现、版本化目录写入和本地快照刷新；写入客户端不持有 token，授权仍由 Worker 执行。管理页使用原生 WPF 控件、语义资源、自动化名称和窄窗滚动边界。
- `AutomationAdminViewModel` 只在管理员路由中编排规则 `ALL` 快照、图形草稿、只读模拟和版本化发布。`IFeedAutomationRuleAdminService` 复用安全会话，以独立 `automation-all` ETag/`If-Match` 和幂等键调用 Worker；响应仍经过 Core 白名单验证，客户端角色显示不替代 Worker RBAC 与 D1 审计。
- `IFeedAutomationRuleSimulationService` 只依赖本地条目与目录的读取接口，把最近条目投影为解释器上下文并返回命中和计划动作；它不依赖运行账本、动作队列、AI、媒体或通知接口，因此模拟路径在结构上不能提交副作用。
- 主题资源分为 Foundations、Colors、Typography、Controls、Components；所有颜色使用语义资源。
- Soft Structuralism：用 1px 结构线、低海拔表面、明确留白和克制圆角表达层级。
- Asymmetrical Bento：信息权重决定卡片跨度；窄窗转为单列而非等宽卡片墙。
- 动画只改变 `Opacity` 与 `TranslateTransform`，减少动画时长为 0。
- 每日早报使用原生 WPF 富文本阅读视图；WebView2 仅为未来其他受控 HTML 能力保留，启用时导航必须由 allowlist/外部浏览器策略拦截。

## 7. 可观测性

本地滚动 JSON Lines 日志位于 `%LocalAppData%\LenxTool\Logs`，默认保留 14 天。事件包含时间、级别、事件 ID、错误码、供应商、请求 ID、耗时和任务 ID，不含密钥或完整内容。崩溃处理生成脱敏诊断文件，并保持 UI 可显示恢复提示。

## 8. 发布与回滚

自包含发布不是单文件，以便 WebView2/native 依赖和差分下载可诊断。Inno Setup 使用固定 AppId，安装到 `{localappdata}\Programs\LenxTool`，覆盖升级前关闭应用；卸载器不删除用户数据。回滚采用重新安装上一个已签名版本，数据库只使用向前兼容迁移；破坏性迁移需另行 ADR。

## 9. 管理员策展 RSS 的 P0/P1 架构

后续资讯架构采用“Worker/D1 权威共享目录 + 桌面客户端本地抓取/缓存”：管理员通过服务端授权的写端点维护 Feed、分类和策略，普通用户只读同步目录；文章正文、AI 结果、字幕和本地文件仍不写入 D1。详细理由和备选方案见 [ADR-001](decisions/ADR-001-admin-curated-rss.md)，实施批次见 [RSS 集成总路线图](plans/RSS_MASTER_ROADMAP.md)。

P0/P1 当前已完成 Worker 身份生命周期、D1 共享目录/AI 策略/自动化规则 schema、管理员单项与原子批量写 API、目录和规则 ACTIVE/ALL 快照，以及桌面安全会话、账号/角色/额度、订阅管理和规则管理 UI。本地 schema v23 已覆盖目录/抓取状态、Feed 条目与七类 FTS、私人阅读状态、离线资源/全文、AI 缓存与任务、规则快照/运行/动作租约、媒体投递、三类应用内通知、显式视图覆盖、ACTIVE 智能视图缓存、持久化条目导出任务、本地计划定义及唯一计划窗口。目录、规则和智能视图写入均以服务端 admin 角色为授权真相，使用相互独立的 `If-Match` 单调版本、`Idempotency-Key`、参数化 SQL、不可变历史/最小审计和原子结果；ACTIVE/ALL 由服务端角色隔离，桌面角色只控制入口可见性。OPML 在客户端完成有界 XXE-safe 解析、选择和逐项安全发现后，才由 Worker 原子批量提交；目录导出只使用公开字段。D1 智能视图只保存封闭筛选定义，不保存文章正文、AI 结果、字幕、本地文件或私人状态。

P2-05 为共享智能视图建立与目录/规则解耦的第三条权威配置流：Worker D1 0009 使用独立 `view_set_version`，ACTIVE 向所有认证用户返回启用视图，ALL 及 POST/PATCH/DELETE 只允许管理员；每次写入通过条件版本、幂等键、事务守卫、不可变版本和最小审计提交。定义只含名称、排序、启用状态，以及 Feed/分类、内容类别、已读、收藏、关键词和发布时间窗口，不含 SQL、正则、脚本、URL 或内容字段。桌面 schema v20 只保存最近一次完整验证的 ACTIVE 快照；后台同步对旧/同版本、ALL 冒充、停用项、重复 ID、超限或畸形响应失败关闭并保留上次缓存。已读与收藏筛选只在 `FeedSmartViewValidator.Apply` 生成本地查询时读取私人状态，不进入 Worker 请求或缓存定义之外的字段。

资讯时间线把已发布智能视图作为独立于临时筛选的显式模式：选择不查询，套用前重新读取 SQLite 中的最新 ACTIVE 快照，再由 Core 验证器生成有页界的本地查询；视图被删除时拒绝从旧内存执行，手动应用或清除临时筛选会退出该模式。管理员使用独立 `SmartViewAdminViewModel` 和原生封闭编辑控件；客户端角色只决定入口/命令可见性，Worker 仍是最终授权来源。写入返回或后续 ACTIVE 缓存同步期间若角色被撤销，ViewModel 保持清空状态，不以迟到结果恢复管理数据。

P0 终验（2026-07-24）沿用上述边界并完成闭环证据：管理员登录后的目录写入、只读快照和审计在真实 workerd/D1 中串行验证；本地抓取/缓存层验证 OPML 安全处理、断网保留、单源故障隔离、schema v2→v7 原位迁移及 10k 条目分页虚拟化。终验不引入新的云端正文存储或客户端授权旁路。

P1-A 已完成：私人状态写入与共享目录版本隔离，180 天清理保护有状态条目，时间线与历史页提供已读、收藏、标签、备注和阅读进度；隔离的真实 WPF 运行测试覆盖长文滚动、键盘焦点、进度写入和重建视图后的 SQLite 恢复。P1-06 已将阅读器图片统一接入 `IArticleImageDownloader`：先读取 `IEntryAssetStore`，未命中时逐跳复用 `FeedNetworkPolicy` 和共享固定-IP 连接工厂，限制 HTTPS/端口、DNS 结果、重定向、并发、单资源、每篇资源数与网络字节；只接受魔数与 MIME 一致的 PNG/JPEG/GIF/BMP/WebP，SVG 和伪装内容不会进入缓存。失败 URL 在短期内返回稳定占位，调用方取消不进入失败缓存。

P1-07 以 Core `IArticleContentExtractor` 建立全文边界：调用方只提交 URL，取得最终 URL、元数据、类型化正文块、警告和提取版本。Infrastructure 下载阶段逐跳复用 `FeedNetworkPolicy`/固定-IP handler，并独立限制总超时、重定向、下载/解压大小、HTML MIME 和同主机并发；编码阶段按 BOM、HTTP charset、HTML meta 和有警告的兼容回退处理。`HtmlAgilityPack` 仅接收已经下载到内存的 HTML，不能访问网络，DOM 深度/节点、正文候选/规模限制和白名单净化由 LenxTool 控制；Core 契约不含第三方类型，P1-08 队列和未来替换实现不需要改变调用方。

P1-08～P1-20 在上述边界上形成闭环：全文、AI 和规则动作都先写可恢复本地任务/运行账本，再在 SQLite 事务外执行网络或模型调用；策略和 ACTIVE 规则使用独立版本快照，Feed 写入成功后才计算确定性计划。七类受限动作由显式处理器按类型领取，稳定幂等键、随机租约和终态阻止重放；`SendToMedia` 只接收经附件分类和逐跳 SSRF 校验的音视频，成功下载后原子登记来源与媒体任务；`Notify` 只写不含正文/URI 的本机收件箱。统一搜索通过 schema v17 的七类投影稳定分页，保留维护通过同一候选 SQL 和有界批次保护私人状态及活动工作。

P2-02 在该边界上增加本地图片流：分类查询以原始稳定 continuation 分块扫描，普通时间线不承担后过滤成本；图片页首次进入才加载并以虚拟化三列行展示。缩略图复用 P1 安全下载边界，网络响应流式写缓存，缓存读取以有界缓冲校验哈希后返回文件流，WPF 只按目标像素宽度解码。目录的独立显式视图状态由本地 schema v18 与 D1 0007 保存，历史非 Article 覆盖和旧 v1 客户端语义均向前兼容。

P2-03 继续使用同一分类与分页边界增加本地音频流：音频页惰性组合通用内容集合，选择与筛选不触发网络；`IFeedAudioPlaybackService` 将 WPF 系统媒体状态机与 ViewModel 隔离，只有用户显式播放才打开已验证 Audio enclosure。播放位置作为本机 `EntryState.Progress` 的消费百分比节流保存，不新增 schema 或云同步字段；来源切换会关闭旧播放器，ViewModel 还会按规范化来源丢弃迟到事件。转写仍通过 `IFeedMediaDeliveryService` 完成逐跳 SSRF、MIME/签名、大小、超时、取消与幂等登记，播放链本身不创建任务。无法内置播放的条目只暴露经过二次确认的安全原文浏览器回退。

P2-04 增加不内嵌远程播放器的视频流：可信封面仍由图片 enclosure 分类与 `FeedThumbnail` 安全缓存链处理，视频 enclosure 不因列表选择而请求。App 层投递计划以可替换磁盘探针组合声明大小、512 MiB 上限、固定目标、64 MiB 保留空间和同源台账；未知或至少 20 MiB 的下载需要二次确认，确认前重做计划。Infrastructure 在下载前和响应长度可知时再次检查空间，视频临时文件保留真实媒体扩展并在移动/登记前由 Media Foundation 打开验证可读音轨；失败、取消或不兼容均删除临时文件且不创建任务，成功才进入既有 Feed-media 幂等与 Whisper 处理链。

P2-08 为未来导出适配器建立第四条独立权威配置流：Worker D1 0010 的 `policy_set_version` 只覆盖封闭集成类型、开关和精确主机数组，ACTIVE 给认证用户，ALL 与整集 PUT 仅限管理员；版本、幂等、事务守卫、不可变快照和审计均与目录、规则、智能视图解耦。桌面不新增集成策略 SQLite 快照，因为外部集成本身要求在线，读取和健康预检直接使用 Worker 的最新验证快照；个人 TargetId 与 HTTPS 地址可进入本机设置，但凭据经哈希槽位只进入 DPAPI CurrentUser。`IEntryIntegrationHealthService` 在提供商专用探针前执行策略、精确主机、HTTPS/443、DNS 全地址分类、超时、取消、冷却和全局并发；探针只取得已批准 URI、固定地址和单次凭据，返回无外部详情的封闭状态。生产 DI 的探针集合为空，因此此阶段没有隐含第三方网络能力。

P2-12 在该历史通用边界之外增加一个客户端专用的 Eagle 本机链路。Worker/D1 仍只保存九种策略的开关和主机 JSON，但 Eagle 与 Obsidian 的 `allowedHosts` 必须为空；Eagle 的端点只在本机单条版本化设置中保存，不进入 D1、凭据存储或通用 HTTPS 健康模型。`EagleApiClient` 使用关闭代理、重定向、Cookie 和自动解压的专用 handler，只接受带显式端口的 IP-literal loopback HTTP 根地址，并以有界 `app/info`、`library/info` 探测验证官方 Web API V2、Windows 4.0 Build 21+ 和已打开资源库。资源库路径仅用于在内存生成不透明修订，不进入 DTO 显示、异常或持久设置。

显式阅读器动作在 ACTIVE 策略和本机目标通过后探测当前资源库，并以 `default.<端点修订>.<资源库修订>` 创建队列目标；该值同时进入既有幂等键。后台执行先取得“当前端点快照 + 进程内代际租约”，并持有到最后一次 Eagle 调用结束；同进程保存新端点必须等待旧代际导出释放，因此端点不会在一次执行中途换代。资源库修订则在首次查询前、已有条目查询后、图片下载后和新增返回后按需复核，探测到的持续变化以 `Conflict` 关闭。同一内容在不同资源库得到不同幂等键和稳定 Eagle ID。图片只从统一附件分类器选择 URL 允许、类型已验证的 PNG/JPEG/GIF/WebP/BMP，声明及实际流都限制 12 MiB；下载后交叉验证 MIME/魔数，再以 data URI 与有界标题、来源、标签投递。远程图片 URL 不交给 Eagle。写前 `item/get`、自定义稳定 ID 和不确定 POST 后复查把队列的至少一次执行收敛为不重复新增。该切片不改变本地 schema v21，也不增加 D1 migration 或依赖。

Eagle 官方 `item/add` 不接受资源库 ID/修订，也没有“当前库仍为 X 才写入”的条件参数，因此资源库探测与新增不能组成原子操作。写后探测能把保持在新库的切换报告为 `Conflict`，但不能撤销可能已经发生的写入；在写入窗口内切换后又切回原库的 ABA 变化也无法由该 API 观测。端点租约同样只协调本 LenxTool 进程内的设置保存，不覆盖另一个进程直接修改本机设置。用户必须在导出任务进入终态前保持 Eagle 当前资源库不变；受控真实 Eagle 验收需要专门覆盖这一人工约束，文档不得声称适配器能原子阻止误投。

P2-13 把首个公网网络适配器接入 P2-08/P2-09 边界。Zotero 目标只表示个人库 User ID、显式 webpage/journalArticle 类型、摘要子笔记开关和默认关闭的附件开关；固定 API 根地址由 User ID 派生，不能配置任意主机。目标全部行为进入不透明队列修订，API key 仍由 `IEntryIntegrationCredentialStore` 的 DPAPI CurrentUser 槽位隔离。设置保存不联网，连接测试要求当前表单与已保存代际一致；健康服务先执行 ACTIVE 精确主机、HTTPS/443、DNS 全地址、冷却与超时，再由 Zotero 探针验证 `/keys/current` 的用户与 library/write/notes/files 权限。阅读器只有显式动作才入队，后台重新验证策略、代际、凭据和能力，并持有目标租约到最后一次上传结束。

`ZoteroEntryExporter` 不推断条目类型，也不读取私人备注或正文：来源使用规范 HTTP(S) URL，作者保持不可可靠拆分的 single-field name，日期取 published/updated，categories 进入有界标签，可选子 note 仅来自 HTML 编码后的 Feed summary。父项、note 和 imported_file attachment 分别从耐久幂等键加角色盐生成官方字符集的 8 位 key，并携带角色化身份标记。`ZoteroApiClient` 对每个目标串行执行，固定 Web API v3、禁代理/跳转/Cookie/解压、钉住所有公网解析地址，并在创建前后读取 key 核对 itemType、父级、URL、附件元数据与标记；匹配对象直接完成，非匹配对象作为不可重试碰撞关闭。`version: 0` 与预分配 key 取代只能短期缓存的 Write Token，适配跨重启队列重放。

附件链在创建 imported_file 子项前由 LenxTool 下载并交叉验证 URL、声明 MIME、响应 MIME、文件魔数和 12 MiB 实际流；支持 PNG/JPEG/GIF/WebP/BMP。创建成功后按 Zotero 官方三阶段上传：官方 file endpoint 返回一次性授权，客户端只把 `prefix + bytes + suffix` 发送到经独立 HTTPS/公网 DNS 钉住的地址且不携带 API key，再回官方 endpoint 登记 upload key。上传正文使用独立两分钟超时，JSON/授权字段/URL/响应仍有界。取消、超时或崩溃不能回滚已经创建的第三方对象；确定性 key、写前/写后身份复查和文件授权的 `exists=1` 语义使重试收敛。本切片复用 schema v21 和 Worker D1 0010，没有本地或云端迁移。

P2-14 在同一耐久队列上增加固定官方目标的 Readwise Reader 适配器。通用个人集成卡选择 Readwise 后把端点锁定为 `https://readwise.io/`、凭据槽位锁定为 `Readwise/default`；保存只写本机设置与 DPAPI CurrentUser，连接测试要求当前表单已保存，并在共享 ACTIVE 策略精确允许 `readwise.io`、HTTPS/443 和全部公网 DNS 地址通过后，以 `Authorization: Token` 调用无写入副作用的 `GET /api/v2/auth/`。阅读器只有当前已选中并显示精确预览的同一 FeedEntry 才允许执行行内或详情动作并以固定 `default.v1` 目标入队，后台重新执行“策略 → 凭据 → API”门控；队列、SQLite 和 D1 都不保存 token。

`ReadwiseEntryExporter` 支持全部五种视图，只映射规范 HTTP(S) 来源、标题、作者、UTC 日期和最多 32 个 categories。正文先取净化内容、为空才回退 Feed summary，规范空白后按 4,000 个 Unicode 文本元素和 16 KiB UTF-8 双重裁剪；同一函数同时产生界面只读预览、队列内容字节数和实际 `summary`，不会发送 `html`、图片、私人备注、AI 私人状态或本机路径。`ReadwiseApiClient` 固定 `POST /api/v3/save/`，关闭代理、跳转、Cookie 与解压，全部 DNS 地址分类后固定连接，每次只允许一个请求并保持 1.2 秒起始间隔；响应头与最多 64 KiB JSON 正文共用 8 秒超时。429/503 的 `Retry-After` 有 24 小时上限且立即作为可重试结果交给耐久队列，不在适配器内长睡眠并占住全局 worker。只有官方定义的 201/200 及合法 ID、`read.readwise.io` HTTPS 地址才算成功，其余写入不确定性保持可重试且不解析/回显第三方正文。

该导出能力按官方同一 URL 去重语义声明可安全重放，但这是“不会创建第二条”的有限幂等：再次保存会把现有文档移到资料库顶部并显示绿色标记，带不同追踪参数的 URL 仍可能创建第二份。因此队列重放可以收敛到同一对象，却不承诺第三方完全无可观察副作用。P2-14 不新增依赖、本地 schema 或 D1 migration，也没有真实 Readwise token/账户写入验收。

P1 终验（2026-07-27）以两条独立数据流验证架构边界：真实 schema v17 SQLite 在重开后的离线库中覆盖 10,000 条 Feed、1,000 个收藏、混合媒体和全文/AI/规则/媒体活动引用，查询、搜索、预览和清理均满足既定预算；真实 workerd/D1 覆盖管理员发布目录/AI 策略/规则、普通用户写入 403、版本不变及应用表/字段内容隐私白名单。Release 回归为 .NET 648/648、Worker 52/52、strict typecheck 和 0 警告构建。该记录关闭 P1 架构交付，不替代生产部署与正式签名发布。

## 10. 统一 Feed 发现协调

DISC-01～DISC-03 建立不依赖 UI 的统一发现边界。Core 将输入确定性分类为 URL、RSSHub 路由或关键词，并以规范 Feed URL 合并候选，同时保留类型化来源证据、置信度、健康状态和警告。Infrastructure 的 `IUnifiedFeedDiscoveryService` 并行调用已注册的 `IFeedDiscoveryProvider`，按注册顺序返回每来源报告；一个来源超时、限流、格式损坏或熔断只产生类型化降级状态，不暴露上游响应或异常文本，也不阻断其他来源。

默认注册只有两条数据流：Worker 已知目录使用现有授权会话读取 `/v1/feeds/discoveries`，对 JSON 大小、HTTPS 元数据、分页、枚举、目录 ID、来源证据和警告逐字段验证；direct provider 复用原 `IFeedDiscoveryService`，因此继续执行公网地址分类、完整 DNS 答案验证、固定 IP 连接、逐跳重定向复核、响应/解压大小、MIME、XML DTD/实体和总超时限制。RSSHub 与外部平台仅保留公开 provider 扩展契约，在官方 API、速率限制、许可和隐私条款完成审核前不注册。

每个 provider 拥有独立并发门闩、总等待/执行超时、成功结果内存缓存和进程内熔断状态；策略上限在协调器构造时验证，缓存条目数、候选数和 TTL 均有硬上限。调用方取消始终传播，不写失败缓存；provider 结果必须让全部证据和具名警告归属于该 provider，不能伪造其他来源。超时使用 .NET 的 [`CancellationTokenSource.CancelAfter`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.cancelafter?view=net-10.0)，多 provider 组合沿用内建 DI 的 [`IEnumerable<T>` 注册顺序语义](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/service-registration)，缓存和熔断时钟通过 [`TimeProvider`](https://learn.microsoft.com/en-us/dotnet/standard/datetime/) 注入以保持可测试性。

## 11. 原生 WPF 共享选择控件

UX-03 保留 WPF 原生控件类型与自动化 Peer，不以无语义容器模拟交互。`Controls.xaml` 提供分段 `TabControl/TabItem`、`CheckBox` 与 `ComboBox`；日期体系独立在 `DateControls.xaml`，避免继续扩大通用资源字典。DatePicker 模板只声明框架要求的 `PART_Root`、`PART_TextBox`、`PART_Button` 和空 `PART_Popup`，由 WPF 在 `OnApplyTemplate` 时把其内部 Calendar 放入 Popup；`CalendarStyle` 再统一弹层、月份/年份和日期按钮。这样既能自定义视觉，也不会切断日期选择回写、键盘焦点、BlackoutDates 和辅助功能链路。

所有颜色使用运行时 `DynamicResource` 语义令牌，主题字典替换后现有控件即时更新；固定尺寸只用于最小命中区和矢量图标，内容区域继续由 WrapPanel、星号列和滚动容器适配窄窗与 DPI。资讯页显式引用共享样式，避免全局隐式 TabControl 模板影响应用内部用于路由的无页签容器。
