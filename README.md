# Lenx Tools

Lenx Tools 是面向 Windows 10/11 x64 的本地优先桌面效率应用，统一承载资讯阅读、热点研判、媒体字幕处理、文档与数据轻工具。

本仓库是对 `L:\RealTimeTranslator` 的独立重构。旧项目仅作为只读功能参考，不是本仓库的代码基础，也不会被本项目的构建、测试或发布脚本修改。

## 状态

项目按可运行垂直切片增量交付。权威需求、架构和任务状态分别位于：

- `docs/SPECIFICATION.md`
- `docs/ARCHITECTURE.md`
- `docs/IMPLEMENTATION_PLAN.md`
- `docs/THREAT_MODEL.md`
- `docs/PROJECT_GUIDE.md` 第 10 节（当前已完成、未完成、未配置和制品状态）

> 当前仓库是 `0.1.0` 预览基线。`Release\LenxTool_Setup.exe` 是本轮修复前的旧制品；在完成正式发布配置并重新构建前，请勿用它验收最新源码。

### 当前开发边界

状态核对日期：2026-07-27。当前源码已经具备资讯刷新与缓存、13 个可多选筛选的分组热点平台、Groq/DeepSeek Key 加密保存、AI 解读/翻译、Groq/本地 Whisper 转写、完整字幕翻译/导出闭环、七类统一搜索、数据库备份/安全清理和签名更新检查等预览能力。

字幕 Gate 0、RSS P0 和 P1 已完成。P1 已交付私人已读/收藏/标签/备注/进度、离线图片与受控全文、AI 摘要/翻译及本地自动处理、管理员受限规则与模拟、确定性动作账本、Feed 媒体投递、应用内通知、七类统一搜索和 180 天安全清理。最终验收以真实 SQLite 覆盖 10,000 条 Feed、1,000 个收藏、混合媒体和离线重开，并在真实 workerd/D1 中验证管理员发布、普通用户写入 403 和内容不落 D1；Release 回归为 .NET 648/648、Worker 52/52、strict typecheck 与 0 警告构建。下一项是 P2-01；JSON 双栏 Diff、生产 Worker/D1、正式签名安装包和发布矩阵仍未完成。完整状态、运行配置和发布阻塞项以 [`docs/PROJECT_GUIDE.md` 第 10 节](docs/PROJECT_GUIDE.md#10-当前版本边界与交付状态)为准。

字幕闭环之后的主路线为 **管理员策展 RSS**：只有管理员能维护共享 RSS/Atom 目录、AI 策略和自动化规则，普通用户只能同步 ACTIVE 配置并在本机消费。P0/P1 已验收，P2 尚未开始；完整任务、现有欠账对齐、参考项目和许可证边界见 [`docs/plans/RSS_MASTER_ROADMAP.md`](docs/plans/RSS_MASTER_ROADMAP.md)。

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
