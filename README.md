# Lenx 工具箱

Lenx 工具箱是面向 Windows 10/11 x64 的本地优先桌面效率应用，统一承载资讯阅读、热点研判、媒体字幕处理、文档与数据轻工具。

本仓库是对 `L:\RealTimeTranslator` 的独立重构。旧项目仅作为只读功能参考，不是本仓库的代码基础，也不会被本项目的构建、测试或发布脚本修改。

## 状态

项目按可运行垂直切片增量交付。权威需求、架构和任务状态分别位于：

- `docs/SPECIFICATION.md`
- `docs/ARCHITECTURE.md`
- `docs/IMPLEMENTATION_PLAN.md`
- `docs/THREAT_MODEL.md`
- `docs/PROJECT_GUIDE.md` 第 10 节（当前已完成、未完成、未配置和制品状态）

> 当前仓库是 `0.1.0` 预览基线。`Release\LenxTool_Setup.exe` 是本轮修复前的旧制品；在完成正式发布配置并重新构建前，请勿用它验收最新源码。

## 常用命令

```powershell
dotnet restore LenxTool.slnx
dotnet build LenxTool.slnx -c Release --no-restore
dotnet test LenxTool.slnx -c Release --no-build
dotnet run --project src/LenxTool.App/LenxTool.App.csproj
```

发布、安装包和便携包由 `scripts/Build-Release.ps1` 统一生成。
Lenx 工具箱是 .NET 10 + WPF 的 Windows 10/11 x64 本地优先桌面应用。本仓库是全新重构项目；`L:\RealTimeTranslator` 仅用于功能分析，未被修改。

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
dotnet build LenxTool.slnx -c Release
dotnet test LenxTool.slnx -c Release
```

正式发布脚本完成后，最终制品位于 `Release`。安装包为 `LenxTool_Setup.exe`，便携版为 `LenxTool_Portable_win-x64.zip`；生成时间必须晚于对应源码修改时间。
