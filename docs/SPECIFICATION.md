# Lenx Tools 产品与工程规格

## 1. 目标

为中文 Windows 用户提供一款本地优先、离线可降级、可批量执行的桌面效率应用。首个正式版本覆盖资讯中心、媒体工作台、轻工具、历史与数据、共享额度账号和安全更新，且能够自包含安装在 Windows 10/11 x64。

成功意味着：应用可编译、可测试、可运行、可安装；用户数据和自备密钥不随升级丢失；远端故障不会让已缓存内容不可用；所有长任务都可取消并留下可诊断的历史记录。

## 2. 已确认的保守默认值

1. 产品显示名为“Lenx Tools”，程序集和命名空间使用 `LenxTool`。
2. 安装范围默认当前用户，不要求管理员权限；固定 AppId 保证覆盖升级。
3. 数据目录固定为 `%LocalAppData%\LenxTool`；数据库为 `Data\lenx.db`，密钥为当前 Windows 用户作用域的 DPAPI 加密文件。
4. 首版更新源使用 GitHub Releases；更新清单通过 ECDSA P-256/SHA-256 离线私钥签名，客户端仅内置公钥并支持镜像数组。
5. Word 转 PDF 首版以适配器隔离具体实现；运行时优先使用 Microsoft Word COM（如已安装），未安装时返回可操作错误，不宣称无损转换。
6. 本地 Whisper 模型不进入安装包。用户导入 `ggml-*.bin`，识别引擎通过独立适配器加载；共享 Groq 请求只经 Cloudflare Worker 流式转发。
7. 云端不持久化新闻缓存/全文、AI 摘要或译文、字幕结果和本地文件信息；共享 AI/语音请求所需的正文或媒体字节只在请求生命周期中转，不写入 D1/R2/KV、日志或审计。
8. 减少动画跟随系统设置，并允许应用内覆盖；动画限定为透明度和位移。
9. 公共热点源属于不稳定外部依赖，单源失败只生成警告，展示其他源或本地缓存。

## 3. 技术栈

- .NET 10、C#、WPF、x64、Nullable、隐式 using。
- MVVM、构造函数依赖注入、`IHttpClientFactory`、`CancellationToken`。
- SQLite + FTS5；所有写入显式事务；迁移前自动备份。
- 每日早报使用原生 WPF 只读视图；WebView2 只作为未来受控 HTML/Markdown 能力的可选承载。
- xUnit 单元测试和真实临时 SQLite 集成测试。
- Cloudflare Workers（TypeScript）+ D1 负责账号、邀请码、角色、额度、用量、令牌刷新与审计。
- Inno Setup 生成安装程序；`dotnet publish` 生成自包含 win-x64 应用与便携 ZIP。

## 4. 工程命令

```powershell
dotnet restore LenxTools.slnx
dotnet build LenxTools.slnx -c Release --no-restore
dotnet test LenxTools.slnx -c Release --no-build --logger "console;verbosity=normal"
dotnet publish src/LenxTool.App/LenxTool.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
powershell -ExecutionPolicy Bypass -File scripts/Build-Release.ps1 `
  -Version 0.1.0 `
  -PrivateKeyPath D:\Offline\lenxtool-private.pem `
  -Repository Empty8492/LenxTools
