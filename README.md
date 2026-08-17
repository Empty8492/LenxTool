# Lenx Tools

Lenx Tools 是面向 Windows 10/11 x64 的本地优先桌面效率应用，统一承载资讯阅读、热点研判、媒体字幕处理、文档与数据轻工具。

## 状态

项目按可运行垂直切片增量交付。权威需求、架构和任务状态分别位于：

- `docs/SPECIFICATION.md`
- `docs/ARCHITECTURE.md`
- `docs/IMPLEMENTATION_PLAN.md`
- `docs/THREAT_MODEL.md`
- `docs/PROJECT_GUIDE.md` 第 10 节（当前已完成、未完成、未配置和制品状态）

> 当前仓库是 `0.1.0` 预览基线。`Release\LenxTool_Setup.exe` 是本轮修复前的旧制品；在完成正式发布配置并重新构建前，请勿用它验收最新源码。

### 当前开发边界

状态核对日期：2026-08-17。当前源码已经具备资讯刷新与缓存、13 个可多选筛选的分组热点平台、Groq/DeepSeek Key 加密保存、AI 解读/翻译、Groq/本地 Whisper 转写、完整字幕翻译/导出闭环、JSON 双栏结构 Diff、七类统一搜索、数据库备份/安全清理和签名更新检查等预览能力。

