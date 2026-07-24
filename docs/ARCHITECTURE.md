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
  -> ISubtitleExporter -> 原文/双语 SRT/TXT
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
- `content_fts`：早报、热点、AI 报告和 Feed 条目的统一 FTS5 内容索引。
- `feed_entry_search_documents`：Feed 条目的搜索文档投影；schema v5 触发器负责与 `content_fts` 同事务同步。
- `user_entry_states`：按 `(entry_id, local_profile)` 保存本机已读、收藏、阅读进度、私人备注和更新时间；不建立 Feed 外键，确保目录软删除或条目清理不会误删私人状态。
- `entry_assets`：按条目和来源 URL 建立本地资源索引，记录内容哈希、MIME、大小及创建/访问时间；物理缓存以 SHA-256 命名并在读取时校验。

本地数据库当前版本为 schema v7。schema v3 用于字幕翻译服务和 token 用量，v4 新增 Feed 目录/条目，v5 回填 Feed FTS 并安装同步触发器，v6 新增本机 `user_entry_states` 私人阅读状态，v7 新增 `entry_assets` 离线资源索引；旧 v2 数据会依次应用全部迁移，任何一步失败均在事务中回滚且不提升版本。

`IFeedCatalogRepository` 是共享目录的本地边界。服务端快照写入时，分类、Feed、作用域、目录版本、生成时间和最后同步时间在同一事务提交；版本倒退在删除前拒绝，失败回滚整批替换。目录移除不会删除 `feed_entries`，仍存在 Feed 的 `feed_fetch_state` 会跨替换保留。读取状态、分类和 Feed 使用同一读事务，ACTIVE 投影过滤停用资源；若本地只同步过 ACTIVE，ALL 查询返回不可用而不是伪造管理员完整目录。

`IFeedRefreshService` 只从 ACTIVE 投影选择到期 Feed，并通过 `FeedNetworkPolicy` 与固定地址传输执行条件 GET。调度有全局并发上限和 Feed 级单飞门闩；每次重定向重新做 SSRF 校验，跨 authority 不携带条件验证器。200 的提交顺序固定为“解析 → `IFeedEntryRepository` 单事务 upsert/FTS → `IFeedFetchStateRepository` 保存 ETag/Last-Modified 与下次时间”，因此条目写失败不会提交新验证器；状态保存失败最多造成下一次幂等重抓。304 不调用条目写入。仓储查询按稳定时间/ID 顺序分页，并以目录表关联 Feed/分类；清理接口排除收藏、标签和 `user_entry_states`，完整私人阅读闭环完成前不进入后台自动调度。

资讯中心的 Feed 时间线只读取 `IFeedEntryRepository` 和 ACTIVE 目录投影，不取得目录写服务。ViewModel 每页请求 50 条，按分类、Feed、本地日期边界和有界 FTS 关键词构造查询；筛选代次会丢弃过期分页结果，追加页同时记录仓储偏移并按条目 ID 去重。目录同步事件的版本高于已加载快照时，ViewModel 会回到 UI 上下文重新读取 ACTIVE 目录、重建筛选项并保留仍有效的分类/Feed 选择。固定高度的 `PagedListBox` 使用 `VirtualizingStackPanel` Recycling 模式，在接近底部时请求下一页；选择项映射为现有 `RichArticleView` 的只读模型，正文仍走原生净化渲染。同步或网络失败只更新“离线缓存/最后抓取/目录同步”状态，不清空已显示条目。

首页 `DashboardViewModel` 是只读聚合层：并行读取 ACTIVE Feed 首屏、目录显示名、旧 `news_articles`、热点、媒体任务和 favorites 计数，不触发网络请求或目录写入。旧早报保留在原表中以维持 schema v2 搜索/阅读兼容；首页和历史搜索在展示层按规范 URL/内容指纹合并重复条目。空目录的新建 Feed 表单使用 `FeedCompatibilitySeed` 预填 `https://daily.juya.uk/rss.xml`，因此兼容来源仍可经管理员安全发现流程纳入共享目录。

参数化语句与事务由仓储负责；页面无法取得原始数据库连接。

## 4. 密钥与认证

- 自备 Groq/DeepSeek Key 写入 `%LocalAppData%\LenxTool\Secrets\secrets.dat`，使用 Windows DPAPI `CurrentUser` 加密并通过产品 entropy 隔离。
- `IAccountSessionService` 是桌面会话边界：短期 access token 只驻进程内存，refresh token 复用 `ISecretStore` 以 DPAPI CurrentUser 保存；启动恢复后必须通过 `/v1/me` 重新取得最小用户与额度快照。
- `WorkerAccountSessionService` 用会话代次和单飞刷新协调并发 401；同一失效会话只轮换一次，每个原请求最多携带新 access token 重放一次。失败、重放或退出会先清除内存状态，再尽力更新 DPAPI 文件。
- 日志过滤 Authorization、Cookie、password、api key、refresh token、音频 multipart 正文和大段模型内容。
- Worker 中真实共享 Key 仅来自 Secret Binding。D1 保存密码摘要、邀请码摘要、角色、额度、聚合用量、刷新令牌摘要与审计元数据。
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
- 主题资源分为 Foundations、Colors、Typography、Controls、Components；所有颜色使用语义资源。
- Soft Structuralism：用 1px 结构线、低海拔表面、明确留白和克制圆角表达层级。
- Asymmetrical Bento：信息权重决定卡片跨度；窄窗转为单列而非等宽卡片墙。
- 动画只改变 `Opacity` 与 `TranslateTransform`，减少动画时长为 0。
- 每日早报使用原生 WPF 富文本阅读视图；WebView2 仅为未来其他受控 HTML 能力保留，启用时导航必须由 allowlist/外部浏览器策略拦截。

