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

状态核对日期：2026-07-21。当前源码已经具备资讯刷新与缓存、13 个可多选筛选的分组热点平台、可用的 Groq/DeepSeek Key 加密保存、AI 解读/趋势报告、Groq/本地 Whisper 转写、原文 SRT 导出、文档与数据工具、全局搜索、数据库备份恢复和签名更新检查等预览能力。

下一里程碑是完成 **SRT 导入、字幕批量翻译和译文/双语 SRT/TXT 导出**。当前主要未完成项还包括：首页接入真实资讯与任务数据，资讯收藏/标签/备注，封面图片离线缓存，JSON 双栏 Diff 界面，字幕片段与模型用量历史，以及客户端共享账号/额度链路。完整状态、运行配置和发布阻塞项以 [`docs/PROJECT_GUIDE.md` 第 10 节](docs/PROJECT_GUIDE.md#10-当前版本边界与交付状态)为准。

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

## 快速验证

```powershell
dotnet build LenxTools.slnx -c Release
dotnet test LenxTools.slnx -c Release
```

正式发布脚本完成后，最终制品位于 `Release`。安装包为 `LenxTool_Setup.exe`，便携版为 `LenxTool_Portable_win-x64.zip`；生成时间必须晚于对应源码修改时间。
