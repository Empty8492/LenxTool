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
├─ installer/                   Inno Setup、WebView2 引导程序、公钥
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

- JSON：格式化、压缩、语法校验、属性排序和结构 Diff 核心算法。
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

## 5. SQLite 数据

数据库：`%LocalAppData%\LenxTool\Data\lenx.db`。

主要表：`news_articles`、`trend_items`、`ai_reports`、`media_jobs`、`subtitle_segments`、`favorites`、`tags`、`entity_tags`、`app_settings`、`feed_catalog_state`、`feed_categories`、`feed_catalog`、`feed_fetch_state`、`feed_entries`、`user_entry_states`、`schema_versions`。

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
- Worker：PBKDF2-SHA256 310,000 次、短期 HMAC access token、refresh token 哈希与轮换、禁用账号即时失效。
- 配额：D1 条件 UPDATE 原子预留，成功后结算，防止并发超额；管理员跳过共享额度。
- Worker 不写入新闻、字幕、音视频或请求正文；音频请求体直接流式转发。

## 8. 更新与安装

- 清单支持多个 HTTPS mirror，客户端无需因 OSS/COS 镜像扩展而升级。
- 使用语义版本选择最高候选版本。
- 支持 `MinimumSupportedVersion` 与 `MandatorySecurityUpdate`。
- 安装器固定 AppId：`{D13CF52E-A89C-4CC6-A888-3CA9F4CCB2B4}`。
- Inno Setup 支持覆盖升级、开始菜单、可选桌面快捷方式、静默安装和卸载。
- WebView2 Evergreen Bootstrapper 随安装器提供并静默检查/安装。
- 用户数据、模型、设置和密钥位于 LocalAppData，覆盖升级及默认卸载均保留。
- 正式发布前需要给 EXE/Setup 增加 Authenticode；Inno 脚本已保留 SignTool 接口。

## 9. 构建与运行

前置：Windows x64、.NET SDK 10.0.302。开发机需安装 WebView2 Runtime；Word 转 PDF 需要 Microsoft Word。

