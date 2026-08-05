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

状态核对日期：2026-08-05。当前源码已经具备资讯刷新与缓存、13 个可多选筛选的分组热点平台、Groq/DeepSeek Key 加密保存、AI 解读/翻译、Groq/本地 Whisper 转写、完整字幕翻译/导出闭环、七类统一搜索、数据库备份/安全清理和签名更新检查等预览能力。

字幕 Gate 0、RSS P0、P1、P2-01～P2-14、P2-20 和统一发现 DISC-01～DISC-06、UX-03 已完成。P1 已交付私人已读/收藏/标签/备注/进度、离线图片与受控全文、AI 摘要/翻译及本地自动处理、管理员受限规则与模拟、确定性动作账本、Feed 媒体投递、应用内通知、七类统一搜索和 180 天安全清理；P2 已交付五类内容视图、智能视图、统一导出契约与集成安全策略、持久化导出队列、本地 Markdown、Obsidian、Eagle、Zotero 个人库与 Readwise Reader 导出。Readwise 固定官方 Reader API 与 DPAPI token，只发送用户可预览的有界纯文本摘要、来源和标签；精确同 URL 重放不会创建第二条，但官方重存会置顶并显示绿色标记，不同追踪 URL 仍可能重复。管理员订阅管理现已提供统一发现、真实本地预览、重复项识别和显式确认发布，并通过最终性能、安全、权限、离线与可访问性检查点。P2-15 Cubox 已取消实施；P2-20 现已完成本地时区重复计算、schema v22 计划定义、schema v23 唯一窗口与 RunOnce/Skip 恢复，以及只领取已注册幂等处理器的后台执行、租约心跳、异常释放、生产 DI 和计划代际取消。当前没有注册具体计划处理器，后台会安全空转；每日/每周摘要处理器及其管理 UI 属于下一项 P2-21，因此定时摘要仍不是可用产品功能。P2-16～P2-19 仍待逐项选择；受控真实 Zotero/Eagle/Readwise 连通、JSON 双栏 Diff、生产 Worker/D1、正式签名安装包和发布矩阵仍未完成。完整状态、运行配置和发布阻塞项以 [`docs/PROJECT_GUIDE.md` 第 10 节](docs/PROJECT_GUIDE.md#10-当前版本边界与交付状态)为准。

字幕闭环之后的主路线为 **管理员策展 RSS**：只有管理员能维护共享 RSS/Atom 目录、AI 策略和自动化规则，普通用户只能同步 ACTIVE 配置并在本机消费。P0/P1 已验收，P2-01～P2-14、P2-20 与统一发现 DISC-01～DISC-06、UX-03 已完成；P2-15 Cubox 已取消，下一垂直切片是 P2-21 每日/每周本地摘要。另有“洛克王国世界每日清体力自动化”作为独立候选调研项登记，尚未批准选型或进入实现，不属于当前 RSS 路线。完整 RSS 任务见 [`docs/plans/RSS_MASTER_ROADMAP.md`](docs/plans/RSS_MASTER_ROADMAP.md)，独立候选边界见 [`docs/plans/GAME_AUTOMATION_BACKLOG.md`](docs/plans/GAME_AUTOMATION_BACKLOG.md)。

## 常用命令

```powershell
dotnet restore LenxTools.slnx
dotnet build LenxTools.slnx -c Release --no-restore
dotnet test LenxTools.slnx -c Release --no-build
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
dotnet build LenxTools.slnx -c Release
dotnet test LenxTools.slnx -c Release
```

正式发布脚本完成后，最终制品位于 `Release`。安装包为 `LenxTool_Setup.exe`，便携版为 `LenxTool_Portable_win-x64.zip`；生成时间必须晚于对应源码修改时间。
