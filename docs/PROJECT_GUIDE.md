# Lenx Tools 项目文档

## 1. 项目定位

Lenx Tools 是面向 Windows 10/11 x64 的本地优先桌面工具。项目从 `L:\RealTimeTranslator` 的功能经验中重构而来，但没有继承旧项目的单窗口业务堆叠、明文密钥、固定管理员口令、IE WebBrowser 或捆绑模型做法。旧目录始终只读。

当前版本：`0.1.0` 可运行预览基线。目标框架为 .NET 10，桌面 UI 使用 WPF、MVVM 与依赖注入；云端共享额度服务使用 Cloudflare Workers + D1。仓库内现有 `Release\LenxTool_Setup.exe` 早于本轮源码修复，不能视为当前版本安装包。

## 2. 目录结构

```text
LenxTool/
├─ src/
│  ├─ LenxTool.App/             WPF、主题、控件、ViewModel、组合根
│  ├─ LenxTool.Core/            领域模型、契约、错误、纯算法
│  └─ LenxTool.Infrastructure/  SQLite、网络、媒体、DPAPI、更新、系统适配器
├─ tests/
│  ├─ LenxTool.Core.Tests/
│  ├─ LenxTool.Infrastructure.Tests/
│  └─ LenxTool.App.Tests/
├─ cloud/LenxTool.Worker/       Worker API、D1 migration、安全测试
├─ installer/                   Inno Setup、WebView2/Windows App Runtime 资产、公钥
├─ tools/LenxTool.ReleaseTool/  离线签名与验证工具
├─ scripts/Build-Release.ps1    可重复发布脚本
├─ Release/                     自包含发布、安装包、便携版、更新清单
└─ docs/                        规格、架构、安全、部署、测试和使用文档
```

## 3. 架构与依赖方向

依赖方向固定为 `App -> Infrastructure -> Core`，Core 不引用 WPF、SQLite、HTTP 或系统 COM。

- App：只负责交互状态、导航、命令和视觉。窗口代码不承载业务规则。
- Core：定义 `INewsRepository`、`ITranscriptionService`、`ISecretStore`、`IUpdateService` 等边界，以及字幕、错误、版本和工具算法。
- Infrastructure：实现 SQLite 事务、网络客户端、DPAPI、Whisper、Word COM、数据库维护和更新下载。
- Worker：是独立安全边界，真实共享 Groq/DeepSeek Key 只存在于 Worker Secret。

应用入口 `App.xaml.cs` 创建 Generic Host，注册 `HttpClientFactory`、服务和 ViewModel，执行数据库迁移后再显示主窗口。

## 4. 桌面功能

### 4.1 应用外壳

- 深石墨侧栏、暖白内容区、完整深色资源。
- Soft Structuralism + Asymmetrical Bento 布局。
- 矢量 Path 图标，无 Emoji 功能图标。
- PerMonitorV2 DPI、长路径、100%～200% 缩放基础支持。
- `Ctrl+K` 命令面板与键盘导航。
- 减少动画选项；视觉仅使用轻量透明度/位移设计约束。

### 4.2 资讯与早报

- 聚合每日早报，以及通过 NewsNow API 获取的 13 个热点平台：TrendRadar 默认的知乎、抖音、bilibili 热搜、华尔街见闻、贴吧、百度热搜、财联社热门、澎湃新闻、凤凰网、今日头条、微博，并保留 GitHub 与 Hacker News。
- 数据源部分失败时返回成功来源；完全断网时回退 SQLite 缓存。
- 文章以内容指纹去重并保存；FTS5 覆盖早报、热点和 AI 报告。
- “资讯列表”“每日早报”“热点趋势”和“AI 报告”均为左侧一级入口，复用同一份资讯缓存与状态；早报默认选择当天，有缓存的历史日期可从日期下拉框切换。
- “资讯列表”（原“Feed 时间线”）按 50 条分页读取本地缓存，支持全部/分类/Feed、发布日期和关键词组合筛选；滚动接近底部自动追加，列表使用 Recycling 虚拟化，选择条目后在现有原生只读阅读器中显示。条目会水合本机 `user_entry_states`，可切换已读/收藏并显示进度，不修改共享目录；阅读器滚动位置按 500 ms 防抖写入，重新选择条目后恢复，支持“从头阅读”重置。
- 断网或目录同步失败时，时间线保留已缓存条目，并显示最后抓取与目录同步时间；该页面不提供任何共享订阅编辑控件。
- 热点按平台卡片分组并使用平台内排名；列表条目可安全打开 HTTP/HTTPS 原文，卡片本身不创建嵌套滚动容器。
- 热点顶部提供 13 个带选中勾号的来源胶囊，可多选过滤、显示已选数量和全选恢复；每日趋势报告只分析当前选中的来源。
- 早报正文使用原生 WPF 富文本视图，不再依赖 WebView2；支持 RSS 封面图、标题层级、项目列表、可点击 HTTP/HTTPS 链接和朗读标记清理，避免运行时初始化异常造成黑屏。
- 数据层已预留收藏、标签、实体标签、备注、AI 报告和 180 天保留策略所需表结构。

