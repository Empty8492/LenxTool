# 测试报告

测试日期：2026-07-21（Asia/Shanghai）
版本：0.1.0  
配置：Release / win-x64 / .NET SDK 10.0.302

## 自动化结果

| 测试组 | 结果 |
|---|---:|
| LenxTool.Core.Tests | 30 passed |
| LenxTool.Infrastructure.Tests | 25 passed |
| LenxTool.App.Tests | 41 passed |
| Cloudflare Worker Vitest | 1 passed |
| Worker TypeScript strict typecheck | passed |
| .NET build warnings | 0 |
| NuGet vulnerable packages | 0 detected |

本轮执行 `dotnet build LenxTools.slnx -c Release`，结果为 0 警告、0 错误；执行 `dotnet test LenxTools.slnx -c Release`，结果为 Core 30、Infrastructure 25、App 41，共 96/96 通过且无跳过。

覆盖点包括：统一 HTTP 错误、Groq 429 与 Retry-After、DeepSeek 当前模型请求与 token 用量解析、AI 报告生成状态和 SQLite/FTS5 持久化、语义版本、签名篡改、严格 SRT 解析/导出、UTF-8 BOM、原序号保留、畸形块错误行定位、已有 SRT 任务与片段原子创建、字幕片段按任务事务替换与按原序号读取、字幕序号/时间轴唯一性、字幕原文/译文/置信指标重开往返、覆盖写入、批次中途失败回滚和 schema v1 升级保留、重叠合并、音频分片计划、JSON/编码/文本工具、SQLite schema/FTS/损坏、早报/热点/AI 报告统一全文搜索、包含未 checkpoint WAL 提交的一致性迁移备份、RSS 富内容持久化、HTML/Markdown 正文配图顺序与安全 URL 解析、受大小限制的图片下载、标题/列表/链接解析与朗读标记清理、NewsNow 13 来源目录与响应解析、恶意子域名拒绝、按平台缓存快照替换、热点平台分组、多选来源过滤、全选恢复与安全原文命令、PasswordBox TwoWay Binding 保留、Key 保存命令状态与 DPAPI 配置反馈、资讯页统一控件样式、无缺边歧义的标签指示器、单一整页滚动、回顶渐显和缓动布局、设置持久化、DPAPI 与脱敏、中文/空格/超过 260 字符的路径、媒体 Queued/Running/Completed/Failed 状态与进度持久化、重启恢复、失败计数和重试、资讯默认当天/日期切换/当天缺失回退、导航和 Ctrl+K 状态。

真实 DeepSeek 连通性测试使用临时进程环境变量执行，未写入仓库或日志。`deepseek-v4-flash` 请求成功，返回 46 total tokens；测试凭据值未保存到项目文件。

## 冒烟与制品测试

- 从当时的 Release 编译结果启动：主窗口可见，进程 Responding=True。
- `LenxTool_Setup.exe` 静默安装到包含中文与空格的测试目录：ExitCode=0。
- 启动已安装应用：Responding=True，标题正确。
- 静默卸载：ExitCode=0，测试安装目录已移除。
- 安装器包含 WebView2 Evergreen Bootstrapper。
- 自包含发布无需系统 .NET Runtime。
- 更新清单 ECDSA 签名与安装包 hash 签名均由发布脚本使用嵌入公钥反向验证。

以上安装器冒烟记录对应 2026-07-20 01:44 的旧制品。本轮源码已更新，但当前机器缺少 Inno Setup 6，且本轮未提供仓库外离线私钥路径，因此尚未覆盖生成签名安装器；不得将旧 `LenxTool_Setup.exe` 视为包含本轮修复。

本轮已生成未签名的开发验收便携包 `artifacts\LenxTool_Portable_0.1.0-preview-rich-reader.zip`（74,359,890 bytes，SHA-256 `7DB438205065AE3BC58C0FFDEFD3CBF2EF9CA9F97749D1D010F97C3BF1DE2CA6`）。它包含资讯分段页签、RSS 富内容迁移和原生富文本早报阅读器，但不能替代正式签名安装包。

## 本次已验证的异常路径

- 完全离线的资讯缓存回退设计与网络错误映射。
- 400、401、403、429、5xx、超时和网络中断的不同错误对象。
- SQLite 损坏、迁移备份和恢复完整性检查。
- 字幕片段全量替换在第二条写入失败时完整回滚，旧批次不被删除，也不留下半批新片段。
- 已有 SRT 任一片段无效时不创建媒体任务；任务写入后片段校验失败时同一 SQLite 事务完整回滚。
- 任务取消状态持久化与异常退出任务恢复。
- 中文用户名、中文文件名和空格路径的数据库及媒体导出。
- 安装、启动、卸载和用户数据目录隔离。
- 真实 WPF 运行检查：2026-07-21 在线刷新成功展示 13 个热点平台；热点按两列平台卡片和平台内名次显示，可点击条目均暴露 Invoke 行为。鼠标位于热点标题中央连续滚轮后，唯一外层 ScrollViewer 从 0% 移到 1.85%，未被卡片吞掉。滚到 35% 后回顶按钮启用并渐显；点击后的滚动位置取样为 35% → 15.55%（90 ms）→ 2.66%（220 ms）→ 0%（520 ms），确认不是瞬间跳转。右侧滚动条贴合内容区最右边；页内标题、按钮和标签栏会随内容移出视图。
- 设置页真实输入无效测试 Key 后，“加密保存”从空输入禁用变为可用，100 ms 内状态更新为“Groq：已配置 · DeepSeek：已配置 · 已加密保存”；随后通过“清除”恢复为双未配置，未保留测试凭据。
- 热点页真实呈现 13 个带 TogglePattern 的来源筛选胶囊；取消“知乎”后即时显示 12/13，知乎卡片隐藏而筛选入口保留，“全选”启用并可恢复 13/13。选中标签使用完整背景和底部强调线，不再依赖容易产生缺边错觉的四周描边。
- Gate0-02 真实 WPF 验收：通过“导入媒体 / SRT”文件选择器导入中文和空格路径下的黄金 SRT，界面显示“已导入 1 个 SRT，共 2 个片段”、`ImportedSrt / Completed`；关闭并重启应用后，该任务仍从本地 SQLite 恢复。

## 需要外部环境的验收

以下测试需要真实账号、模型或较长运行时间，不在无凭据自动化环境中执行：真实 Groq/DeepSeek 请求、Cloudflare 生产 D1 并发压测、超长视频全程转写、各代 CPU 的本地大型模型性能、真实 GitHub Release 更新覆盖、已签 Authenticode 的 SmartScreen 声誉、Windows 10/11 多台物理机 100%～200% DPI 矩阵。对应测试步骤已写入规格与发布指南，生产发布前必须执行。
