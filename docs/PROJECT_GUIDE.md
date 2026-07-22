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

### 4.2 资讯中心

- 聚合每日早报，以及通过 NewsNow API 获取的 13 个热点平台：TrendRadar 默认的知乎、抖音、bilibili 热搜、华尔街见闻、贴吧、百度热搜、财联社热门、澎湃新闻、凤凰网、今日头条、微博，并保留 GitHub 与 Hacker News。
- 数据源部分失败时返回成功来源；完全断网时回退 SQLite 缓存。
- 文章以内容指纹去重并保存；FTS5 覆盖早报、热点和 AI 报告。
- “每日早报”和“热点趋势”是两个独立标签页；早报默认选择当天，有缓存的历史日期可从日期下拉框切换。
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

主要表：`news_articles`、`trend_items`、`ai_reports`、`media_jobs`、`subtitle_segments`、`favorites`、`tags`、`entity_tags`、`app_settings`、`schema_versions`。

实现原则：

- WAL、foreign_keys、busy_timeout 和 integrity check。
- 所有迁移和批量写入使用事务。
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

本节是当前交付状态的唯一准绳，最后核对日期为 **2026-07-22**。`IMPLEMENTATION_PLAN.md` 保留完整任务与验收条件；其中未勾选的任务可能已有部分实现，但表示尚未满足该任务的全部验收条件。

### 10.1 本轮已完成

- 资讯中心拆分为“每日早报”和“热点趋势”两个页面；早报默认当天并支持按缓存日期切换，正文改用原生 WPF 显示。
- 早报正文按原始顺序显示 HTML/Markdown 配图；图片请求限制为 HTTP/HTTPS、最大 12 MiB，并提供加载超时或失败提示。资讯页采用贴右侧的单一整页滚动条，页内标题和操作区随阅读滚动；回到顶部按钮在滚动后渐显，并使用可被系统“减少动画”关闭的缓动回顶。
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

### 10.2 下一里程碑

Gate 0 字幕闭环已经完成。P0“管理员策展 RSS”已完成服务端契约、身份生命周期、目录 schema、管理员 CRUD 和只读目录（P0-01～P0-05）；下一里程碑是 P0-06“桌面会话模型与安全令牌存储”，不再继续扩张字幕数据模型。

字幕闭环完成后的产品主路线已确定为“管理员策展 RSS”：管理员维护共享 RSS/Atom 目录，普通用户只能同步和阅读，不得修改共享订阅、分类、抓取策略或自动化规则。为保持现有“云端不存新闻正文”边界，首版采用 Worker/D1 保存权威目录、各桌面客户端本地抓取和 SQLite 缓存的模式。

详细执行顺序如下：

1. Gate 0 字幕闭环已完成；验收记录见 [`plans/EXISTING_BACKLOG_ALIGNMENT.md`](plans/EXISTING_BACKLOG_ALIGNMENT.md)。
2. P0-01～P0-05 已完成；下一步实现 P0-06 桌面会话与安全令牌存储，再继续本地目录、安全抓取、OPML、时间线和首页真实数据；具体见 [`plans/RSS_P0_ADMIN_CATALOG.md`](plans/RSS_P0_ADMIN_CATALOG.md)。
3. 实现私人阅读状态、全文/图片离线、AI 摘要/翻译、管理员规则、媒体衔接和统一搜索；具体见 [`plans/RSS_P1_READING_INTELLIGENCE.md`](plans/RSS_P1_READING_INTELLIGENCE.md)。
4. 实现多内容视图、外部导出适配器、本地定时摘要和通知；具体见 [`plans/RSS_P2_VIEWS_INTEGRATIONS.md`](plans/RSS_P2_VIEWS_INTEGRATIONS.md)。

总路线、参考项目和许可证边界见 [`plans/RSS_MASTER_ROADMAP.md`](plans/RSS_MASTER_ROADMAP.md)，架构决策见 [`decisions/ADR-001-admin-curated-rss.md`](decisions/ADR-001-admin-curated-rss.md)。只有上述 P0-01～P0-05 可作为已实现的服务端基础；桌面接线、抓取和后续 P1/P2 仍不能作为已交付功能宣传。

### 10.3 其他尚未完成的产品功能

本地产品缺口：

- 首页仍使用演示数据，尚未接入资讯和任务仓储。
- 资讯收藏、标签、备注的完整编辑入口。
- 早报正文配图当前从来源站点的 HTTP/HTTPS 地址加载，富文本和链接会持久化，但图片文件尚未下载到本地缓存；完全离线时正文仍可读，配图会显示加载失败提示。
- JSON 双栏结构 Diff 界面；目前只有 Core 层 Diff 算法。

云端与管理缺口：

- 客户端共享账号登录、注册、额度展示和管理端；Worker 虽有接口，但桌面端尚未接线。
- Worker 认证、令牌轮换和管理员目录写入已有 workerd/D1 自动化；生产 D1 并发压测、共享额度代理链路和真实部署仍未验收。
- 管理员分类/Feed 写 API 已实现；普通用户只读目录、目录 ETag/304、桌面角色与管理界面尚未实现。
- 通用 Feed 条目模型、安全发现/抓取、OPML、时间线、Feed 健康、全文/图片离线、自动化规则和外部导出均仅有详细计划，尚无实现代码。

### 10.4 普通本地使用需要配置

- 云端转写：在设置页保存有效的 Groq API Key。
- 离线转写：导入兼容 whisper.cpp、文件大于 1 MiB 的 `ggml-*.bin` 模型。
- DeepSeek Key：生成单条解读或每日趋势报告时需要；在设置页保存后由 DPAPI CurrentUser 加密，报告正文和 token 用量写入本地 SQLite。
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
