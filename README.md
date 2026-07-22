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

状态核对日期：2026-07-22。当前源码已经具备资讯刷新与缓存、13 个可多选筛选的分组热点平台、可用的 Groq/DeepSeek Key 加密保存、AI 解读/趋势报告、Groq/本地 Whisper 转写、完整字幕翻译/导出闭环、文档与数据工具、全局搜索、数据库备份恢复和签名更新检查等预览能力。

字幕 Gate 0 已完成。P0-01～P0-14 已完成 API 契约、Worker 身份/目录/RBAC、桌面会话与同步、Feed schema v5、安全发现/解析、条件调度、条目事务仓储和统一 FTS；当前下一里程碑是补齐 **P0-C 至少 20 个 RSS/Atom fixture** 检查点，随后实现 P0-15 管理员订阅管理页。其他主要未完成项包括：首页接入真实数据，资讯收藏/标签/备注，封面图片离线缓存，JSON 双栏 Diff，以及真正的管理员目录管理界面。完整状态、运行配置和发布阻塞项以 [`docs/PROJECT_GUIDE.md` 第 10 节](docs/PROJECT_GUIDE.md#10-当前版本边界与交付状态)为准。

字幕闭环之后的主路线为 **管理员策展 RSS**：只有管理员能维护共享 RSS/Atom 目录，普通用户只能同步和阅读。P0/P1/P2 的完整任务、现有欠账对齐、参考项目和许可证边界见 [`docs/plans/RSS_MASTER_ROADMAP.md`](docs/plans/RSS_MASTER_ROADMAP.md)。当前 P0-01～P0-14 已实现，其余能力仍按计划推进。

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
