# Lenx 工具箱项目文档

## 1. 项目定位

Lenx 工具箱是面向 Windows 10/11 x64 的本地优先桌面工具。项目从 `L:\RealTimeTranslator` 的功能经验中重构而来，但没有继承旧项目的单窗口业务堆叠、明文密钥、固定管理员口令、IE WebBrowser 或捆绑模型做法。旧目录始终只读。

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

- 聚合每日早报、Hacker News RSS 和 GitHub 热点。
- 数据源部分失败时返回成功来源；完全断网时回退 SQLite 缓存。
- 文章以内容指纹去重并保存；FTS5 覆盖早报、热点和 AI 报告。
- “每日早报”和“热点趋势”是两个独立标签页；早报默认选择当天，有缓存的历史日期可从日期下拉框切换。
- 早报正文使用原生 WPF 富文本视图，不再依赖 WebView2；支持 RSS 封面图、标题层级、项目列表、可点击 HTTP/HTTPS 链接和朗读标记清理，避免运行时初始化异常造成黑屏。
- 数据层已预留收藏、标签、实体标签、备注、AI 报告和 180 天保留策略所需表结构。

### 4.3 媒体工作台

- 批量选择音频/视频，顺序队列执行。
- NAudio Media Foundation 将媒体统一为 16 kHz、16-bit、mono WAV。
- Groq Whisper：自备 Key，HTTP 请求级授权，超时/断网/429 分级错误。
- 本地 Whisper：Whisper.net CPU runtime；导入 `ggml-*.bin`，模型不进入主安装包。
- 长音频按 5 分钟切片、10 秒重叠；后片携带最多 180 字上下文。
- 分片结果使用中点交接、连续重复消除和 `no_speech_prob/avg_logprob` 过滤。
- 任务支持进度、取消、失败状态、历史持久化；异常退出的 Running 任务会恢复为可重试失败。
- 自动导出 UTF-8 SRT 到 `%LocalAppData%\LenxTool\Output`，可直接打开输出。

### 4.4 文档与数据工具

- JSON：格式化、压缩、语法校验、属性排序和结构 Diff 核心算法。
- Base64 和 URL 编解码。
- 文本重复行删除、空白归一和空行折叠。
- SHA-256 文件校验，支持取消。
- Word 转 PDF 位于独立 `IDocumentConverter` 适配器；仅在本机安装 Microsoft Word 时启用。

### 4.5 历史、数据与设置

- 查看最近媒体任务、引擎、模型、输出和结构化错误。
- 打开输出文件。
- SQLite 一键备份；恢复前自动备份当前数据库并执行完整性检查。
- Groq/DeepSeek 自备 Key 通过 PasswordBox 输入，保存后立即清空界面内存。
- Key 使用 Windows DPAPI CurrentUser 加密，不进入 SQLite。
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

### 10.1 本轮已完成

- 资讯中心拆分为“每日早报”和“热点趋势”两个页面；早报默认当天并支持按缓存日期切换，正文改用原生 WPF 显示。
- 媒体任务以 Queued 状态持久化，持续写入 Running 进度，正确区分完成/失败计数，并提供失败或取消任务重试。
- 数据库迁移前使用 SQLite 在线备份 API，生成包含已提交 WAL 内容的一致性快照。
- 深色模式和减少动画设置写入 SQLite 并在重启后恢复；减少动画值已进入主题资源供动效消费。
- “历史与数据”已提供 FTS5 全局搜索界面，统一检索已缓存的早报、热点和 AI 报告，支持结果详情与受限 HTTP/HTTPS 来源打开。
- 资讯中心已接入自备 DeepSeek Key 的单条早报解读和每日趋势报告；使用 `deepseek-v4-flash` 非思考模式，支持取消、结构化错误、token 用量记录、SQLite 持久化和 FTS5 检索。

### 10.2 尚未完成的产品功能

- 资讯收藏、标签、备注的完整编辑入口。
- 早报封面当前从来源站点的 HTTPS 地址加载，富文本和链接会持久化，但图片文件尚未下载到本地缓存；完全离线时正文仍可读，封面可能不显示。
- SRT 导入、字幕翻译、双语 SRT/TXT 导出；当前只自动导出原文 SRT。
- JSON 双栏结构 Diff 界面；目前只有 Core 层 Diff 算法。
- 客户端共享账号登录、注册、额度展示和管理端；Worker 虽有接口，但桌面端尚未接线。
- 字幕片段持久化、历史检索和模型/token 用量展示。
- 首页仍使用演示数据，尚未接入资讯和任务仓储。
- Worker 的认证、令牌轮换、并发额度和 Groq/DeepSeek 代理链路仍缺少充分自动化验收；当前只有基础安全测试。

### 10.3 普通本地使用需要配置

- 云端转写：在设置页保存有效的 Groq API Key。
- 离线转写：导入兼容 whisper.cpp、文件大于 1 MiB 的 `ggml-*.bin` 模型。
- DeepSeek Key：生成单条解读或每日趋势报告时需要；在设置页保存后由 DPAPI CurrentUser 加密，报告正文和 token 用量写入本地 SQLite。
- WebView2 Runtime：当前电脑已安装；早报正文已不再依赖它，安装器和未来富文本能力仍保留运行时检查。
- Microsoft Word：当前电脑已安装，Word 转 PDF 无需额外配置。
- .NET SDK 与 Node/npm：当前开发机已满足；安装自包含正式包的普通用户不需要 .NET SDK。

### 10.4 部署 Worker 前需要配置

- 将 `cloud/LenxTool.Worker/wrangler.toml` 中的 D1 占位 `database_id` 替换为真实 ID。
- 配置必需的 `TOKEN_SECRET`，按启用能力配置 `GROQ_API_KEY`、`DEEPSEEK_API_KEY`。
- 执行远端 D1 migration。
- 在受控终端手工初始化首个管理员；仓库目前没有可直接使用的 bootstrap 脚本。

### 10.5 正式发布前需要配置或完成

- 安装 Inno Setup 6；当前机器未安装，因此不能重新生成 Setup。
- GitHub 更新仓库已配置为 `Empty8492/LenxTools`；正式发布时需在该仓库创建带签名清单与安装包的 Release。
- 提供仓库外的 ECDSA P-256 更新签名私钥路径；私钥不得发到聊天或提交仓库。
- 购买并配置 Authenticode 证书和可信时间戳服务。
- 填写真实发布说明、最低支持版本和强制更新标志，并完成覆盖升级验收。
- 恢复或初始化正确的 Git 元数据；当前目录无法被 Git 识别，尚不能核对提交与远端状态。

### 10.6 当前制品状态

`Release\LenxTool_Setup.exe` 是 2026-07-20 01:44 生成的旧制品，不含随后完成的媒体、备份、设置和资讯修复。包含本轮源码的开发验收便携包为 `artifacts\LenxTool_Portable_0.1.0-preview-rich-reader.zip`；它未经过正式签名发布流程。现有 Setup 和 `Release` 中的旧便携包仍需重新构建；在上述发布配置完成并重新运行 `scripts/Build-Release.ps1` 前，不应对外宣称已有最新正式安装包。