```powershell
dotnet restore LenxTools.slnx
dotnet build LenxTools.slnx -c Release
dotnet test LenxTools.slnx -c Release
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

本节是当前交付状态的唯一准绳，最后核对日期为 **2026-07-24**。`IMPLEMENTATION_PLAN.md` 保留完整任务与验收条件；其中未勾选的任务可能已有部分实现，但表示尚未满足该任务的全部验收条件。

### 10.1 本轮已完成

- “资讯列表”“每日早报”“热点趋势”和“AI 报告”提升为左侧一级菜单；早报默认当天并支持按缓存日期切换，正文改用原生 WPF 显示。
- 早报正文按原始顺序显示 HTML/Markdown 配图；图片请求限制为 HTTP/HTTPS、最大 12 MiB，并提供加载超时或失败提示。资讯页采用贴右侧的单一整页滚动条，页内标题和操作区随阅读滚动；回到顶部按钮在滚动后渐显，并使用可被系统“减少动画”关闭的缓动回顶。
- 首页早报卡片直接从结构化“概览”开始，最多显示 5 个栏目、每栏 3 条；已移除重复的卡片标题、状态、来源和日期区。壳层统一左右表面并移除重复页标题，热点卡片使用与浅色工作区一致的暖灰表面。
- 资讯筛选下拉框按 `Label` 正确显示，不再泄漏筛选对象文本；空状态完整覆盖列表与阅读器。每日早报使用 1.45 倍滚轮倍率，切换一级资讯入口时自动回到顶部。
- 图片网络策略兼容公网 HTTPS 主机经 DNS 返回的 `198.18.0.0/15` Fake-IP，同时继续阻止直接访问该保留网段。
- 热点源扩展为 TrendRadar 默认 11 个平台并保留 GitHub、Hacker News，共 13 个来源；采用 NewsNow 当前接口与兼容请求头、HTTPS/预期域名校验、单源失败隔离和按平台替换缓存快照。
- 热点趋势改为两列平台卡片，名次在平台内部独立显示；热点条目提供悬停/按下/焦点反馈与受限 HTTP/HTTPS 原文打开，去除吞掉鼠标滚轮的嵌套列表。
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
- P1-15 Core 确定性解释器、运行账本、动作租约、受限本地状态动作与后台处理器已完成：ACTIVE 规则先验证并编译为可复用只读规则集，按优先级降序、冲突顺序升序、规则 ID 和规则内动作顺序稳定计算。九类字段、ALL/ANY、带时区时间、音视频存在性和非回溯 regex 均在纯内存中匹配；加标签按本地 `NOCASE` 语义去重，其余副作用动作全局只保留第一个胜者。schema v12 在单个 SQLite 事务中保存每个条目/规则版本的命中结果和全部动作决策，计划动作使用稳定幂等键，被抑制动作保留胜者引用；同一版本重放或重启后再次暂存都不会追加记录。待执行动作按确定顺序和显式动作类型集合领取，租约过期可回收，旧持有者不能提交；失败可延时重试，主动释放可立即重领，成功/永久失败进入不可再次领取的终态。schema v13 增加按本地档案隔离的隐藏状态，普通时间线默认排除隐藏条目；本地执行服务只接受加标签、隐藏和标为已读，先做有界载荷校验，再调用原子追加标签或私人状态仓储。追加标签保留现有标签与同名标签颜色，重放不重复关联；缺失条目返回可终结结果。后台处理器只领取上述三类本地动作，以最多 4 并发执行；缺失条目和非法载荷永久失败，可重试应用错误尊重受限 `Retry-After`，未知错误指数退避，5 次后终结，取消会释放已经领取且仍在并发门外等待的租约。AI、媒体和通知动作不会被本地处理器领取，等待各自受限适配器；数据库事务仍不请求网络。
- P1-15 ACTIVE 规则缓存已完成：schema v14 用单例状态和按稳定顺序索引的规则表保存 Worker 规则集版本、生成/同步时间及完整受限规则。仓储写入前复用 Core 验证器与解释器编译，拒绝停用规则、重复 ID、旧/同版本覆盖、非 UTC 时间和超量快照；规则规范化后在单个事务中整体替换，重启后可完整恢复。读取会交叉核对独立元数据与 JSON，并把格式或语义损坏统一视为无效本地缓存；同步时间只允许在预期版本仍匹配时更新。下一片接入 Worker `ACTIVE` 规则同步与 Feed 刷新后的本地计划暂存。
- P0 最终验收第一片已完成：`p0-final-acceptance.test.ts` 在真实 workerd/D1 中走临时 bootstrap/login，覆盖管理员发布与停用、目录刷新、普通用户同步/阅读、六类管理员写端点 403 隔离和审计字段脱敏。
- P0 最终验收第二片已完成：完整 .NET 310/310、Worker 39/39、typecheck 和 Release 0 警告/0 错误复核了 OPML 导入/导出、断网缓存、坏源隔离、schema v2 原位升级和 10k 条目首屏性能；五份终验文档已同步，P0 已关闭。

### 10.2 下一里程碑

Gate 0 字幕闭环和 P0“管理员策展 RSS”已经完成。P1-01～P1-14、P1-A、P1-B 与 P1-C 已完成；P1-15 Core 确定性解释器、SQLite v12～v14 运行账本/动作租约/受限本地状态动作/ACTIVE 规则缓存和后台处理器已完成。下一切片进入 Worker 规则同步与 Feed 刷新触发点。

字幕闭环完成后的产品主路线已确定为“管理员策展 RSS”：管理员维护共享 RSS/Atom 目录，普通用户只能同步和阅读，不得修改共享订阅、分类、抓取策略或自动化规则。为保持现有“云端不存新闻正文”边界，首版采用 Worker/D1 保存权威目录、各桌面客户端本地抓取和 SQLite 缓存的模式。

详细执行顺序如下：

1. Gate 0 字幕闭环已完成；验收记录见 [`plans/EXISTING_BACKLOG_ALIGNMENT.md`](plans/EXISTING_BACKLOG_ALIGNMENT.md)。
2. P0-01～P0-20、P0-B/P0-C 及最终检查点已完成；P0 关闭记录见 [`plans/RSS_P0_ADMIN_CATALOG.md`](plans/RSS_P0_ADMIN_CATALOG.md)，现在才进入 P1。
3. P1-01～P1-14、P1-A、P1-B 与 P1-C 已完成；P1-15 Core 解释器、SQLite v12～v14 运行账本/动作租约/受限本地状态动作/ACTIVE 规则缓存和后台处理器已完成，继续 Worker 规则同步与 Feed 刷新触发点，具体见 [`plans/RSS_P1_READING_INTELLIGENCE.md`](plans/RSS_P1_READING_INTELLIGENCE.md)。
4. 实现多内容视图、外部导出适配器、本地定时摘要和通知；具体见 [`plans/RSS_P2_VIEWS_INTEGRATIONS.md`](plans/RSS_P2_VIEWS_INTEGRATIONS.md)。

总路线、参考项目和许可证边界见 [`plans/RSS_MASTER_ROADMAP.md`](plans/RSS_MASTER_ROADMAP.md)，架构决策见 [`decisions/ADR-001-admin-curated-rss.md`](decisions/ADR-001-admin-curated-rss.md) 与 [`decisions/ADR-002-article-content-extraction.md`](decisions/ADR-002-article-content-extraction.md)。P0-01～P0-20、P0-B/P0-C、P1-01～P1-14 与 P1-A/P1-B/P1-C 可作为已实现基础；规则本地执行/模拟管理、媒体投递和外部导出仍不能作为已交付功能宣传。

### 10.3 其他尚未完成的产品功能

本地产品缺口：

- 首页已接入本地 Feed、旧早报、热点、媒体任务和收藏计数；资讯收藏、标签、备注的完整编辑入口仍待完成。
- Feed AI 本地缓存、单条/批量摘要、可恢复条目翻译、管理员策略、共享额度门控和本地自动处理已建立；通用管理员规则的安全契约、权威版本、发布 API、本地解释器、运行账本、受限本地动作处理器和 ACTIVE 快照缓存已建立，Worker 同步、非本地动作适配与管理 UI 仍待 P1-15/P1-16。
- JSON 双栏结构 Diff 界面；目前只有 Core 层 Diff 算法。

云端与管理缺口：

- 客户端已接入共享账号登录、退出、过期状态、角色、额度和管理员目录管理；注册尚未实现。
- Worker 认证、令牌轮换和管理员目录写入已有 workerd/D1 自动化；生产 D1 并发压测、共享额度代理链路和真实部署仍未验收。
- 管理员分类/Feed 写 API、普通用户只读目录、ETag/304、桌面角色可见性、本地目录自动同步、安全发现/抓取和管理交互已实现。
- 安全 Feed URL 发现、通用条目解析、抓取调度、OPML 管理、只读时间线、Feed 健康诊断、首页真实数据聚合、全文/图片离线、自动化规则发布边界、本地运行账本与受限状态动作已实现；规则同步/非本地动作/管理和外部导出仍待后续任务。

### 10.4 普通本地使用需要配置

- 云端转写：在设置页保存有效的 Groq API Key。
- 离线转写：导入兼容 whisper.cpp、文件大于 1 MiB 的 `ggml-*.bin` 模型。
- DeepSeek Key：生成单条解读或每日趋势报告时需要；在设置页保存后由 DPAPI CurrentUser 加密，报告正文和 token 用量写入本地 SQLite。
- 共享账号：部署 Worker 后，以 `LENXTOOL_WORKER_BASE_URL` 配置其 HTTPS 根地址；登录界面才会启用。该变量不是凭据，账号 refresh token 仍只由 DPAPI CurrentUser 保存。
- WebView2 Runtime：当前电脑已安装；早报正文已不再依赖它，安装器和未来富文本能力仍保留运行时检查。
- Microsoft Word：当前电脑已安装，Word 转 PDF 无需额外配置。
- .NET SDK 与 Node/npm：当前开发机已满足；安装自包含正式包的普通用户不需要 .NET SDK。

### 10.5 部署 Worker 前需要配置

- 将 `cloud/LenxTool.Worker/wrangler.toml` 中的 D1 占位 `database_id` 替换为真实 ID。
- 配置必需的 `TOKEN_SECRET`，按启用能力配置 `GROQ_API_KEY`、`DEEPSEEK_API_KEY`。
- 执行远端 D1 migration。
- 临时配置 `BOOTSTRAP_TOKEN` 后，在受控终端运行 `cloud/LenxTool.Worker/scripts/bootstrap-admin.ps1` 初始化首个管理员；成功后立即删除该 Secret。

### 10.6 正式发布前需要配置或完成

- 安装 Inno Setup 6；当前机器未安装，因此不能重新生成 Setup。
- GitHub 更新仓库已配置为 `Empty8492/LenxTools`；正式发布时需在该仓库创建带签名清单与安装包的 Release。
- 提供仓库外的 ECDSA P-256 更新签名私钥路径；私钥不得发到聊天或提交仓库。
- 购买并配置 Authenticode 证书和可信时间戳服务。
- 填写真实发布说明、最低支持版本和强制更新标志，并完成覆盖升级验收。
- 当前 Git 仓库可正常识别；正式发布前仍需确认发布提交已推送到 `origin/main`，并让版本标签、清单版本和安装包版本保持一致。

### 10.7 当前制品状态

`Release\LenxTool_Setup.exe` 是 2026-07-20 01:44 生成的旧制品，不含随后完成的媒体、备份、设置和资讯修复。包含本轮源码的开发验收便携包为 `artifacts\LenxTool_Portable_0.1.0-preview-rich-reader.zip`；它未经过正式签名发布流程。现有 Setup 和 `Release` 中的旧便携包仍需重新构建；在上述发布配置完成并重新运行 `scripts/Build-Release.ps1` 前，不应对外宣称已有最新正式安装包。
