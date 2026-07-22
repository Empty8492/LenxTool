# 测试报告

测试日期：.NET / Worker 2026-07-22（Asia/Shanghai）
版本：0.1.0  
配置：Release / win-x64 / .NET SDK 10.0.302

## 自动化结果

| 测试组 | 结果 |
|---|---:|
| LenxTool.Core.Tests | 37 passed |
| LenxTool.Infrastructure.Tests | 49 passed |
| LenxTool.App.Tests | 57 passed |
| Cloudflare Worker Vitest | 34 passed |
| Worker TypeScript strict typecheck | passed |
| .NET build warnings | 0 |
| NuGet vulnerable packages | 0 detected |

本轮执行单节点、禁用共享编译器的 `dotnet build LenxTools.slnx -c Release`，结果为 0 警告、0 错误；执行 `dotnet test LenxTools.slnx -c Release`，结果为 Core 37、Infrastructure 49、App 57，共 143/143 通过且无跳过。

P0-08 覆盖 4 项 SQLite 集成场景并同步既有 schema 断言：新建库创建目录状态、分类、Feed、抓取状态、条目、索引和搜索映射；真实 schema v2 哨兵数据经 v3/v4 原位升级后仍可由现有仓储读取；迁移对象冲突时完整回滚且版本停留在 v3；相同外部 ID、URL 和内容哈希可存在于不同 Feed，同一 Feed 内重复外部 ID 被约束拒绝。既有包含未 checkpoint WAL 提交的一致性备份测试继续通过。

P0-09 新增 5 项 SQLite 仓储集成测试：覆盖新库空 ACTIVE 状态与 ALL 不可伪造、完整 ALL 快照往返及 ACTIVE 投影、相同分类排序下按名称/ID 的契约顺序、版本倒退拒绝、中途插入失败后整批回滚、空目录替换，以及删除目录 Feed 后继续保留本地文章。读取状态、分类和 Feed 使用同一读事务，避免观察到混合版本。

桌面账号新增 8 项 Infrastructure 测试和 7 项 App 测试：覆盖登录、refresh token 恢复、`/v1/me`、并发 401 单次刷新、每请求最多重放一次、失效清理、离线退出、DPAPI 删除/写入失败脱敏、登录密码清空、过期与额度显示、admin/user 导航隔离、角色降级回退和 XAML 自动化名称/绑定。

Worker 使用 Cloudflare 官方 Vitest pool 在本地 workerd 中应用真实 D1 迁移并执行路由测试。身份测试覆盖单次邀请码的并发原子消费、`GET /v1/me` 公开字段与额度、登录响应、logout 撤销/幂等及原子审计、refresh 同 token 并发只有一个成功者、旧 token 重放、refresh 过期错误、禁用账号 access/refresh、过期/伪造 JWT、401/403 与 `AppError` 错误体、未知字段不消耗 token、无长度头正文的流式大小限制，以及临时 bootstrap secret 的首管理员初始化、原子审计和重复执行安全失败；共 12/12 通过。

D1 目录迁移测试明确执行“0001 → 写入旧 schema 哨兵数据 → 全部迁移”，覆盖带数据升级、重复应用、全局版本初值、保留有意义 query、活动分类规范名与 Feed 规范 URL 唯一、软删除后安全重建、分类外键与硬删除 `RESTRICT`、枚举/范围/布尔/HTTPS/单例约束、失败 batch 回滚，以及目录/幂等/审计字段隐私白名单；迁移测试 8/8 通过。

管理员目录写入新增 7 项 workerd/D1 集成测试：覆盖分类与 Feed 新增、编辑、启停、排序、移动、软删除，全部 6 个写端点的 user/匿名 403/401，NFKC 分类重复、规范 URL 重复、危险 URL、停用分类、非空分类删除、同版本并发单赢家、幂等重放/错用、版本冲突安全详情，以及操作者/目标/动作/目录版本/请求 ID 审计；Worker 合计 27/27 通过。