热点平台 ID、显示名和预期域名参考 [TrendRadar 默认配置](https://github.com/sansan0/TrendRadar/blob/master/config/config.yaml)，接口格式来自其依赖的 [NewsNow](https://github.com/ourongxing/newsnow)。LenxTool 仅复用公开的数据源约定，并自行实现 .NET 请求、校验、缓存和 WPF 展示。

### 4.3 媒体工作台

- 批量选择音频/视频，顺序队列执行。
- NAudio Media Foundation 将媒体统一为 16 kHz、16-bit、mono WAV。
- Groq Whisper：自备 Key，HTTP 请求级授权，超时/断网/429 分级错误。
- 本地 Whisper：Whisper.net CPU runtime；导入 `ggml-*.bin`，模型不进入主安装包。
- 长音频按 5 分钟切片、10 秒重叠；后片携带最多 180 字上下文。
- 分片结果使用中点交接、连续重复消除和 `no_speech_prob/avg_logprob` 过滤。
- 任务支持进度、取消、失败状态、历史持久化；异常退出的 Running 任务会恢复为可重试失败。
- 转写完成后字幕片段直接写入 SQLite；已有 SRT 也可导入同一任务历史。
- 媒体工作台可选择字幕任务、目标语言和 DeepSeek 模型，逐批显示并持久化翻译进度；取消或失败后可从精确断点继续。
- 可导出 UTF-8 无 BOM 的原文 SRT、译文 SRT、双语 SRT 和 TXT 到 `%LocalAppData%\LenxTool\Output`，并直接打开输出。

### 4.4 文档与数据工具

- JSON：格式化、压缩、语法校验、属性排序，以及双栏结构 Diff；Diff 支持根值 `null`、独立错误定位、交换、取消、2 MiB 单侧上限、500 项数量上限，以及单路径 1,024 字符/总路径 256 KiB 的结果预算。
- Base64 和 URL 编解码。
- 文本重复行删除、空白归一和空行折叠。
- SHA-256 文件校验，支持取消。
- Word 转 PDF 位于独立 `IDocumentConverter` 适配器；仅在本机安装 Microsoft Word 时启用。

### 4.5 历史、数据与设置

- 查看最近媒体任务、逐段字幕原文/译文、翻译服务与模型、请求数、输入/输出/总 token 和脱敏错误。
- 从 SQLite 历史重新导出字幕而不再次调用模型，并打开输出文件。
- SQLite 一键备份；恢复前自动备份当前数据库并执行完整性检查。
- Groq/DeepSeek 自备 Key 通过 PasswordBox 输入，保存后立即清空界面内存。
- Key 使用 Windows DPAPI CurrentUser 加密，不进入 SQLite。
- PasswordBox 附加绑定会在空初始值时正确订阅输入事件并保留 TwoWay Binding；空输入时保存按钮禁用，成功或失败均在设置页给出明确状态。
- 启动后台检查更新，设置页支持手动检查、展示版本/大小/日志、下载进度和安装。
- Windows 系统通知默认关闭；启用后可选择通用提示或仅标题、配置静默时段与聚合间隔。系统能力不可用时只降级 Toast，应用内通知仍会持久保存。

## 5. SQLite 数据

数据库：`%LocalAppData%\LenxTool\Data\lenx.db`。

主要表：`news_articles`、`trend_items`、`ai_reports`、`media_jobs`、`subtitle_segments`、`favorites`、`tags`、`entity_tags`、`app_settings`、`feed_catalog_state`、`feed_categories`、`feed_catalog`、`feed_fetch_state`、`feed_entries`、`user_entry_states`、`entry_assets`、`feed_full_text_content`、`feed_full_text_jobs`、`feed_ai_automation_jobs`、`feed_automation_runs`、`feed_automation_action_runs`、`feed_automation_rules`、`feed_media_deliveries`、`app_notifications`、`local_scheduled_tasks`、`local_scheduled_task_payloads`、`local_schedule_runs`、`local_schedule_run_retries`、`feed_digest_requests`、`schema_versions`。

实现原则：

- WAL、foreign_keys、busy_timeout 和 integrity check。
- 所有迁移和批量写入使用事务。
- 共享目录的分类、Feed、目录版本和同步时间在同一事务中替换；版本倒退在写入前拒绝，失败保留上一完整快照，移除 Feed 不级联删除本地文章。
- 升级前写入 `Data\Backups`。
- FTS5 虚表与触发器同步全文字段。
- 损坏与迁移失败映射为独立中文错误，不吞异常。
- 收藏记录不参与自动过期；清理策略默认 180 天。

## 6. 统一错误与日志安全

`AppError` 包含错误码、标题、用户说明、建议、技术详情、服务商、请求 ID、Retry-After 和可重试标志。400、401、403、429、5xx、超时、断网、数据库损坏和更新校验失败具有不同提示。

Groq 429 读取 `Retry-After`、请求限额和剩余量，并计算已用量。`SecretRedactor` 会移除 Bearer token、常见 API Key 形态、密码字段和查询串敏感值。禁止空 catch；仅允许对临时文件清理使用明确的 IOException/UnauthorizedAccessException 降级。

## 7. 安全边界

- 客户端自备 Key：DPAPI CurrentUser。
- 共享 Key：仅 Worker Secret。
- 更新私钥：仓库外离线保存；客户端只嵌入 P-256 公钥。
- 更新：清单 ECDSA-SHA256 签名，安装包 SHA-256 加独立签名。
- Worker：PBKDF2-SHA256 固定 100,000 次（Cloudflare Workers Web Crypto 的生产运行时上限），配合 12 字符首管理员密码下限与认证限流；短期 HMAC access token、refresh token 哈希与轮换、禁用账号即时失效。
- 配额：D1 条件 UPDATE 原子预留，成功后结算，防止并发超额；管理员跳过共享额度。
- Worker 不写入新闻、字幕、音视频或请求正文；音频请求体直接流式转发。

## 8. 更新与安装

- 清单支持多个 HTTPS mirror，客户端无需因 OSS/COS 镜像扩展而升级。
- 使用语义版本选择最高候选版本。
- 支持 `MinimumSupportedVersion` 与 `MandatorySecurityUpdate`。
- 安装器固定 AppId：`{D13CF52E-A89C-4CC6-A888-3CA9F4CCB2B4}`。
- Inno Setup 支持覆盖升级、开始菜单、可选桌面快捷方式、静默安装和卸载。
- WebView2 Evergreen Bootstrapper 随安装器提供并静默检查/安装。
- Windows App Runtime 2.3.1 x64 随安装器提供并静默检查/安装；便携版缺失 Runtime 时只关闭系统通知能力。
- 发布脚本在 Inno Setup 打包前，对 WebView2 和 Windows App Runtime 的缓存或下载文件统一执行固定 SHA-256、有效 Authenticode 和 Microsoft 精确发布者校验。
- 用户数据、模型、设置和密钥位于 LocalAppData，覆盖升级及默认卸载均保留。
- 正式发布前需要给 EXE/Setup 增加 Authenticode；Inno 脚本已保留 SignTool 接口。

## 9. 构建与运行

前置：Windows x64、.NET SDK 10.0.302。开发机需安装 WebView2 Runtime；运行系统通知手测还需 Windows App Runtime 2.3.1+。Word 转 PDF 需要 Microsoft Word。

```powershell
dotnet restore LenxTool.slnx
dotnet build LenxTool.slnx -c Release
dotnet test LenxTool.slnx -c Release
dotnet run --project src\LenxTool.App\LenxTool.App.csproj -c Release
```

Worker：

```powershell
cd cloud\LenxTool.Worker
npm.cmd install
npm.cmd run typecheck
npm.cmd test -- --run
```

发布命令见 `RELEASE_GUIDE.md`。任何私钥路径都必须指向仓库外位置。

## 10. 当前版本边界与交付状态

本节是当前交付状态的唯一准绳，最后核对日期为 **2026-08-13**。`IMPLEMENTATION_PLAN.md` 保留完整任务与验收条件；其中未勾选的任务可能已有部分实现，但表示尚未满足该任务的全部验收条件。

### 10.1 本轮已完成

- “资讯列表”“每日早报”“热点趋势”和“AI 报告”提升为左侧一级菜单；早报默认当天并支持按缓存日期切换，正文改用原生 WPF 显示。
- 早报正文按原始顺序显示 HTML/Markdown 配图；图片请求限制为 HTTP/HTTPS、最大 12 MiB，并提供加载超时或失败提示。资讯页采用贴右侧的单一整页滚动条，页内标题和操作区随阅读滚动；回到顶部按钮在滚动后渐显，点击会立即定位并取消残余滚轮动量。
- 首页早报卡片直接从结构化“概览”开始，最多显示 5 个栏目、每栏 3 条；已移除重复的卡片标题、状态、来源和日期区。壳层统一左右表面并移除重复页标题，热点卡片使用与浅色工作区一致的暖灰表面。
- 资讯筛选下拉框按 `Label` 正确显示，不再泄漏筛选对象文本；空状态完整覆盖列表与阅读器。所有原生滚动区采用直接移植自 `TwilightLemon/FluentScrollViewer` 的统一运动核心：标准鼠标按原始滚轮 delta 累加速度并指数衰减，高分辨率触控板从当前真实偏移连续逼近目标，每个显示帧直接调用 `ScrollToVerticalOffset`，不预计算落点或使用内容 `RenderTransform`。像素虚拟列表继续使用 Recycling 并在前后各保留一屏容器；每日早报按累计四分之一屏且最高 120 px 的位移合并延迟正文检查。Shift 横向意图、Windows 禁用滚轮/客户端动画和“减少动画”会退出惯性并交回原生 WPF；切换一级资讯入口时仍自动回到顶部。
- 图片网络策略兼容公网 HTTPS 主机经 DNS 返回的 `198.18.0.0/15` Fake-IP，同时继续阻止直接访问该保留网段。
- 热点源扩展为 TrendRadar 默认 11 个平台并保留 GitHub、Hacker News，共 13 个来源；采用 NewsNow 当前接口与兼容请求头、HTTPS/预期域名校验、单源失败隔离和按平台替换缓存快照。
- 热点趋势改为两列平台卡片，名次在平台内部独立显示；热点条目提供悬停/按下/焦点反馈与受限 HTTP/HTTPS 原文打开，去除吞掉鼠标滚轮的嵌套列表。平台卡片只在进入当前视口与半屏缓冲区后创建内部热点按钮，离开缓冲区即释放；每日早报正文同样只实现当前视口前后一屏的段落与图片。
- 资讯标签改为无缺边歧义的选中底线；热点来源增加 13 项多选胶囊、实时 N/13 反馈、空筛选提示和全选恢复。
- 修复 PasswordBox 空初始值不触发附加属性回调而导致 Groq/DeepSeek Key 无法保存的问题；保存命令现在按输入状态启用，并验证 DPAPI 写入后显示配置结果。
- 媒体任务以 Queued 状态持久化，持续写入 Running 进度，正确区分完成/失败计数，并提供失败或取消任务重试。
- 数据库迁移前使用 SQLite 在线备份 API，生成包含已提交 WAL 内容的一致性快照。
- 深色模式和减少动画设置写入 SQLite 并在重启后恢复；减少动画值已进入主题资源供动效消费。
- “历史与数据”已提供 FTS5 全局搜索界面，统一检索已缓存的早报、热点和 AI 报告，支持结果详情与受限 HTTP/HTTPS 来源打开。
- 资讯中心已接入自备 DeepSeek Key 的单条早报解读和每日趋势报告；使用 `deepseek-v4-flash` 非思考模式，支持取消、结构化错误、token 用量记录、SQLite 持久化和 FTS5 检索。
- 字幕片段仓储已提供按媒体任务原子替换和按序读取，可持久化原文、译文及两项置信指标；临时 SQLite 测试已覆盖重开往返、覆盖写入、批次中途失败回滚和 schema v1 升级保留。
- 媒体工作台已支持导入一个或多个现有 SRT：严格解析 UTF-8/BOM、原序号和时间轴，格式错误会定位到具体行；任务与片段在同一 SQLite 事务中写入，中文、空格和超过 260 字符的路径已通过自动化测试，真实 WPF 文件选择与重启恢复已验收。
- 字幕批量翻译契约已定义：只向翻译器暴露原序号与原文，按可取消的异步批次返回译文；批次携带模型、请求数、token 用量和幂等恢复点，结构化失败复用 `AppError`，合并时保证原文、时间轴、置信指标和列表顺序不变。
- Gate0-04 已完成 DeepSeek 字幕翻译适配器：从 DPAPI 密钥仓储读取 Key，按字符上限分批，只发送序号和原文；严格校验缺项、增项与重复项，按输入顺序返回译文，并携带模型、token、实际请求数和精确恢复点。每批最多请求 3 次，429 尊重受限 `Retry-After`，超时短退避，取消不重试。
- Gate0-05/06 已完成字幕交付闭环：媒体工作台接入翻译、取消、断点恢复和四种导出；转写及每个翻译批次把字幕、恢复点和模型用量在同一 SQLite 事务中提交。schema v3 保存翻译服务、目标语言、请求数及输入/输出/总 token；历史页可查看原文/译文与脱敏错误，并仅依赖本地数据重新导出。
- P0-01～P0-03 已完成 Worker v1 账号/目录契约、身份生命周期与 D1 共享目录 schema：支持 `/v1/me`、refresh 轮换、幂等 logout、实时禁用检查、一次性首管理员初始化，以及带约束和迁移测试的分类/Managed Feed 表。
- P0-04 已完成管理员分类和 Feed CRUD：服务端支持新增、编辑、启停、排序、移动和软删除；全部写端点执行 admin 授权、`If-Match` 全局版本、`Idempotency-Key`、参数化 D1 写入和仅含元数据的版本审计。user/匿名权限矩阵、并发、幂等、重复与危险 URL 均有 workerd/D1 集成测试。
- P0-05 已完成只读目录发布：user 只能读取 ACTIVE，admin 可读取 ACTIVE/ALL；服务端从单个 D1 batch 发布稳定排序的原子快照，并支持强 ETag、304、矛盾缓存条件校验和客户端版本超前拒绝。软删除记录及 ACTIVE 下的停用资源不会返回。
- P0-06 已完成桌面安全会话：access token 只驻内存，refresh token 由 DPAPI CurrentUser 保存；启动恢复、`/v1/me`、并发 401 单次刷新、请求最多重放一次、失效清理和离线退出均有自动测试。
- P0-07 已完成账号与角色 UI：设置页支持登录、退出、过期提示和额度显示；侧栏显示真实会话状态，管理员入口随服务端角色出现或移除。Worker 始终是授权真相来源。
- P0-08 已完成本地 Feed schema v4：新增目录状态、分类、Feed、抓取状态和通用条目表；v2 用户按 v2 → v3 → v4 原位升级，旧早报、热点、AI 报告、媒体任务和设置保持可读。Feed 外部 ID 只在 Feed 内唯一，规范化 URL 与内容哈希不作全局去重。
- P0-09 已完成本地目录仓储：`IFeedCatalogRepository` 在单一 SQLite 事务内替换分类、Feed 和目录状态；拒绝版本倒退，中途失败回滚到上一完整版本，保留已下载文章及仍存在 Feed 的抓取状态。ACTIVE 查询稳定过滤停用资源，管理员 ALL 仅在本地确实保存过完整快照时可用。
- P0-10 已完成目录同步服务：启动恢复和后续登录会立即同步，成功后每 15 分钟检查版本，失败执行有界指数退避。user 只能通过受限同源 GET 读取 ACTIVE，admin 读取 ALL；401 复用账号单飞刷新且请求最多重放一次。首次或 ACTIVE→ALL 范围升级强制完整快照，304 仅更新时间戳；旧版、409、超时、离线、取消或无效/超限 DTO 均保留最后完整目录。设置页显示目录版本、最后同步时间与 stale 状态。
- P0-11 已完成 Feed URL 发现与 SSRF 防护：默认只允许公网 HTTPS/443；HTTP 与私网主机需要独立精确白名单。DNS 返回地址经完整 IPv4/IPv6 分类后直接钉住到无代理、无自动重定向的 TCP 连接，每次跳转重新解析校验。服务限制连接/总超时、跳转、候选数、压缩/解压大小、MIME 和编码；RSS/Atom XML 禁用 DTD/外部实体并完整读到文档末尾，HTML 只提取有界 `rel=alternate` 候选。
- P0-12 已完成 RSS 2.0 / Atom 统一解析与稳定身份：支持 CDATA、常见日期、作者、分类、enclosure、缺失字段和重复项；外部身份按 id/guid、规范 URL、Feed 作用域内容指纹依次回退。URL 只删除明确追踪参数，遇到签名或身份参数则保留完整 query；XML 禁用 DTD/实体并限制文档与条目规模，不可信正文只产出不执行脚本的纯文本。
- P0-13 已完成条件抓取、调度与退避：启用 Feed 在启动后和每分钟调度中按到期时间刷新，每批 100、全局并发 4、同 Feed 单飞。请求继续使用 SSRF 校验和地址钉住，发送 ETag/Last-Modified；200 先事务 upsert 条目再保存新条件头，304 不重写条目。单源的超时、解析、HTTP、响应上限或存储失败只更新该源的脱敏状态，并按 1 分钟至 6 小时指数退避；429 可在上限内延长到 Retry-After，退出会取消在途调度。
- P0-14 已完成 Feed 条目仓储、FTS 与安全保留边界：schema v5 为既有条目回填统一 `content_fts` 并用事务触发器持续同步；仓储支持稳定分页、Feed/分类/日期/全文/未读占位筛选，统一内容搜索可返回 Feed 条目。显式 180 天清理保护已有收藏和标签，且在私人状态模型完成前不自动调度。
- P0-C 已完成兼容与安全检查点：20 个独立 fixture（11 个 RSS、9 个 Atom）全部参数化通过，覆盖中文、ISO-8859-1、带 BOM 的 UTF-16 LE/BE、CDATA、扩展命名空间、相对链接、签名 query、重复身份和 enclosure。既有批次故障隔离、缓存保留、SSRF、XXE、巨型响应和重定向绕过拒绝测试共同完成检查点证据。
- P0-15 已完成管理员订阅管理页：管理员可刷新 ALL 目录，安全发现 Feed，并新增、编辑、启停、排序或两步删除分类和 Feed；每次写入携带目录版本与幂等键。409 只刷新而不自动覆盖，写入成功但同步失败会锁定旧版本继续写入，角色降级会清空管理目录。普通用户无入口且 Worker 仍对直接请求返回 403；页面具备键盘控件、自动化名称、实时状态和窄窗滚动边界。
- P0-16 已完成 OPML 预览、选择导入与导出：2 MiB 有界 codec 禁用 DTD/外部实体并限制结构深度和数量；嵌套分组展平为 `父 / 子` 分类，预览区分新增、重复、冲突和无效项且默认不提交。选中项先逐个通过安全 Feed 发现并复核最终 URL，再以最多 100 个操作、单次版本增量的 Worker batch 原子写入；导出只投影公开目录字段。
- P0-17 已完成普通用户 Feed 时间线与筛选：ACTIVE 目录条目按 50 条稳定分页，支持全部/分类/Feed、日期和 200 字符关键词筛选；筛选代次丢弃过期页，滚动加载使用回收虚拟化并通过 ID 去重。选择条目复用原生只读阅读器，离线时显示最后抓取/目录同步时间，目录版本前进会刷新筛选项并保留有效选择，页面不含共享订阅编辑控件；10k 假缓存首屏只物化 50 条。
- P0-18 已完成管理员 Feed 健康与诊断：健康 Tab 一次读取本机全部目录 Feed（含停用项）的抓取状态，显示最后成功/失败、连续失败、下次重试和固定错误类别；错误不带令牌、完整响应或网络内部详情。管理员可对启用 Feed 执行单条强制重试，仍复用原有 SSRF、响应大小、超时、解析和单 Feed 门闩策略，命令本身串行避免重复点击。
- P0-19 已完成首页真实数据与旧早报兼容：首页并行读取 ACTIVE Feed 条目、旧 `news_articles`、热点、媒体任务和收藏计数，状态文案不再包含固定日期/标题/任务演示数据；Feed 与旧早报按规范 URL/内容指纹去重。空目录管理员新建 Feed 时预填 `FeedCompatibilitySeed`（`https://daily.juya.uk/rss.xml`），旧 schema v2 早报继续可搜索和阅读。
- P0-20 / P1-01 已完成本机私人阅读状态：schema v6 新增 `user_entry_states`，按 profile 隔离已读、收藏、0～100% 进度和私人备注；局部 patch 保留未修改字段，条目清理保护有状态的旧条目。已补充并发局部更新与数据库重开往返测试。Feed 时间线首屏/分页批量读取状态，提供已读/收藏切换和进度展示；共享目录与 Worker 契约不变。
- P1-02 已完成收藏、标签和备注仓储闭环：通用实体可收藏/取消收藏、批量读取和更新私人备注；标签支持 NFKC 规范化、颜色更新、实体关联原子替换和安全删除，删除标签不会删除实体或收藏备注。
- P1-03 时间线交互、筛选与历史入口已完成：Feed 阅读器批量水合通用收藏，并提供收藏切换、4000 字本机备注、80 字标签创建/关联/移除、保存/取消/失败状态和选择代次保护；打开未读条目会自动写入已读，仍可手动恢复未读，动作按钮随状态显示；时间线可组合筛选未读/已读、收藏与标签，条件在 SQLite 分页前执行，收藏兼容 favorites 与旧 `user_entry_states.is_starred`；历史统一搜索选中 Feed 条目后提供同一组私人状态入口。
- P1-04 阅读进度与 P1-A 检查点已完成：正文 `ScrollViewer` 的位置按 500 ms 防抖写入默认本机 profile，切换或重开条目时恢复到对应位置，提供“从头阅读”按钮；恢复过程使用非动画滚动，避免初始滚动事件覆盖已保存进度。隔离的真实 WPF 运行测试覆盖长文滚动、备注键盘焦点、约 73% 进度持久化，以及重建 ViewModel/视图后从同一临时 SQLite 恢复进度、备注和标签；清理保护与共享目录版本不变另有集成测试。
- P1-05 资源索引与缓存预算已完成：schema v7 新增 `entry_assets`，`IEntryAssetStore` 以 SHA-256 内容哈希命名缓存文件，临时文件成功后原子转正并记录 MIME/大小/来源/创建与访问时间；支持单资源上限、全局预算、按内容哈希去重、保护当前内容的 LRU 清理和文件哈希损坏检测。
- P1-06 安全图片下载与本地回退已完成：阅读器缓存优先，网络未命中复用 P0 逐跳 DNS/SSRF/重定向和固定-IP 连接策略；仅接受 MIME 与魔数一致的 PNG/JPEG/GIF/BMP/WebP，拒绝 SVG 与伪装内容。全局并发、每篇 24 个资源、48 MiB 网络字节和单资源缓存上限共同生效，切换文章会传播取消。离线已缓存图片由真实 WPF 测试解码显示，未命中显示稳定占位，同一失败条目在五分钟窗口内不重复解析 DNS/请求网络。
- P1-07 全文提取契约与实现已完成：Core 的 `IArticleContentExtractor`/类型化正文块不暴露解析库类型；Infrastructure 逐跳复用 P0 URL/DNS/固定-IP/重定向策略，限制总超时、下载/解压大小、HTML MIME、DOM 深度/节点/正文规模和同主机并发。`HtmlAgilityPack` 仅解析内存 HTML，不取得网络能力；输出保留标题、作者、发布时间、标题/段落/列表/引用/图片及 HTTP/HTTPS 链接，脚本、样式、表单、iframe 和危险协议被移除。12 组站点形态 fixture 及中文编码、畸形 HTML、提示注入文本、净化和网络拒绝测试均通过；依赖依据见 ADR-002。
- P1-08 按 Feed 控制的全文抓取队列已完成：Worker/D1 和本地目录支持 `NONE`、`ON_OPEN`、`BACKGROUND`，默认关闭；本地 schema v8 持久化全文、任务租约与主机退避，后台执行保持全局并发 2、单主机并发 1，支持取消、去重、过期租约恢复、指数重试和停用 Feed 隔离。RSS/Atom 自带完整正文时跳过网页抓取，403、无正文和付费墙不绕过。
- P1-09 阅读器增强与 P1-B 检查点已完成：资讯阅读器标识 RSS/提取全文与提取时间，缓存或按策略提取成功后默认显示结构化全文并可切回 RSS；快速切换取消旧请求并拒绝迟到结果。标题、段落、引用、列表、图片和链接保持顺序，危险协议或带凭据 URL 降级为纯文本；原网页仅通过受控命令交给系统浏览器。
- P1-10 Feed AI 本地缓存已完成：schema v9 在现有 `ai_reports` 上建立由条目 ID、内容哈希、任务、目标语言、模型和提示版本组成的唯一缓存键；正文变化保留旧历史但精确查询不会命中过期结果。新仓储记录请求数、输入/输出/总 Token、耗时、错误码与更新时间并继续接入本地 FTS，表结构不保存 API Key、Secret 或 Credential。
- P1-11 单条与批量摘要已完成：Feed RSS/提取全文以有界、不可信 JSON DATA 进入 DeepSeek，正文内提示词与伪造 DATA 边界不能变成 system/tool 指令；单条先查六项本地缓存键，同键并发只请求一次。批量最多 20 条、并发最多 2，支持取消、429/Token/耗时/错误指标和逐条失败；阅读器可生成当前来源摘要或摘要当前页前 20 条，切换来源/条目不会显示过期结果。管理员生成策略仍按计划在 P1-13 接入。
- P1-12 条目翻译与双语阅读已完成：复用字幕翻译器的安全纯文本、序号校验和可恢复批次，每批立即写入按内容哈希/目标语言/模型/提示版本隔离的本地缓存；失败或取消后原文始终可读并可续传。阅读器支持 RSS/提取全文、四种目标语言及原文/译文/双语模式；链接只沿用原文安全目标，模型 HTML 不执行。管理员自动翻译策略仍按计划在 P1-13 接入。
- P1-13 管理员 AI 策略与本地自动处理已完成：目录版本携带全局、分类、Feed 三层手动摘要/自动摘要/自动翻译开关、目标语言、每日条目上限和并发上限；桌面离线持久化并按 Feed→分类→全局解析，旧目录安全回退为手动允许、自动关闭。订阅管理页可编辑分类/Feed 覆盖并显示预计用量与并发。schema v11 持久化自动任务、租约和每日不同条目计数；Feed 刷新成功后本机幂等入队，后台在 AI 调用前重新读取策略，停用、内容变化与语言变化立即跳过或废弃旧任务，重启可恢复。摘要和翻译继续优先使用自备 DeepSeek Key，否则走登录账号共享额度与 Worker 原子门控；缓存命中不重复请求，AI 结果不上传 D1。
- P1-14 受限规则模型与发布边界已完成：规则只暴露 Feed/分类/标题/作者/正文/语言/发布时间/音视频存在性九类字段、六类操作符和七类受限动作；显式保存规则版本、规则集版本、优先级、冲突顺序、启用状态、匹配模式和动作顺序。Core 与 Worker 都按字段白名单约束操作符，文本、集合、规则总数和排序均有上限；正则在 Worker 只编译不执行，拒绝反向引用/前后查找等非便携结构，本地固定使用 100 ms 超时的非回溯引擎。D1 schema v6 保存当前快照和不可变历史版本，管理员发布使用 `If-Match`、幂等键和最小审计，普通用户只能读取 ACTIVE 快照；无参数动作拒绝 URL、命令或其他载荷，D1 不保存新闻正文或 AI 结果。
- P1-15 Core 确定性解释器、运行账本、动作租约、受限本地状态动作与后台处理器已完成：ACTIVE 规则先验证并编译为可复用只读规则集，按优先级降序、冲突顺序升序、规则 ID 和规则内动作顺序稳定计算。九类字段、ALL/ANY、带时区时间、音视频存在性和非回溯 regex 均在纯内存中匹配；加标签按本地 `NOCASE` 语义去重，其余副作用动作全局只保留第一个胜者。schema v12 在单个 SQLite 事务中保存每个条目/规则版本的命中结果和全部动作决策，计划动作使用稳定幂等键，被抑制动作保留胜者引用；同一版本重放或重启后再次暂存都不会追加记录。待执行动作按确定顺序和显式动作类型集合领取，租约过期可回收，旧持有者不能提交；失败可延时重试，主动释放可立即重领，成功/永久失败进入不可再次领取的终态。schema v13 增加按本地档案隔离的隐藏状态，普通时间线默认排除隐藏条目；本地执行服务只接受加标签、隐藏和标为已读，先做有界载荷校验，再调用原子追加标签或私人状态仓储。追加标签保留现有标签与同名标签颜色，重放不重复关联；缺失条目返回可终结结果。后台处理器只领取上述三类本地动作，以最多 4 并发执行；缺失条目和非法载荷永久失败，可重试应用错误尊重受限 `Retry-After`，未知错误指数退避，5 次后终结，取消会释放已经领取且仍在并发门外等待的租约。AI、媒体和通知动作不会被本地处理器误领；数据库事务仍不请求网络。
- P1-15 ACTIVE 规则缓存已完成：schema v14 用单例状态和按稳定顺序索引的规则表保存 Worker 规则集版本、生成/同步时间及完整受限规则。仓储写入前复用 Core 验证器与解释器编译，拒绝停用规则、重复 ID、旧/同版本覆盖、非 UTC 时间和超量快照；规则规范化后在单个事务中整体替换，重启后可完整恢复。读取会交叉核对独立元数据与 JSON，并把格式或语义损坏统一视为无效本地缓存；同步时间只允许在预期版本仍匹配时更新。
- P1-15 Worker ACTIVE 规则同步已完成：登录后立即请求 `/v1/automation-rules?scope=ACTIVE&afterVersion=本地版本`，之后每 15 分钟增量检查；失败按 1 分钟重试，退出登录后停止请求。200 响应有 4 MiB 上限并逐项显式映射大写字段/操作符/动作契约，随后再次经过 Core 验证、编译和本地原子替换；304 只推进当前版本的同步时间，首次空规则集也会留下已同步标记。401 继续复用账号服务的单次令牌刷新，取消不写缓存，错误 scope、旧版本、超限或畸形快照均保留最后成功规则。
- P1-15 Feed 刷新规则触发已完成：只有条目事务写入且抓取成功状态提交后，规划服务才读取一次 ACTIVE 快照并编译一次规则集；每个解析条目使用 Feed/分类、标题、作者、正文、发布时间和音视频投影计算计划，再写入既有幂等运行账本。正文优先使用清洗正文、缺失时回退摘要，并在 Unicode 边界内限制为 100,000 字符；音视频由 Feed 视图或 enclosure MIME 判断，当前 Feed 模型尚无可靠语言字段时显式传空。空规则不打开运行事务，304/写入失败不规划，同一条目+规则版本重放由账本保持零新增；规划失败与 AI 策略排队互相隔离，已成功的 Feed 刷新不回滚。
- P1-15 AI 摘要/翻译规则动作已完成：独立处理器只领取 `GenerateSummary` 与 `Translate`，在读取条目前验证动作载荷，停用 Feed/分类和缺失条目进入明确终态；规则本身授权执行，因此不依赖自动摘要/自动翻译开关，但继续使用解析后的每日条目上限。实际处理复用既有摘要/翻译缓存、共享额度传输和富文本纯文本投影，AI 调用始终位于 SQLite 事务之外；可重试供应商错误遵循受限退避，取消会释放全部已领取租约。处理器保守串行执行，以满足任意 Feed 的最小并发上限。
- P1-15 Notify 规则动作已完成：独立处理器只领取 `Notify`，在读取后复核 Feed/分类启用状态。schema v16 的 `app_notifications` 用动作幂等键保存本地标题、来源、规则追踪和创建/已读状态，不保存正文或 URI；并发与重启重放不重复。顶部铃铛显示最近通知、未读角标并支持单条/全部已读，后台事件切回 UI 上下文且订阅者异常与持久动作隔离。P2-22 已在该耐久收件箱之上补齐系统投递与受控激活。
- P1-16 规则管理和只读模拟已完成：管理员专属一级页面只用封闭字段、允许操作符和七类动作构建规则，不提供脚本或自定义请求。桌面读取 Worker `ALL` 快照，以独立规则集版本和幂等键发布；版本冲突刷新但不重放，响应继续走 Core 验证。模拟只读取本地最近条目和目录，在内存解释器中展示命中及计划动作，不取得运行账本、AI、媒体或通知服务。普通用户无入口，绕过 UI 的发布仍由 Worker 403 拒绝，管理员发布由既有 D1 不可变版本与审计记录覆盖。
- P1-17 附件分类与安全打开已完成：RSS/Atom enclosure 与 Media RSS `media:content` 统一解析、稳定去重并限制每条 32 项；Core 以允许清单核对 MIME/扩展名，并阻止危险协议、凭据、自定义端口、保留主机名和非公网字面 IP。阅读器显示附件种类、大小/未知大小、类型状态和来源警告；仅经验证且不超过 12 MiB 的 HTTPS 图片进入既有安全图片下载器，音视频、大图、未知大小和不支持类型回退为受限外链，危险地址不生成链接。
- P1-18 Feed 媒体投递已完成：`SendToMedia` 规则动作只处理经附件分类器批准的音视频，逐跳复用固定地址/SSRF 策略，并限制类型、容器、大小、重定向、超时、并发和专用临时目录。schema v15 以条目和附件 URL 幂等创建来源台账与既有媒体任务；失败不留文件或孤立任务，重启后由持久队列恢复，成功任务即时进入媒体工作台并继续复用转写、翻译、SRT 导出和历史能力。
- P1-19 统一搜索扩展已完成：schema v17 把字幕、标签和收藏加入本地 FTS，并为旧库回填索引；字幕替换、收藏和标签更新/删除会同步索引，避免幽灵结果。历史页可按七类实体、日期、Feed、分类、标签和收藏组合筛选并稳定分页；Feed 结果在应用内打开精确阅读条目，字幕结果直接定位任务历史。
- P1-20 保留、清理与数据库维护已完成：默认只清理严格早于 180 天、没有收藏/备注/标签/私人阅读状态且无全文、AI、规则或媒体活动任务引用的 Feed 条目；最多 5000 条一批，取消只停止后续批次。资源清理与缓存写入/LRU 共用互斥门闩，数据库优化在空间不足或占用时安全跳过压缩。设置页显示数据库、图片缓存和模型占用，提供只读预览、显式确认与取消；容量统计在主窗口显示后后台运行。
- P0 最终验收第一片已完成：`p0-final-acceptance.test.ts` 在真实 workerd/D1 中走临时 bootstrap/login，覆盖管理员发布与停用、目录刷新、普通用户同步/阅读、六类管理员写端点 403 隔离和审计字段脱敏。
- P0 最终验收第二片已完成：完整 .NET 310/310、Worker 39/39、typecheck 和 Release 0 警告/0 错误复核了 OPML 导入/导出、断网缓存、坏源隔离、schema v2 原位升级和 10k 条目首屏性能；五份终验文档已同步，P0 已关闭。
- P1 最终检查点已完成：真实 schema v17 SQLite 在数据库重开后的离线场景中装载 10,000 条 Feed、1,000 个收藏和混合音频/视频/图片，收藏分页与七类统一搜索各自满足 2 秒预算；清理预览满足 10 秒预算，8,996 个无保护旧条目的有界清理满足 60 秒预算并保留 1,000 个收藏和 4 个全文/AI/规则/媒体活动条目。真实 workerd/D1 验证管理员发布目录、AI 策略与规则，普通用户三类写入均为 403 且版本不变，并逐表确认 D1 不含正文、AI 结果、字幕或本地路径字段/哨兵值。当前 Release 回归为 .NET 648/648（Core 96、Infrastructure 354、App/WPF 198）、Worker 52/52、Worker strict typecheck 和全解决方案构建 0 警告/0 错误。
- P2-01 内容类型分类器已完成：Core 新增每条内容独立的 `EntryViewKind` 和确定性分类器，优先使用单独跟踪的管理员显式覆盖，再按 Feed 声明顺序选择首个 URL 允许且类型完整验证的 enclosure，随后接受正文提取层的结构化主媒体信号，最后回退 Article。仅 URL 扩展名、仅 MIME、冲突类型、受阻 URL、Unknown 和非法覆盖值都不能产生不可信媒体分类。既有目录 `FeedViewKind` 必填且历史默认 `Article`，不能区分“显式文章”与“自动分类”，因此本片不修改目录 schema，也不把该默认值直接当覆盖；后续目录/UI 接入必须新增单独的显式覆盖状态。
- P2-02 图片流已完成：本地 schema v18 与 Worker/D1 0007 增加独立视图显式覆盖状态，迁移保留历史非 Article 覆盖并兼容旧 v1 客户端；管理页可选择自动识别或强制五类视图。图片页按首次切换懒加载，支持来源/分类/日期/收藏筛选、原始 continuation 分页、Enter 安全打开和三列虚拟化。缩略图沿安全下载边界流式写缓存、以有界缓冲校验后返回文件流并按 360 像素解码，容器复用会取消旧请求；真实图片页装载 1,000 张图片后，首尾滚动和 200% WPF 布局缩放仅实现可见行。
- P2-03 音频与播客视图已完成：资讯中心新增惰性音频页签，复用 Audio 分类查询、分页和四类本地筛选；选择条目不访问网络，只有显式播放才由可替换的 WPF 系统媒体适配器打开已验证 enclosure。播放器展示来源、MIME/大小、当前位置和可用时长，本地 `EntryState.Progress` 以最多每秒一次写入并支持重启恢复，切换条目会停止旧源且忽略迟到事件。转写复用 Feed-media 的 SSRF/MIME/签名/大小/取消和 entry/source 幂等边界，发布任务后进入媒体工作台；强制音频但格式不支持、或系统解码/断流失败时，只能经两步确认打开安全原文。真实 WPF 运行时以 1,000 条音频验证 900×620、200% 缩放和回收虚拟化。
- P2-04 视频视图已完成：资讯中心新增惰性 Video 查询、四类筛选和 1,000 条回收虚拟化列表；封面只接受同条目中完整验证的图片 enclosure，并继续走 `FeedThumbnail` 安全缓存链，没有可信封面时显示本地占位。页面不包含网页或视频播放器，只以两步确认打开安全原文。显式“下载并转写”先显示声明大小/未知上限、固定目标目录、可用空间和 512 MiB 上限；未知或不小于 20 MiB 时需再次确认，确认执行前和底层联网前都会重检磁盘，取消清理临时文件且不发布任务。视频下载后还要通过本机 Media Foundation 音轨兼容性探针才会登记并进入既有 Whisper 链。真实 WPF 覆盖 900×620、200% 缩放、无横向溢出和无内嵌播放器。
- P2-05 通知流已完成：schema v19 在既有本地收件箱上增加内容命中、系统健康和任务完成三类封闭类别，旧通知自动归入内容命中；顶部通知流可按类别筛选，筛选不改变未读角标或数据库。Feed 抓取异常以 Feed、失败周期和错误类别形成稳定键，自动摘要完成以任务 ID 形成稳定键；两者只保存规范化标题/来源和本地关联，不保存正文、摘要结果、异常详情、URL 或令牌。通知写入/订阅者失败与原抓取、AI 任务结果隔离，单条/全部已读仍只修改本机 SQLite。
- P2-05 共享智能视图已完成：Worker/D1 0009 增加独立版本的 ACTIVE/ALL 快照和管理员创建/更新/删除，普通用户写入与 ALL 读取由服务端 403；定义只能包含有界名称/排序和 Feed、分类、内容类别、已读、收藏、关键词、发布时间窗口，未知字段、脚本、URL 和超限值在写入前拒绝。条件版本、幂等键、事务守卫、不可变历史与最小审计独立于目录/规则。桌面 schema v20 保存最后验证成功的 ACTIVE 定义并在登录后后台增量同步，旧/同版本、损坏、停用或重复项不会覆盖离线缓存。资讯时间线可只读选择并显式套用已发布视图，套用前重读最新本地快照；临时筛选会退出共享视图，已读/收藏只进入本地查询。独立管理员原生编辑页只暴露封闭控件，发布、更新、确认删除处理版本冲突且角色撤销后不回填管理状态。
- DISC-01～DISC-03 统一发现基础已完成：Core 提供 URL/RSSHub/关键词分类、规范候选和来源证据合并；Worker/D1 提供认证后的已知目录索引；桌面统一协调器聚合 Worker 已知目录与既有安全 direct URL 探测，并按来源隔离超时、并发、成功缓存、熔断和脱敏状态。provider 契约可替换，但 RSSHub/外部平台在官方接口和条款审核前不注册。定向测试覆盖 429、畸形响应、证据伪造、取消、部分失败、全源不可用和完整 SSRF/DNS 重绑定边界。
- UX-03 原生控件视觉基础已完成：资讯时间线/图片使用保留原生选择语义的分段页签，日期/日历、复选框和下拉框共享语义主题与完整交互状态；日期模板保留 WPF 自有 Calendar 实例和回写链路。结构测试与真实 WPF 运行时测试覆盖模板部件、键盘、Automation Peer、弹层选择、900×620 窄窗、等效 200% 缩放和深浅主题，历史/管理/设置页的同类迁移另行拆片。
- 全局滚动体验已补强：类级事件处理覆盖显式与控件模板内部的 `ScrollViewer`，嵌套滚动区优先消费最内层仍可滚动的容器，到达边界后交还外层。标准鼠标完整采用上游的 2.0 速度倍率、0.92 摩擦和 144 Hz 时间基准，连续输入直接代数叠加速度，因此反向滚轮会先抵消旧动量；高分辨率触控板采用 0.5 插值，并以每次输入到达时的真实 `VerticalOffset` 重设目标。渲染帧读取真实偏移后直接调用 `ScrollToVerticalOffset`，不再解析最终落点、预提交逻辑位置或修改内容 `RenderTransform`。ListBox、ListView、PagedListBox 与下拉列表继续使用 Recycling 像素虚拟化和前后各一屏缓存；每日早报只实现当前视口前后一屏的正文块，并把全文视口扫描按累计四分之一屏且最高 120 px 的安全位移合并，离开缓冲区的图片立即取消后台下载与解码。快速连续滚动复用单一状态；拖动、键盘、触控、程序化定位、回顶和卸载会清理当前动量。Shift 横向意图、Windows 禁用滚轮/客户端动画和“减少动画”交回原生 WPF，回顶保持立即定位。
- 滚动算法来源边界：运动核心直接移植自 [`TwilightLemon/FluentScrollViewer` 提交 `63f07a9`](https://github.com/TwilightLemon/FluentScrollViewer/blob/63f07a972bfde3d9a517f5c0f13f105df5a64b34/MyScrollViewer.cs)，包括输入分类、速度叠加、逐帧衰减/插值、阈值和 `ScrollToVerticalOffset` 时序；LenxTool 只在外围保留全局/嵌套路由、宿主动效策略、程序化定位和长文视口节流。上游完整 MIT 文本随源码和构建输出保存在 `Controls/FluentScrollViewer.LICENSE.txt`，未引入上游二进制依赖。
- DISC-04 管理员统一发现页已完成：入口位于订阅管理内部，统一支持关键词、URL 与 RSSHub 路由识别、450 ms 防抖、显式提交/取消、请求代次和重试；输入变化或会话降权立即取消旧请求。候选卡显示来源、健康、警告、类型和更新时间，并用一次批量 SQLite 投影读取匹配 Feed 的本地标题/时间预览，过滤隐藏条目且每 Feed 最多 4 条，不读取或上传正文。加载、部分成功、空结果、离线、限流、非法输入和取消状态互不混淆；本片无目录发布写命令，普通用户继续无法进入管理员管理页。
- DISC-05 管理员发布闭环已完成：候选先按 ALL 管理目录的规范化 URL 标记为“加入共享目录”或“查看现有项”；发布面板完整显示规范化地址、分类、刷新、视图与全文策略，只有管理员显式勾选确认后才可提交。写入复用现有 `If-Match`、同次令牌刷新幂等键、Worker RBAC 与审计；版本冲突会刷新目录且不自动重放，网络中断或写后刷新失败会锁住后续发布并要求先确认目录状态。成功刷新到新版本后，当前候选立即转成现有项。
- DISC-06 最终检查点已完成：已知目录关键词来源和直接探测的默认预算分别锁定为 8 秒与 20 秒，真实 5,000 条 workerd/D1 目录首批 50 条测试用例耗时 188 ms，100 个候选/2,500 条本地条目的 SQLite 预览投影实测 55 ms。全源不可用显示可重试离线状态，慢源超时不丢健康来源；D1/响应字段白名单和发现日志静态审计确认正文、私人状态、令牌、敏感查询和本地路径不进入云端或日志。普通用户写入 403、管理员并发冲突、恶意 URL/重定向/XXE/压缩炸弹均由真实传输或 workerd/D1 测试覆盖。最终门禁为 .NET Release 755/755、Worker 64/64、strict typecheck、依赖 0 漏洞与构建 0 警告/0 错误。

- P2-08 集成策略与安全健康检查已完成：Worker/D1 0010 只保存九种封闭类型、启用状态和精确主机白名单，ACTIVE 可由认证用户读取，ALL/PUT 由服务端 admin RBAC 隔离，并使用独立版本、幂等、事务守卫、不可变快照和最小审计。个人 TargetId/HTTPS 地址留在本机设置，凭据经 SHA-256 派生槽位进入 DPAPI CurrentUser，不写 SQLite/D1/日志/导出文件且界面不回读。连接测试在任何适配器前执行精确主机、HTTPS/443、DNS 全地址分类、私网/保留地址阻断、8 秒超时、取消、30 秒目标冷却和并发 2，结果只使用封闭状态。P2-08 完成当时生产没有注册真实探针或导出适配器，因此该历史切片默认零第三方外联；后续 P2-11～P2-14 已分别接入受控 Obsidian、Eagle、Zotero 与 Readwise 适配器。Core 173/173、Infrastructure 397/397、App/WPF 312/312、Worker 74/74、typecheck 与 Release 0 警告/0 错误通过。
- P2-09 导出队列与历史已完成：schema v21 使用稳定幂等键、五态互斥约束、租约令牌和续期心跳持久化任务；单进程并发固定为 1，跨服务在租约有效期内不能重复领取，心跳还会把其他进程写入的持久取消桥接给当前适配器，应用退出可释放或等待租约恢复。队列只接受可安全重放的幂等适配器，429 精确尊重 0～7 天内的 Retry-After；历史不保存正文、凭据、请求/响应或任意 RemoteId/RemoteUrl，只展示非秘密目标引用、条目/内容版本、状态、次数、时间和封闭错误码。取消在副作用成功前合作终止，适配器已返回成功后由 Completed 优先，避免把真实成功伪装为撤销。决策和限制见 ADR-003。
- P2-10 Markdown 文件导出已完成：固定 front matter 与 UTF-8 无 BOM 输出支持仅链接、清洗正文、正文加已缓存栅格图片；缺失图片不会触发下载。中文、Windows 保留名、恶意分隔符和 Unicode 截断统一清理，根目录及目标组件拒绝 symlink/junction reparse point，正文和图片均通过同目录临时文件转正。覆盖/跳过使用稳定条目文件名，新版本由幂等键派生，同一内容版本重启重试不会制造副本。生产仍未注册任何目标实例，所以当前不会静默写文件。
- P2-09/P2-10 完整门禁为 Core 173/173、Infrastructure 414/414、App/WPF 323/323，共 910/910；Worker 74/74、strict typecheck 与 Release build 0 警告/0 错误。
- P2-11 Obsidian 适配器已完成：生产始终注册适配器能力，但本机目标只保存为单条版本化 JSON；只有 Vault 已明确保存、管理员 ACTIVE 策略启用且用户点击阅读器行内或详情区“导出到 Obsidian”后才入队，后台执行时再次读取当前配置和策略。队列目标使用不泄露路径的 `default.<24 位小写十六进制>` 配置修订标识；版本化任务在任何目录操作前必须与当前规范化配置精确匹配，旧配置任务以非重试 `Conflict` 关闭，重新点击会按新配置作用域入队。Windows 路径大小写、分隔符和尾随分隔符等价形式保持同一作用域，实际目标或渲染输出变化才生成新作用域；只有预版本 `default` 任务为迁移兼容读取当前配置。失败或取消任务可由用户显式重试，已完成任务仍保持去重。显式 Feed 视图覆盖自动分类。
- Vault 必须是已存在且不能为磁盘根的绝对本地目录，拒绝 UNC/device/network、reparse point、目录逃逸、ADS、保留名和尾随空格/点。64 KiB 内联模板只允许五种正文占位符且每种最多一次，并以单遍、非递归方式替换；空模板统一为未设置。实际输入最多 8 MiB、最终 UTF-8 文件最多 12 MiB；HTML 在建 DOM 前受标记预算保护，解析阶段限制嵌套，解析后再限制 16,384 个来源节点、128 层深度和深度加权渲染工作量。Feed 文本中的 Markdown 控制符、原始图片/链接和自定义 URI 按纯文本安全输出，代码区保留字面内容并动态选择围栏，只有通过校验的 HTTP(S) 来源链接由渲染器以安全目标形式生成。渲染与输出预算全部成功后才创建目标目录或复制缓存图片。标签进入逐项转义的复数 YAML `tags`。每个内容版本创建确定性新文件且从不覆盖，不读取 Vault 模板文件、不安装插件，也不实现或调用 `obsidian://`。临时设置存储或 Vault I/O 故障进入可重试状态，权限、无效配置、旧版本作用域和撤销策略失败关闭。客户端本机 Obsidian/Eagle 策略都使用空主机列表，其余七种网络集成仍要求精确 DNS 主机。180 天清理新增对 `QUEUED`/`RUNNING` 导出引用的保护。
- P2-11 当前完整门禁为 Core 174/174、Infrastructure 484/484、App/WPF 347/347，共 1005/1005；Worker 75/75、strict typecheck 与 Release build 0 警告/0 错误。滚动运行时验收不再依赖固定等待 80 ms，而是直接断言动画会话与最终逻辑滚动，避免离屏 WPF 帧时钟抖动改变产品契约。
- P2-12 Eagle 适配器已完成：Worker 的 Eagle 策略只能使用空 `allowedHosts`，端点仅保存在本机版本化设置中。专用 HTTP 客户端只接受带显式端口的 IP-literal loopback HTTP 根地址，关闭代理、自动重定向、Cookie 和自动解压，并以官方 Web API V2 的 `app/info`、`library/info` 验证 Windows 4.0 Build 21+ 与当前资源库。保存设置本身可不探测；只有 ACTIVE 策略启用后的连接测试或阅读器显式导出才越过本机 API 边界。
- 图片候选必须同时通过 URL、声明媒体类型、格式白名单、MIME/魔数和声明/实际 12 MiB 上限；远程图片由 LenxTool 下载后以 data URI 发送，Eagle 不取得源图 URL。标题、HTTP(S) 来源和有界标签确定映射。资源库原始名称/路径不会保存或显示，只在内存生成不透明修订；队列目标 `default.<端点修订>.<资源库修订>` 同时隔离端点、当前资源库和幂等键。执行器持有端点进程内代际租约，同进程保存新端点会等待；资源库在查询、下载与新增边界复核，探测到的变化以 `Conflict` 关闭。稳定自定义 ID、写前查询和不确定 POST 后复查共同收敛崩溃重放；408/429/5xx 与未知写入结果保持可重试。官方 `item/add` 不支持按资源库身份条件写入，故不能原子阻止探测与新增之间的外部切库；写后持续切库只能报冲突，无法撤销可能的误写，ABA 切库也不可观测。端点代际门不跨 LenxTool 进程，实际使用和真实验收必须在任务终态前保持当前资源库不变且避免其他进程改设置。
- P2-12 当前独立项目门禁为 Core 177/177、Infrastructure 538/538、App 372/372（排除一个在基线 `1a1cd057` 同样失败的既有 `SelectionControlsWpfRuntimeTests` Calendar AutomationPeer 环境缺陷）、Worker 78/78、strict typecheck、NuGet/npm 0 漏洞和 Release build 0 警告/0 错误。全解决方案同进程 App 运行因该 Calendar peer 污染表现为 366/373；竞态加固前的最终同进程 Infrastructure 另有 1 项 SQLitePCL 已释放的宿主串扰，精确复跑 1/1、竞态加固后独立全量 538/538 均通过。未把环境阻断写成全绿。未新增依赖、SQLite schema、D1 migration 或凭据存储，且无受控真实 Eagle 连通证据。
- P2-13 Zotero 适配器已完成：首版只支持个人库，专用设置卡显式保存正整数 User ID、`webpage`/`journalArticle` 类型、可选 Feed summary 子笔记和默认关闭的首张兼容图片附件。API key 只进入固定 `Zotero/default` DPAPI CurrentUser 槽位，界面保存后立即清空且不支持回读；目标选项与 User ID 共同生成不泄露原值的配置修订。管理员 ACTIVE 策略必须精确允许 `api.zotero.org`，用户显式点击阅读器行内或详情按钮后才入队，执行前再次校验目标、策略和凭据，并持有目标代际租约直到最后一次 API 调用。父条目只映射标题、规范化 HTTP(S) 来源、single-field 作者、发布日期/更新时间和 Feed categories；私人备注、正文与 AI 私人状态不会进入 Zotero。
- Zotero 客户端固定 Web API v3 个人库根地址，并用 `/keys/current` 核对 User ID、library/write 和按目标需要的 notes/files 权限。父项、note 与 imported_file attachment 使用按对象角色分盐的确定性 8 位 Zotero key、`version: 0` 和 LenxTool 身份标记；写前/写后按 key 读取并核对身份，匹配即收敛，碰撞失败关闭。可选附件只使用首个同时通过 URL、已验证类型、PNG/JPEG/GIF/WebP/BMP 白名单、MIME/魔数与声明/实际 12 MiB 上限的图片；上传按官方三阶段协议执行，一次性 HTTPS 存储地址继续经过公网 DNS 钉住、禁代理/跳转/Cookie/解压和有界响应，且绝不取得 Zotero API key。取消或超时不能回滚已发生的第三方写入，后续只能依靠稳定 key、身份复查与文件 `exists=1` 重放收敛；没有受控真实 Zotero key/个人库，因此 P2-D 真实连通检查点仍未完成。本切片未新增依赖、SQLite schema 或 D1 migration。
- P2-13 当前独立项目门禁为 Core 177/177、Infrastructure 642/642、App 389/389（继续排除上述已在基线复现的单个 Calendar AutomationPeer 环境用例），共 1208 个未阻断 .NET 用例；Worker 78/78、strict typecheck、NuGet/npm 0 漏洞和 Release build 0 警告/0 错误。Zotero 聚焦结果为 Infrastructure 104/104、App 17/17；独立只读审查未发现 P0/P1。
- P2-14 Readwise 适配器已完成：通用个人集成卡选择 Readwise 后固定 `default` 与只读 `https://readwise.io/`，token 只进入 `Readwise/default` DPAPI CurrentUser 槽位；当前表单必须先保存再测试。无副作用健康探针只调用官方 `GET /api/v2/auth/`，显式阅读器动作在 ACTIVE 策略精确允许 `readwise.io` 后才按固定 `default.v1` 作用域入队，后台再次执行策略、凭据和 API 门控。行内 `R` 仅对当前已选中并展示预览的同一 FeedEntry 可执行，详情按钮使用同一边界，防止预览与发送条目错位。
- 导出器支持五种视图，只发送规范来源、标题、作者、UTC 日期、最多 32 个 categories 和界面可见的有界 `summary`。摘要优先净化正文、为空才回退 Feed summary，规范空白后同时限制 4,000 个 Unicode 文本元素与 16 KiB UTF-8；不发送 `html`、图片、私人备注、AI 私人状态或本机路径。客户端固定 Reader API 与 `Token` 请求头，禁代理/跳转/Cookie/解压、全部公网 DNS 钉住、单并发、1.2 秒主动节流、响应头与正文共用 8 秒、有界 JSON 和封闭错误；长 `Retry-After` 立即交还耐久队列调度，不占住全局导出 worker。官方同 URL 重放不会创建第二条，但会把文档置顶并显示绿色标记；不同追踪 URL 仍可能重复，因此不宣称无副作用强幂等。没有真实 token/账户写入，本切片不新增依赖、SQLite schema 或 D1 migration。
- P2-14 当前独立项目门禁为 Core 177/177、Infrastructure 714/714、App 397/397（继续排除上述单个 Calendar AutomationPeer 基线环境用例），共 1288 个未阻断 .NET 用例；Worker 78/78、strict typecheck、NuGet/npm 0 漏洞和 Release build 0 警告/0 错误。Readwise 聚焦结果为 Infrastructure 72/72、App/设置/DI 17/17。Infrastructure 首轮因既有 SQLitePCL 测试宿主释放串扰为 713/714，精确复跑 1/1 后独立全量 714/714；App 首轮暴露只读预览误用默认 TwoWay 绑定为 396/397，改为 OneWay 后精确与独立全量均通过。独立审查发现并修复了长 Retry-After 阻塞全局 worker、行按钮可发送非预览条目两项 P1；修复后复核未发现新的 P0/P1。
- P2-20 本地定时任务模型已完成：Core 负责 once/daily/weekly/monthly 的本地时区与 DST 换算，schema v22 保存计划定义和下一 UTC 游标，schema v23 以唯一窗口、租约令牌和尝试次数提供重启后的 RunOnce/Skip 恢复。通用后台只领取已注册稳定 ID 的幂等处理器，未知计划不执行也不阻塞；续租心跳、异常释放、宿主停止和陈旧 owner 均有独立收敛路径。领取后的任何计划写入都形成持久取消代际，Complete/Release 在 SQL 中原子要求计划仍存在且代际未变；禁用后快速重启、删除计划和过期孤儿窗口都不能恢复旧执行。P2-20 聚焦为计划仓储/窗口 30/30、处理器/DI 6/6；独立审查修复缺失计划绕过最终护栏和首次取消探测期间宿主停止不释放两项问题，复核无剩余 P0/P1。
- P2-21 每日/每周本地摘要已完成：两个稳定计划 ID 按所选 ACTIVE Feed/分类/关键词读取上一个本地日历窗口，限制候选 200、去重后 40 条、单条 1,200 字符和总源 16,000 字符。空窗口和确定性报告缓存在模型调用前返回；报告身份只覆盖真正进入模型的输入，并包含范围、窗口、模型和 prompt 版本。schema v24 以三张伴生表提供计划+范围原子保存、持久 Retry-After/指数退避和模型请求防重账本；报告/FTS、请求与窗口终态在同一事务验证租约和计划代际后提交。明确可重试的 429 按 Delta 或 HTTP-date 退避，永久 4xx 收敛为终态；网络/超时/5xx 或崩溃等结果不明场景不自动重放，以可能跳过一次摘要换取不重复计费。报告可在 AI 报告页刷新、搜索并原子导出 `.txt`；筛选、报告和路径不上传 Worker/D1，生成沿用本机 DPAPI DeepSeek Key。管理卡提供日/周启停、本地时间、星期、ACTIVE 范围、关键词和下一次执行时间。最终新鲜门禁见 [`TEST_REPORT.md`](TEST_REPORT.md)。
- P2-22 Windows 通知已完成：系统投递默认关闭并采用通用提示，标题预览也不包含来源或正文；设置页可配置静默时段、0/5/15/30/60 分钟聚合和关闭通知，保存后立即生效。应用内 SQLite 收件箱保持唯一耐久真相，容量 128 的 Windows 通道只做尽力投递；启动早期事件会等持久策略恢复，隐私降级或关闭与最终 `Show` 串行，避免旧标题竞态。schema v25 为通知增加 `NONE`、`FEED_ENTRY`、`AI_REPORT` 封闭目标；系统激活只接受唯一的 64 位小写十六进制通知 ID，重读本地行后才路由，不接受 URI。Toast 点击会同步当前列表和全表角标，包括最近 50 条窗口外目标。Windows App SDK Runtime 缺失或系统禁用时只降级 Toast；安装器的 WebView2/Windows App Runtime 资产在下载和缓存复用时均受固定哈希与 Microsoft 签名验证。真实系统通知、常规/最小窗口设置页以及最终自动化门禁均已验证，独立终审无剩余 P0/P1。

### 10.2 下一里程碑

Gate 0 字幕闭环、P0“管理员策展 RSS”、P1“阅读增强、AI 与自动化”、P2-01～P2-14、P2-16～P2-23，以及插入计划 DISC-01～DISC-06、UX-03 均已完成。[P2 内容视图与集成计划](plans/RSS_P2_VIEWS_INTEGRATIONS.md) 的 P2-15 Cubox 因与既有导出能力重叠、官方幂等与安全重试契约不足而取消实施；客户端不保存 Cubox API 凭据，也不注册连接探针或导出器。P2-23 已按 [Accepted ADR-004](decisions/ADR-004-server-email-digest-gate.md) 选择 A：不实施邮件摘要、不收集邮箱、所有 Feed/AI 内容云端保留 0 天，不新增云端文章表、邮箱字段或邮件发送代码。P2-D 已完成 Desktop v2 与 qBittorrent 的部分真实 canary，但 qBittorrent 剩余状态矩阵和 Readeck/Outline/Webhook 仍未完成；P1/P2 源码进度不等于正式签名发布完成。

2026-08-17 生产检查点已完成 D1 创建、迁移前后 Time Travel 留证、0001～0011 全量迁移、关键列/触发器复核、Worker v2、随机 `TOKEN_SECRET`、公网 `/health` 200、首管理员和策略 schema v2/旧客户端兼容契约。bootstrap 首轮因本地与生产 PBKDF2 迭代上限不一致返回 500；失败请求未创建用户，根因修复提交 `7ce9827` 已发布。随后首管理员条件写入、正常登录与 `/v1/me` ADMIN 身份均验证成功；临时 `BOOTSTRAP_TOKEN` 已删除，入口恢复 404。策略契约首轮在任何 PUT 前发现 Cloudflare 压缩把强 ETag 改成弱 ETag；提交 `3cbb879` 为所有 ETag 快照的 200/304 响应增加 `Cache-Control: no-transform`，生产复验后 v2 GET/PUT、强 ETag/`If-Match`、精确幂等重放、幂等键冲突、旧版本冲突、旧客户端 ACTIVE 投影/ALL 升级拒绝及 304 均通过。当前 100% Worker 版本为 `94d90695-3162-4e9c-b8ad-d3feb1541dd6`。策略版本 3 曾只为 qBittorrent canary 授权 category `lenxtool-canary` 与 loopback 端口 47891；验收结束后版本 4 已恢复九类全部禁用、所有 host/endpoint/resource/port 授权为空、ACTIVE 0。Release Desktop 的真实健康、magnet、受控 `.torrent`、重放、精确清理和撤销均通过；target 已降为 marker 0，LenxTool DPAPI 测试凭据删除，qBittorrent 进程停止。D1/Worker 不保存 provider 秘密、条目或完整 magnet；远端无待迁移，Secret 仅有 `TOKEN_SECRET`。qBittorrent 的真实公网 fetch/200/202/失败状态、Readeck/Outline/Webhook、Groq/DeepSeek Provider Secret 与正式发布仍未完成。恢复书签和请求级证据只保存在本机忽略文件，不进入仓库。

#### 10.2.1 可执行下一步：P2-D → 正式发布

按以下顺序推进，任何一步失败都停止后续写入或发布，不用“重试”代替人工确认：

1. **锁定受控环境并发布最小权限策略。** 指定 Readeck、Outline、qBittorrent 和 Webhook 的测试实例、版本、操作者、测试时间窗与回滚负责人；准备精确 endpoint、Outline collection UUID、qBittorrent category 和 Webhook 接收端。每轮从当前版本 4 的全禁用安全基线只启用一个实际测试类型，并加入最小 host/endpoint/resource/port 授权；结束后恢复全禁用，不重复迁移、重开 bootstrap 或手工改迁移账本。
2. **补完真实 provider 矩阵。** qBittorrent 先补生产 `TorrentFileFetcher` 的公网 HTTPS `.torrent` 和可观测 200/202/409/暂时故障；Readeck 验证标签查找、首次创建、重复重放和归档；Outline 验证 collection 身份、个人草稿、重复更新和目标切换；Webhook 验证 OPTIONS 能力、固定 JSON、幂等键、HMAC 和精确 ack。目标始终先于 DPAPI 秘密保存，结束时清理测试对象并降 marker；每一步只记录脱敏状态、队列终态和非秘密对象标识。
3. **关闭或回滚。** 只有四个 provider 的真实矩阵、凭据生命周期、策略撤销、断网/超时/重复执行和 D1/Worker 观测均通过，才关闭 P2-D；迁移或策略语义异常时停止写入，保留 Time Travel 书签并由发布负责人决定恢复，不在生产直接反复应用迁移。
4. **关闭 formatter 后生成正式制品并发布。** 先在独立提交中修复全仓编码/空白/导入顺序，使 `dotnet format --verify-no-changes` 与完整回归同时通过。安装 Inno Setup、准备仓库外 ECDSA 更新私钥和 Authenticode 证书后，运行 [`RELEASE_GUIDE.md`](RELEASE_GUIDE.md) 的构建脚本；核对安装包/便携包/清单的版本、哈希、签名和 Microsoft 依赖资产，完成 Windows 10/11 x64 全新安装、覆盖升级、卸载保留数据、Runtime 缺失降级和更新回滚测试。先推送源码提交，再创建带版本标签的 GitHub Release；未完成以上步骤前不能标记为“端到端生产验收完成”。

字幕闭环完成后的产品主路线已确定为“管理员策展 RSS”：管理员维护共享 RSS/Atom 目录，普通用户只能同步和阅读，不得修改共享订阅、分类、抓取策略或自动化规则。为保持现有“云端不存新闻正文”边界，首版采用 Worker/D1 保存权威目录、各桌面客户端本地抓取和 SQLite 缓存的模式。

详细执行顺序如下：

1. Gate 0 字幕闭环已完成；验收记录见 [`plans/EXISTING_BACKLOG_ALIGNMENT.md`](plans/EXISTING_BACKLOG_ALIGNMENT.md)。
2. P0-01～P0-20、P0-B/P0-C 及最终检查点已完成；P0 关闭记录见 [`plans/RSS_P0_ADMIN_CATALOG.md`](plans/RSS_P0_ADMIN_CATALOG.md)。
3. P1-01～P1-20、P1-A/P1-B/P1-C/P1-D 及最终检查点已完成；关闭记录见 [`plans/RSS_P1_READING_INTELLIGENCE.md`](plans/RSS_P1_READING_INTELLIGENCE.md)。
4. P2-01～P2-14 已完成五视图、智能视图、统一导出契约、安全集成策略、持久化队列、本地 Markdown、受控 Obsidian Vault、Eagle 图片、Zotero 个人库与 Readwise Reader 导出；[`plans/RSS_DISCOVERY_AND_CONTROL_UX.md`](plans/RSS_DISCOVERY_AND_CONTROL_UX.md) 的 DISC-01～DISC-06 已交付独立发现索引、安全可替换 provider、管理员统一发现页、确认发布闭环和最终检查点，UX-03 已交付原生 WPF 共享控件模板，不依赖 Folo 私有 API 或复制其源码。
5. P2-15 Cubox 已取消实施；P2-16～P2-19 已完成 Readeck、Outline、qBittorrent 与受控 Webhook 的共享 schema v2 策略、独立本机设置、健康探针、导出器和显式动作；每种 provider 首版只允许一个本机目标，endpoint/resource 策略元数据只适用于同一 Worker 信任域。旧占位凭据没有新目标文档的 `CredentialVersion=1` 不会启用，必须显式重填或清理。P2-20～P2-22 已完成本地计划、每日/每周摘要、schema v22～v25、系统通知隐私策略、受控激活和 Runtime 降级；P2-23 选择“不扩权”关闭。P2-D 的受控真实外部连通仍开放，详细边界见 [`plans/RSS_P2_VIEWS_INTEGRATIONS.md`](plans/RSS_P2_VIEWS_INTEGRATIONS.md)。
6. Independent-01 JSON 双栏结构 Diff 已完成；Core 对每侧只解析一次，接受合法根值 `null`，使用分块协作取消、无歧义方括号路径和有界路径输出，双栏 UI 支持交换与回收虚拟化差异列表，并在最小 920×620 的真实 `MainWindow`、500 行结果及等效 200% 布局中验证生产滚动区可达；未修改 RSS 模型、SQLite 或 Worker。
7. “洛克王国世界每日清体力自动化”只登记为独立候选调研项，尚未批准 MaaFramework 依赖或任何实现。它不属于 RSS P2 编号；若后续启动，必须先完成条款核对、前台手动登录边界、识别 PoC、进程隔离、停止/失败保护与许可证审查，具体见 [`plans/GAME_AUTOMATION_BACKLOG.md`](plans/GAME_AUTOMATION_BACKLOG.md)。

总路线、参考项目和许可证边界见 [`plans/RSS_MASTER_ROADMAP.md`](plans/RSS_MASTER_ROADMAP.md)，架构决策见 [`decisions/ADR-001-admin-curated-rss.md`](decisions/ADR-001-admin-curated-rss.md)、[`decisions/ADR-002-article-content-extraction.md`](decisions/ADR-002-article-content-extraction.md)、[`decisions/ADR-003-durable-entry-export-queue.md`](decisions/ADR-003-durable-entry-export-queue.md) 与已接受的 [`decisions/ADR-004-server-email-digest-gate.md`](decisions/ADR-004-server-email-digest-gate.md)。P0 与 P1 可作为已验收基础；生产 D1/Worker、首管理员、schema v2/旧客户端策略契约和 Desktop/qBittorrent 部分 canary 已完成，但其余真实 provider、正式安装包、签名、升级和跨物理机矩阵仍按 10.5～10.7 节单独验收。

### 10.3 其他尚未完成的产品功能

本地产品缺口：

- 首页已接入本地 Feed、旧早报、热点、媒体任务和收藏计数；Feed 的收藏、标签、备注和阅读进度编辑已完成，旧早报/热点的完整同等编辑体验仍可在后续统一。
- Feed AI 本地缓存、单条/批量摘要、可恢复条目翻译、管理员策略、共享额度门控和本地自动处理已建立；通用管理员规则的安全契约、权威版本、图形管理/发布 UI、只读模拟、本地解释器、运行账本、ACTIVE 快照缓存、Worker 同步，以及本地状态、AI、媒体和通知动作适配均已建立。
- 洛克王国世界每日清体力自动化：当前只有候选计划，没有依赖、资源、代码、UI、调度器或发布制品；不得把公开项目的使用人数推断为账号安全证据，也不得宣称可规避游戏检测。

云端与管理缺口：

- 客户端已接入共享账号登录、退出、过期状态、角色、额度和管理员目录管理；注册尚未实现。
- Worker 认证、令牌轮换和管理员目录写入已有 workerd/D1 自动化；生产 D1 migration、Worker 公网健康、首管理员/登录、bootstrap 关闭及 schema v2/旧客户端管理契约已验收，D1 并发压测和共享额度代理链路仍未验收。
- 管理员分类/Feed 写 API、普通用户只读目录、ETag/304、桌面角色可见性、本地目录自动同步、安全发现/抓取和管理交互已实现。
- 安全 Feed URL 发现、通用条目解析、抓取调度、OPML 管理、只读时间线、Feed 健康诊断、首页真实数据聚合、全文/图片离线、附件分类与安全外链、自动化规则发布边界、图形管理与只读模拟、本地运行账本、规则同步、受限状态动作、AI 摘要/翻译动作、Feed 媒体投递、本地/Windows 通知、七类实体统一搜索、180 天保留维护以及 P2-21 日/周本地摘要均已实现；P2-01～P2-14 与 P2-16～P2-19 的多内容视图、智能视图和已选外部导出也已完成。P2-23 已选择 A，以“不扩权”关闭，不存在邮件实现；P2-D 仍等待受控真实服务验收。

### 10.4 普通本地使用需要配置

- 云端转写：在设置页保存有效的 Groq API Key。
- 离线转写：导入兼容 whisper.cpp、文件大于 1 MiB 的 `ggml-*.bin` 模型。
- DeepSeek Key：生成单条解读、每日趋势报告或 P2-21 日/周订阅摘要时需要；在设置页保存后由 DPAPI CurrentUser 加密，报告正文和 token 用量写入本地 SQLite。计划不绕过凭据检查；未配置/无效 Key 等永久 4xx 会把当前窗口终止，明确的 429 持久退避，结果不明的网络/超时/5xx 失败不自动重放以避免重复计费。
- Obsidian：管理员先在“外部集成”中启用 Obsidian；本机再从设置页选择一个已存在的本地 Vault 根目录并保存相对子目录、标签、可选内联模板和来源链接开关。只有阅读器中的显式导出按钮会入队，保存设置本身不会写入 Vault。
- Eagle：管理员先在“外部集成”中启用 Eagle；本机设置页默认使用 `http://127.0.0.1:41595/`，也可保存其他带显式端口的数字 loopback HTTP 根地址。Eagle 4.0 Build 21+ 必须正在 Windows 上运行并打开目标资源库；阅读器只对已验证图片显示显式导出动作。
- Zotero：管理员先在“外部集成”中启用 Zotero 并只允许 `api.zotero.org`；本机设置页保存个人库 User ID、显式条目类型和可选笔记/附件开关，API key 进入 DPAPI 后不回读。所需权限至少为 library/write；启用笔记或附件时还分别需要 notes/files。当前不支持群组库，且真实个人库写入仍需受控验收。
- Readwise：管理员先在“外部集成”中启用 Readwise 并只允许 `readwise.io`；本机“个人外部集成”选择 Readwise 后固定 `default` 与官方端点，保存 access token 后再测试。阅读器仅在显式点击当前已预览条目的 `R` 或详情按钮后入队；当前没有真实 token/账户写入验收。
- Readeck：管理员先启用 Readeck，并将实例精确配置为公网 HTTPS 主机或受信私网 HTTPS `{host,port}`；本机专用卡保存一个实例根地址、归档选项和 token。导出会写入可见的 `lenxtool:<stable-id>` 技术标签并据此收敛重放；当前没有真实实例写入验收。
- Outline：管理员先启用 Outline，并同时允许实例 endpoint 与 collection UUID；本机专用卡保存一个 HTTPS 根地址、collection ID 和 API key。同一 Feed 条目使用确定性 UUID 更新同一文档；首版固定创建和更新个人草稿，不会自动向工作区发布，当前没有真实实例写入验收。
- qBittorrent：只支持 5.2+ / WebAPI 2.14.1+ API key。管理员必须允许实例、非空 category，以及使用同机 HTTP 时的精确 localhost 端口；本机专用卡只保存一个目标。资讯页每次投递 magnet/torrent 都要再次确认。5.2.3 / WebAPI 2.15.1 的 loopback 健康、magnet、受控文件上传、重放、清理与撤销 canary 已通过；生产公网 `.torrent` 获取和 200/202/失败状态仍待验收。
- Webhook：管理员先启用 Webhook 并允许精确 HTTPS endpoint；本机专用卡可选择 HMAC-SHA256。接收方必须用 OPTIONS 声明 LenxTool v1 与幂等能力，并在 POST 后精确回显 `LenxTool-Ack`；不支持自定义方法、请求头、Authorization 或正文模板，当前没有真实接收端验收。
- 共享账号：部署 Worker 后，以 `LENXTOOL_WORKER_BASE_URL` 配置其 HTTPS 根地址；登录界面才会启用。该变量不是凭据，账号 refresh token 仍只由 DPAPI CurrentUser 保存。
- WebView2 / Windows App Runtime：当前电脑均已安装。安装版会携带并校验两项 Microsoft 安装资产；便携版若缺少 Windows App Runtime，只会禁用系统通知，应用内收件箱仍可用。
- Microsoft Word：当前电脑已安装，Word 转 PDF 无需额外配置。
- .NET SDK 与 Node/npm：当前开发机已满足；安装自包含正式包的普通用户不需要 .NET SDK。

### 10.5 部署 Worker 前需要配置

- 将 `cloud/LenxTool.Worker/wrangler.toml` 中的 D1 占位 `database_id` 替换为真实 ID。
- 配置必需的 `TOKEN_SECRET`，按启用能力配置 `GROQ_API_KEY`、`DEEPSEEK_API_KEY`。
- 执行远端 D1 migration。
- 临时配置 `BOOTSTRAP_TOKEN` 后，在受控终端运行 `cloud/LenxTool.Worker/scripts/bootstrap-admin.ps1` 初始化首个管理员；成功后立即删除该 Secret。

### 10.6 正式发布前需要配置或完成

- 安装 Inno Setup 6；当前机器未安装，因此不能重新生成 Setup。
- GitHub 更新仓库已配置为 `Empty8492/LenxTool`；正式发布时需在该仓库创建带签名清单与安装包的 Release。
- 提供仓库外的 ECDSA P-256 更新签名私钥路径；私钥不得发到聊天或提交仓库。
- 购买并配置 Authenticode 证书和可信时间戳服务。
- 填写真实发布说明、最低支持版本和强制更新标志，并完成覆盖升级验收。
- 若有意升级 WebView2 或 Windows App Runtime 安装资产，必须从官方来源取得新文件，人工复核版本/签名后显式更新固定 SHA-256；不得为了恢复构建而移除校验。
- 修复当前 `dotnet format LenxTool.slnx --verify-no-changes --no-restore` 暴露的既有全仓编码、空白与导入顺序基线，并在独立提交中重跑完整测试；不得在 provider canary 提交中批量格式化无关文件。
- 当前 Git 仓库可正常识别；正式发布前仍需确认发布提交已推送到 `origin/main`，并让版本标签、清单版本和安装包版本保持一致。

### 10.7 当前制品状态

`Release\LenxTool_Setup.exe` 是 2026-07-20 01:44 生成的旧制品，不含随后完成的媒体、备份、设置、资讯和 P2-22 通知修复。本轮源码已发布到忽略目录 `artifacts\publish\p2-22-final`（315 个顶层文件、167.0 MiB）并完成主程序/系统通知开发验收，但它没有经过 Inno、离线更新签名或 Authenticode 流程。现有 Setup 和 `Release` 中的旧便携包仍需重新构建；在上述发布配置完成并重新运行 `scripts/Build-Release.ps1` 前，不应对外宣称已有最新正式安装包。