```

Worker：

```powershell
cd cloud/LenxTool.Worker
npm ci
npm run typecheck
npm test
npx wrangler d1 migrations apply lenx-tool --local
```

## 5. 项目结构

```text
src/LenxTool.Core/                    领域模型、接口、错误类型、纯逻辑
src/LenxTool.Infrastructure/          SQLite、网络、AI、文件、系统、更新实现
src/LenxTool.App/                     WPF 视图、主题、ViewModel、应用组合根
tests/LenxTool.Core.Tests/            快速纯逻辑测试
tests/LenxTool.Infrastructure.Tests/  SQLite/文件/网络边界集成测试
cloud/LenxTool.Worker/                Cloudflare Worker 与 D1 迁移
installer/                            Inno Setup 脚本与更新公钥
scripts/                              可重复构建、打包和验收脚本
docs/                                 规格、架构、ADR、威胁模型和使用说明
artifacts/                            本地发布产物（不提交）
```

## 6. 代码风格

- 类型、成员用 PascalCase，局部变量和参数用 camelCase，私有字段用 `_camelCase`。
- `async` 方法以 `Async` 结尾并接受 `CancellationToken`；禁止同步阻塞异步代码。
- View 的后台代码只处理窗口生命周期、焦点和视觉树事件；业务行为进入 ViewModel/Service。
- 跨层只依赖 `Core` 中的接口和不可变模型。
- 不捕获无法处理的异常；不得使用空 `catch`。清理失败必须至少写脱敏日志。

```csharp
public sealed class NewsSearchService(INewsRepository repository) : INewsSearchService
{
    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken) =>
        repository.SearchAsync(query.Normalize(), cancellationToken);
}
```

## 7. 功能验收

### 应用外壳

- 深石墨侧边栏、暖白/深色内容区，布局在 100%～200% 缩放和 900×620 最小窗口下可用。
- 全部功能图标为矢量 Path，键盘可达，焦点可见；`Ctrl+K` 打开全局命令面板。
- 首页显示今日早报、热点、最近任务、收藏和快捷入口；深色与减少动画设置可持久化。
- 首页数据来自本地 ACTIVE Feed 条目、旧早报兼容表、热点、媒体任务和 favorites 计数；空目录管理员新建 Feed 表单预填 `https://daily.juya.uk/rss.xml` 兼容种子。旧 schema v2 早报可继续搜索/阅读，首页与统一搜索不得重复显示相同规范 URL 的迁移条目。

### 资讯中心

- 获取、事务保存并筛选每日早报和多平台热点；相同规范化 URL 或内容指纹去重。
- 管理员可维护共享 Feed/分类，并通过有界安全预览选择性导入 OPML；批量写入原子提交，导出只包含共享目录字段。普通用户没有目录写入口。
- 共享 Feed 条目以 50 条为一页稳定加载，支持全部/分类/Feed、发布日期和至多 200 字符关键词筛选；列表使用回收虚拟化，选择条目后由原生只读阅读器显示净化内容。
- 管理员可在订阅管理的健康页查看每个本机 Feed 的最后成功/失败、连续失败、下次重试和固定错误类别，并对启用 Feed 发起安全强制重试；页面不得展示令牌、完整响应或内部网络详情。
- 支持日期、平台、关键词、收藏、标签、备注和七类实体 FTS5 全文搜索（Feed、旧早报、热点、AI 报告、字幕、标签、收藏）；Feed 时间线的已读/收藏/进度基础状态保存在本机 `user_entry_states`，不会上传或改变共享目录。
- 管理员可发布受限 AI 策略和自动化规则；普通用户只能读取 ACTIVE 目录/规则。规则只允许封闭字段、操作符和七类动作，不接受脚本、命令或任意网络请求；动作计划、租约、媒体投递和应用内通知在本机幂等恢复。
- 远端失败时返回带最后抓取和目录同步时间的本地结果；默认只清理严格早于 180 天且没有收藏、标签、备注、私人状态或全文/AI/规则/媒体活动引用的条目。
- P1 大库验收必须在重开的真实 SQLite 中覆盖 10,000 条 Feed、1,000 个收藏、混合媒体和离线查询：收藏分页/统一搜索各自不超过 2 秒，清理预览不超过 10 秒，有界清理不超过 60 秒；真实 workerd/D1 必须验证普通用户目录/策略/规则写入为 403 且内容字段不落库。
- 单条 AI 解读与每日趋势报告可取消，并记录模型、token/请求用量和脱敏错误。

### 媒体工作台

- 批量队列包含排队、运行、完成、失败、取消状态；支持取消、重试、历史、打开输出目录。
- 支持 Groq Whisper、自备 Groq Key、共享 Worker 代理和本地 Whisper。
- 长音频按重叠窗口分片，传递上下文，按时间交接去重，并过滤低置信度非语音片段。
- 可导出原文 SRT、译文 SRT、双语 SRT 和纯文本 TXT；路径支持中文、空格和长文件名。

