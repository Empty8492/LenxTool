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
- `favorites`：通用实体收藏和备注。
- `tags`、`entity_tags`：标签与多态关联。
- `app_settings`：非秘密设置。
- `schema_versions`：已应用迁移及校验和。
- `content_fts`：早报、热点、AI 报告的 FTS5 外部内容索引。

参数化语句与事务由仓储负责；页面无法取得原始数据库连接。

## 4. 密钥与认证

- 自备 Groq/DeepSeek Key 写入 `%LocalAppData%\LenxTool\Secrets\secrets.dat`，使用 Windows DPAPI `CurrentUser` 加密并通过产品 entropy 隔离。
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
- 主题资源分为 Foundations、Colors、Typography、Controls、Components；所有颜色使用语义资源。
- Soft Structuralism：用 1px 结构线、低海拔表面、明确留白和克制圆角表达层级。
- Asymmetrical Bento：信息权重决定卡片跨度；窄窗转为单列而非等宽卡片墙。
- 动画只改变 `Opacity` 与 `TranslateTransform`，减少动画时长为 0。
- 每日早报使用原生 WPF 富文本阅读视图；WebView2 仅为未来其他受控 HTML 能力保留，启用时导航必须由 allowlist/外部浏览器策略拦截。

## 7. 可观测性

本地滚动 JSON Lines 日志位于 `%LocalAppData%\LenxTool\Logs`，默认保留 14 天。事件包含时间、级别、事件 ID、错误码、供应商、请求 ID、耗时和任务 ID，不含密钥或完整内容。崩溃处理生成脱敏诊断文件，并保持 UI 可显示恢复提示。

## 8. 发布与回滚

自包含发布不是单文件，以便 WebView2/native 依赖和差分下载可诊断。Inno Setup 使用固定 AppId，安装到 `{localappdata}\Programs\LenxTool`，覆盖升级前关闭应用；卸载器不删除用户数据。回滚采用重新安装上一个已签名版本，数据库只使用向前兼容迁移；破坏性迁移需另行 ADR。

## 9. 已决策但尚未实现：管理员策展 RSS

后续资讯架构采用“Worker/D1 权威共享目录 + 桌面客户端本地抓取/缓存”：管理员通过服务端授权的写端点维护 Feed、分类和策略，普通用户只读同步目录；文章正文、AI 结果、字幕和本地文件仍不写入 D1。详细理由和备选方案见 [ADR-001](decisions/ADR-001-admin-curated-rss.md)，实施批次见 [RSS 集成总路线图](plans/RSS_MASTER_ROADMAP.md)。

本节描述的是已接受的后续架构方向，不表示当前版本已经具备通用 RSS、管理员订阅页或普通用户目录同步能力。