只读目录新增 7 项 workerd/D1 集成测试：覆盖 ACTIVE/ALL 权限隔离、软删除与停用过滤、完整公共 DTO、分类/Feed 确定排序、同版本字节级稳定序列化、强 ETag 与缓存头、`afterVersion`/`If-None-Match` 304、旧版本全量快照、超前版本 409、矛盾缓存条件和未知/重复/越界参数拒绝，以及空目录版本 0 仍返回完整快照；Worker 合计 34/34 通过。

`npm audit` 当前报告 4 个 high，均位于开发依赖 `@cloudflare/vitest-pool-workers` / `wrangler` 经 `miniflare` 引入的 `sharp < 0.35.0`，生产依赖计数为 1，且审计结果标记暂无可用修复。没有执行破坏性 `audit fix --force`；发布前需跟踪 Cloudflare 工具链升级并重新审计。

覆盖点包括：统一 HTTP 错误、Groq 429 与 Retry-After、DeepSeek 当前模型请求与 token 用量解析、DeepSeek 字幕批次成功与 token、模型乱序输出复序、缺项拒绝、取消、429 重试与实际请求数、超时退避耗尽后的精确断点、畸形超大 `Retry-After` 限界、部分批次恢复和组合根 `ISubtitleTranslator` 解析、AI 报告生成状态和 SQLite/FTS5 持久化、语义版本、签名篡改、严格 SRT 解析/四种导出、UTF-8 无 BOM、原序号保留、畸形块错误行定位、已有 SRT 任务与片段原子创建、转写片段入库、字幕批量翻译输入快照、批大小边界、取消令牌、流式批次、模型/请求数/token 用量、幂等恢复点、首批失败后的第二次断点恢复、翻译取消状态与恢复位置、历史原文/译文详情、脱敏错误和不调用模型的本地再导出、字幕与翻译用量同事务提交、schema v3 重开往返、字幕序号/时间轴唯一性、覆盖写入、批次中途失败回滚和 schema v1 升级保留、重叠合并、音频分片计划、JSON/编码/文本工具、SQLite schema/FTS/损坏、早报/热点/AI 报告统一全文搜索、包含未 checkpoint WAL 提交的一致性迁移备份、RSS 富内容持久化、HTML/Markdown 正文配图顺序与安全 URL 解析、受大小限制的图片下载、标题/列表/链接解析与朗读标记清理、NewsNow 13 来源目录与响应解析、恶意子域名拒绝、按平台缓存快照替换、热点平台分组、多选来源过滤、全选恢复与安全原文命令、PasswordBox TwoWay Binding 保留、Key 保存命令状态与 DPAPI 配置反馈、资讯页统一控件样式、无缺边歧义的标签指示器、单一整页滚动、回顶渐显和缓动布局、设置持久化、DPAPI 与脱敏、中文/空格/超过 260 字符的路径、媒体 Queued/Running/Completed/Failed 状态与进度持久化、重启恢复、失败计数和重试、资讯默认当天/日期切换/当天缺失回退、导航和 Ctrl+K 状态。

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
- Gate0-05/06 当前 WPF 构建 UI Automation 冒烟：可导航媒体工作台并找到字幕任务、目标语言、模型、翻译/取消、四种导出和打开目录控件；可导航任务历史并找到媒体任务、字幕原文/译文详情、历史导出格式和本地导出控件。进程 `Responding=True`，本项未发出真实模型请求。
- P0-07 最新 Release 程序集真实 WPF 验收：侧栏显示“云服务未配置 · 可离线使用”，普通用户导航不包含管理员入口；设置页显示共享账号卡片、未配置提示、用户名/密码自动化名称和禁用登录态。密码框可获得焦点，Tab 会跳过禁用登录按钮进入下一个输入框；本项未输入真实凭据、未发出 Worker 请求。

## 需要外部环境的验收

以下测试需要真实账号、模型或较长运行时间，不在无凭据自动化环境中执行：真实 Groq/DeepSeek 请求、Cloudflare 生产 D1 并发压测、超长视频全程转写、各代 CPU 的本地大型模型性能、真实 GitHub Release 更新覆盖、已签 Authenticode 的 SmartScreen 声誉、Windows 10/11 多台物理机 100%～200% DPI 矩阵。对应测试步骤已写入规格与发布指南，生产发布前必须执行。