### 轻工具与历史

- Word 转 PDF 只通过 `IDocumentConverter`；JSON 支持格式化、压缩、校验、排序和结构 Diff。
- Base64、URL 编解码、SHA-256、文本去重和空行清理可离线使用。
- 历史页可检索、重试任务、打开输出、查看错误和模型使用量；数据库可一键备份与恢复。

### 云端账号

- 邀请码注册；普通用户默认每日 10 分钟共享语音、10 次共享 AI；管理员无限额。
- 管理员可按用户或邀请码设置额度、禁用账号；额度扣减防并发超额。
- 短期 access token + 可轮换 refresh token；基础审计不记录正文、字幕或文件。

### 更新与发布

- 启动后台检查，设置页手动检查；语义版本、最低版本和强制安全更新均正确处理。
- 展示版本、大小、更新日志；下载有进度、SHA-256 和签名校验，确认后静默覆盖安装并重启。
- 生成 `Release/LenxTool_Setup.exe` 和便携 ZIP；卸载默认保留 `%LocalAppData%\LenxTool`。

## 8. 测试策略

- 单元测试：版本比较、错误映射、额度、分片/去重、SRT、JSON、编码、哈希、内容指纹。
- 集成测试：临时 SQLite 建库/迁移/FTS5/事务/备份恢复/损坏检测；HTTP 假服务器覆盖 400/401/429/500/超时/断网。
- Worker 测试：邀请码并发注册、并发额度预留、刷新令牌轮换、禁用用户、管理员豁免，以及目录/AI 策略/规则的 RBAC、版本、幂等、ACTIVE/ALL 和 D1 内容隐私边界。
- 发布烟测：中文用户名、中文/空格路径、只读目录、覆盖升级和卸载保留数据。
- 手动 UI：100/125/150/200% DPI、900×620/1920×1080、键盘、深浅主题、减少动画。

## 9. 边界

始终执行：参数化 SQL、输入长度限制、HTTPS、事务、取消传播、脱敏日志、发布校验、测试后提交。

需要产品负责人另行决定：收费体系、正式品牌证书、新闻源商业授权、云存储正文、系统级全局快捷键。

永不执行：提交密钥/私钥/固定管理员密码；将 Key 写入 SQLite；记录密码、令牌或完整敏感正文；静默绕过更新签名；把模型塞进主安装包；修改旧项目。

## 10. 正式版本整体完成定义

- Release 构建零新增警告，全部测试通过且无跳过。
- 冷启动可创建/迁移数据库并进入首页；关键离线功能不依赖网络。
- 安装包与便携包均能在无 .NET Runtime 的 Windows x64 环境启动。
- README、用户指南、发布说明、架构和回滚说明与实现一致。

### P0 终验记录（2026-07-24）

P0 管理员策展 RSS 的自动化终验已通过：Worker/D1 workerd 测试覆盖管理员 bootstrap/login、分类与 Feed 发布/刷新/停用/审计、普通用户同步阅读和全部管理写端点 401/403 隔离；本地测试覆盖 OPML 导入/导出安全边界、断网缓存保留、坏源隔离、schema v2 原位升级和 10k 条目首屏虚拟化。Release 构建 0 警告/0 错误，.NET 310/310、Worker 39/39、Worker strict typecheck 通过。

### P1 终验记录（2026-07-27）

P1 阅读增强、AI 与自动化的本地/自动化终验已通过：真实 schema v17 SQLite 覆盖 10,000 条 Feed、1,000 个收藏、混合媒体、离线重开、七类统一搜索和安全清理；真实 workerd/D1 覆盖管理员目录/AI 策略/规则发布、普通用户三类写入 403、版本不变和正文/AI/字幕/本地路径不落 D1。Release 结果为 .NET 648/648、Worker 52/52、Worker strict typecheck 与全解决方案构建 0 警告/0 错误。此记录只关闭 P1 阶段，不代表生产 Worker/D1、签名安装包、升级路径和正式版本整体完成定义已经满足。