字幕 Gate 0、RSS P0、P1、P2-01～P2-14、P2-16～P2-23、Independent-01 和统一发现 DISC-01～DISC-06、UX-03 已完成。P1 已交付私人已读/收藏/标签/备注/进度、离线图片与受控全文、AI 摘要/翻译及本地自动处理、管理员受限规则与模拟、确定性动作账本、Feed 媒体投递、应用内通知、七类统一搜索和 180 天安全清理；P2 已交付五类内容视图、智能视图、统一导出契约与集成安全策略、持久化导出队列、本地 Markdown、Obsidian、Eagle、Zotero 个人库、Readwise Reader、Readeck、Outline、qBittorrent、受控 Webhook 和隐私安全的 Windows 系统通知。P2-16～P2-19 使用独立本机设置卡、DPAPI 凭据代际和 Worker/D1 schema v2 策略；执行时重新校验 ACTIVE 策略与全部 DNS 地址，默认禁代理、跳转、Cookie 和自动解压。P2-20～P2-22 提供本地计划、日/周摘要和封闭通知；筛选、报告和通知设置不上传 Worker/D1。P2-15 Cubox 已取消。P2-23 已按 [`Accepted ADR-004`](docs/decisions/ADR-004-server-email-digest-gate.md) 选择 A，以“不实施、不收邮箱、不扩权”关闭。2026-08-17 已完成生产 D1 0001～0011、Worker v2、随机 `TOKEN_SECRET`、公网健康、首管理员和 schema v2/旧客户端策略契约；同日用 Release Desktop 和 qBittorrent 5.2.3/WebAPI 2.15.1 完成健康、magnet、受控 `.torrent`、幂等重放、精确清理及策略撤销的真实 canary。生产策略已恢复为版本 4 的九类全禁用基线，本机 target 为 marker 0、LenxTool DPAPI 测试凭据已删除、provider 进程已停止。qBittorrent 的真实公网 `.torrent` 获取与 200/202/失败状态矩阵、其他三个 provider、正式签名安装包和跨机发布矩阵仍未完成。完整状态、运行配置和发布阻塞项以 [`docs/PROJECT_GUIDE.md` 第 10 节](docs/PROJECT_GUIDE.md#10-当前版本边界与交付状态)为准。

字幕闭环之后的主路线为 **管理员策展 RSS**：只有管理员能维护共享 RSS/Atom 目录、AI 策略和自动化规则，普通用户只能同步 ACTIVE 配置并在本机消费。P0/P1 已验收，P2-01～P2-14、P2-16～P2-23 与统一发现 DISC-01～DISC-06、UX-03 已完成；P2-15 Cubox 已取消，P2-D 的外部真实连通仍单独开放。P2-23 的 [`Accepted ADR-004`](docs/decisions/ADR-004-server-email-digest-gate.md) 明确不实施服务端邮件摘要、不收集邮箱、所有 Feed/AI 内容云端保留 0 天，也不增加云端文章表、邮箱字段或邮件发送代码。另有“洛克王国世界每日清体力自动化”作为独立候选调研项登记，尚未批准选型或进入实现，不属于当前 RSS 路线。完整 RSS 任务见 [`docs/plans/RSS_MASTER_ROADMAP.md`](docs/plans/RSS_MASTER_ROADMAP.md)，独立候选边界见 [`docs/plans/GAME_AUTOMATION_BACKLOG.md`](docs/plans/GAME_AUTOMATION_BACKLOG.md)。

## 下一步：P2-D 受控验收与正式发布

源码和假 HTTP 契约已经完成，下一步不是继续扩展 provider，而是按 [`P2-D 执行手册`](docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md#p2-d-执行手册)完成真实环境验收，再制作签名发布包：

1. 准备剩余受控输入：Readeck 实例、Outline 实例与 collection、Webhook 接收端，以及 qBittorrent 可公开 HTTPS 获取的测试 `.torrent`/状态观测。测试账号、API key 和 token 只通过密码管理器或运行时安全输入提供，不写入仓库、Issue、日志或聊天。
2. 生产 D1、Worker v2、首管理员、强 ETag、schema v2/旧客户端矩阵和 Desktop v2/qBittorrent 部分 canary 已完成；每轮继续从版本 4 的九类全禁用基线发布单 provider 最小权限，结束后立即回滚并清理 marker/测试对象。
3. 下一轮优先补 qBittorrent 的真实公网 HTTPS `.torrent` 获取和可观测 200/202/失败状态，再执行 Readeck 标签查找/创建/重放、Outline collection 与草稿回执、Webhook OPTIONS/HMAC/幂等 ack。每个用例记录脱敏结果、队列终态和第三方实际对象，不记录秘密或正文。
4. 同时单独修复当前全仓 `dotnet format --verify-no-changes` 的历史编码/空白/导入顺序基线；P2-D 与 formatter 均关闭后，才按 [`构建、签名与发布指南`](docs/RELEASE_GUIDE.md)重新生成并验收正式制品。
5. 发布前再次运行 [`docs/TEST_REPORT.md`](docs/TEST_REPORT.md) 中的完整门禁，确认没有真实凭据、私钥、数据库或旧制品进入提交；最后创建 GitHub Release，不能把现有旧 `Release\LenxTool_Setup.exe` 当作本轮制品。

当前仍开放的是 qBittorrent 剩余状态矩阵、Readeck/Outline/Webhook 真实外联、formatter 基线、Provider key、签名证书/离线更新私钥和跨物理机升级矩阵；这些输入确认前，不宣称端到端生产验收完成。

## 常用命令

```powershell
dotnet restore LenxTool.slnx
dotnet build LenxTool.slnx -c Release --no-restore
dotnet test LenxTool.slnx -c Release --no-build
dotnet run --project src/LenxTool.App/LenxTool.App.csproj
```

发布、安装包和便携包由 `scripts/Build-Release.ps1` 统一生成。
Lenx Tools 是 .NET 10 + WPF 的 Windows 10/11 x64 本地优先桌面应用。本仓库是全新重构项目；`L:\RealTimeTranslator` 仅用于功能分析，未被修改。

## 文档入口

- [开发约定（含中文注释规范）](CONTRIBUTING.md)
- [完整项目文档](docs/PROJECT_GUIDE.md)
- [用户使用说明](docs/USER_GUIDE.md)
- [架构说明](docs/ARCHITECTURE.md)
- [产品规格](docs/SPECIFICATION.md)
- [实施清单](docs/IMPLEMENTATION_PLAN.md)
- [威胁模型](docs/THREAT_MODEL.md)
- [Worker 部署](docs/WORKER_DEPLOYMENT.md)
- [构建与发布](docs/RELEASE_GUIDE.md)
- [测试报告](docs/TEST_REPORT.md)
- [管理员策展 RSS 总路线图](docs/plans/RSS_MASTER_ROADMAP.md)
- [统一发现与原生控件视觉计划](docs/plans/RSS_DISCOVERY_AND_CONTROL_UX.md)
- [P0 管理员订阅计划](docs/plans/RSS_P0_ADMIN_CATALOG.md)
- [P1 阅读智能计划](docs/plans/RSS_P1_READING_INTELLIGENCE.md)
- [P2 视图与集成计划](docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md)
- [现有未完成项对齐计划](docs/plans/EXISTING_BACKLOG_ALIGNMENT.md)
- [ADR-001：管理员策展 RSS](docs/decisions/ADR-001-admin-curated-rss.md)

## 快速验证

```powershell
dotnet build LenxTool.slnx -c Release
dotnet test LenxTool.slnx -c Release
```

正式发布脚本完成后，最终制品位于 `Release`。安装包为 `LenxTool_Setup.exe`，便携版为 `LenxTool_Portable_win-x64.zip`；生成时间必须晚于对应源码修改时间。