## 7. 可观测性

本地滚动 JSON Lines 日志位于 `%LocalAppData%\LenxTool\Logs`，默认保留 14 天。事件包含时间、级别、事件 ID、错误码、供应商、请求 ID、耗时和任务 ID，不含密钥或完整内容。崩溃处理生成脱敏诊断文件，并保持 UI 可显示恢复提示。

## 8. 发布与回滚

自包含发布不是单文件，以便 WebView2/native 依赖和差分下载可诊断。Inno Setup 使用固定 AppId，安装到 `{localappdata}\Programs\LenxTool`，覆盖升级前关闭应用；卸载器不删除用户数据。回滚采用重新安装上一个已签名版本，数据库只使用向前兼容迁移；破坏性迁移需另行 ADR。

## 9. 分阶段实现中：管理员策展 RSS

后续资讯架构采用“Worker/D1 权威共享目录 + 桌面客户端本地抓取/缓存”：管理员通过服务端授权的写端点维护 Feed、分类和策略，普通用户只读同步目录；文章正文、AI 结果、字幕和本地文件仍不写入 D1。详细理由和备选方案见 [ADR-001](decisions/ADR-001-admin-curated-rss.md)，实施批次见 [RSS 集成总路线图](plans/RSS_MASTER_ROADMAP.md)。

当前已完成 Worker v1 契约、身份生命周期、D1 共享目录 schema、管理员分类/Feed 单项与原子批量写 API、版本化只读目录、桌面安全会话与账号/角色/额度 UI、本地 schema v7、目录原子仓储、安全发现/解析、条件调度、条目 FTS/查询、P0-C 兼容安全检查点、P0-15 管理页、P0-16 OPML 管理、P0-17 普通用户 Feed 时间线、P0-18 管理员 Feed 健康诊断、P0-19 首页真实数据兼容、P0-20/P1-01 私人阅读状态、P1-02 收藏标签备注仓储、P1-03/P1-04 私人阅读交互与进度恢复，以及 P1-05 离线资源索引。目录写入以服务端 admin 角色为授权真相，使用 `If-Match` 单调版本、`Idempotency-Key`、参数化 SQL 和同一 D1 batch 内的资源写入/最小审计/幂等结果；OPML 批量使用内存模拟后约 12 条聚合语句原子提交，跨操作 `categoryRef` 可引用同批次较早创建的分类，成功只增加一次版本。桌面导入在写入前先执行有界 XXE-safe 解析、预览分类、用户选择、安全 Feed 发现和发现后重复复核，任一前置项失败不会提交；导出采用目录字段白名单。目录读取以同一 D1 batch 生成确定排序的原子快照，ACTIVE/ALL 由服务端角色隔离，并用强 ETag、304 和超前版本拒绝保护客户端缓存；本地仓储拒绝版本倒退并以读写事务维持完整快照，桌面角色只控制入口可见性且角色降级后清空管理员投影，D1 仍不保存文章正文。解析层的 20 个独立 RSS/Atom fixture 覆盖中文、ISO-8859-1 与 UTF-16 LE/BE，安全抓取测试覆盖单源隔离、缓存保留、SSRF、XXE、响应上限和重定向绕过。

P1-A 已完成：私人状态写入与共享目录版本隔离，180 天清理保护有状态条目，时间线与历史页提供已读、收藏、标签、备注和阅读进度；隔离的真实 WPF 运行测试覆盖长文滚动、键盘焦点、进度写入和重建视图后的 SQLite 恢复。P1-06 已将阅读器图片统一接入 `IArticleImageDownloader`：先读取 `IEntryAssetStore`，未命中时逐跳复用 `FeedNetworkPolicy` 和共享固定-IP 连接工厂，限制 HTTPS/端口、DNS 结果、重定向、并发、单资源、每篇资源数与网络字节；只接受魔数与 MIME 一致的 PNG/JPEG/GIF/BMP/WebP，SVG 和伪装内容不会进入缓存。失败 URL 在短期内返回稳定占位，调用方取消不进入失败缓存。

P0 终验（2026-07-24）沿用上述边界并完成闭环证据：管理员登录后的目录写入、只读快照和审计在真实 workerd/D1 中串行验证；本地抓取/缓存层验证 OPML 安全处理、断网保留、单源故障隔离、schema v2→v7 原位迁移及 10k 条目分页虚拟化。终验不引入新的云端正文存储或客户端授权旁路。
