# 现有未完成项与 RSS 路线对齐计划

状态：实施中（Gate0-01～02 已完成；Gate0-03～06 尚未实现）
最后核对：2026-07-21

## 1. 目的

本文件把 `PROJECT_GUIDE.md` 第 10 节和当前代码中的未完成项映射到新的 P0/P1/P2 路线。RSS 扩展不能覆盖或重复已有实现，也不能让当前字幕里程碑长期停在半成品状态。

## 2. 对齐矩阵

| 现有未完成项 | 当前事实 | 处理方式 | 归属 |
|---|---|---|---|
| SRT 导入、批量翻译、双语/译文导出 | 严格 `SrtCodec`、事务仓储和已有 SRT 导入已完成；转写入库、翻译与导出交互未接线 | 先收口当前里程碑，再让 Feed 媒体复用它 | Gate 0 |
| 首页演示数据 | `DashboardViewModel` 仍是固定 2026-07-19 示例 | P0 时间线稳定后接真实仓储 | P0-16 |
| 收藏、标签、备注 | SQLite 表已建，仓储/UI 缺失 | 扩展为 Feed 通用私人阅读状态 | P1-01～P1-03 |
| 正文图片离线缓存 | 现有下载器只做在线加载、类型和 12 MiB 限制 | 建立资源索引、缓存预算和清理 | P1-04 |
| JSON 双栏 Diff UI | Core 算法已有，UI 未接 | 独立完成，不与 RSS 模型耦合 | Independent-01 |
| 字幕片段/模型用量历史 | 表、事务仓储和已有 SRT 写入已完成；转写/翻译写入、查询/UI 和模型用量展示不完整 | Gate 0 持久化，P1 统一搜索 | Gate 0 / P1-14 |
| 桌面账号、额度、管理端 | Worker 路由已有，客户端无接线，测试仅 1 项 | 作为 RSS 管理员授权前置基础 | P0-01～P0-05 |
| Worker 完整自动化验收与部署 | D1/路由骨架已有，生产未配置 | P0 先补契约与安全测试；部署仍需外部配置 | P0 检查点 |
| 更新/安装正式制品 | 旧 Setup 不含最新源码，缺 Inno Setup/签名私钥 | 各阶段先产开发验收包，正式发布走现有门禁 | Release Gate |

## 3. Gate 0：先完成字幕交付闭环

参考：LenxTool 当前 `MediaWorkbenchViewModel`、`SrtCodec`、`subtitle_segments`，以及 Folo 的媒体转写入口仅用于验证“Feed 附件应复用媒体流水线”，不复制实现。

### Gate0-01：字幕片段仓储

**目标：** 为已有 `subtitle_segments` 表补齐事务化读写接口。

**依赖：** 无。
**预计范围：** M。
**主要文件：** `IMediaJobRepository.cs` 或新 `ISubtitleRepository.cs`、`MediaJobRepository.cs`、数据库集成测试。

**验收：**

- [x] 一次事务替换指定任务的片段，序号与时间轴唯一。
- [x] 可读取原文、译文和置信指标，重启后数据不丢失。
- [x] 失败写入完整回滚，不留下半批片段。

**验证：** 2026-07-21 已通过临时 SQLite 原序号/时间轴唯一性、重开往返、覆盖写入、触发器强制中途失败回滚和 schema v1 升级保留测试；Release 构建 0 警告/0 错误，全部 .NET 测试 91/91 通过。

### Gate0-02：SRT 导入垂直切片

**目标：** 用户可选择已有 SRT，解析后创建可恢复的字幕任务。

**依赖：** Gate0-01。
**预计范围：** M。
**主要文件：** `IDesktopFileDialogService`、`DesktopFileDialogService`、`MediaWorkbenchViewModel`、对应 App 测试。

**验收：**

- [x] 支持 UTF-8/常见 BOM，错误行给出可操作提示。
- [x] 导入后片段立即持久化，关闭重开仍可选择。
- [x] 中文、空格和长路径可用。

**验证：** 2026-07-21 已通过黄金 SRT、UTF-8 BOM、原序号/多行正文、畸形第二块错误行、任务与片段同事务创建/回滚、中文/空格/超过 260 字符路径、ViewModel 命令失败恢复测试；真实 WPF 文件选择导入 2 个片段后显示 `ImportedSrt / Completed`，关闭并重启仍恢复。Release 构建 0 警告/0 错误，全部 .NET 测试 96/96 通过。

### Gate0-03：字幕翻译服务契约

**目标：** 定义可取消、可重试、保持序号/时间轴的批量翻译边界。

**依赖：** Gate0-01。
**预计范围：** S。
**主要文件：** 新 `ISubtitleTranslator.cs`、翻译请求/结果模型、Core 测试。

**验收：**

- [ ] 契约只修改 `TranslatedText`，不改原文和时间轴。
- [ ] 包含批大小、目标语言、模型和 token 用量。
- [ ] 定义幂等恢复位置和结构化错误。

**验证：** 纯模型/契约测试。

### Gate0-04：DeepSeek 批量翻译实现

**目标：** 复用现有 DeepSeek 请求和 DPAPI Key，批量翻译字幕。

**依赖：** Gate0-03。
**预计范围：** M。
**主要文件：** 新 `DeepSeekSubtitleTranslator.cs`、DI 注册、Infrastructure 测试。

**验收：**

- [ ] 批次有长度上限并保留输入序号，模型输出不能重排时间轴。
- [ ] 取消、429、超时、部分批次失败可恢复。
- [ ] 记录模型、请求数和 token，不记录 Key 或完整字幕到日志。

**验证：** 假 HTTP 覆盖成功、乱序、缺项、429、取消和恢复。

### Gate0-05：翻译与导出交互

**目标：** 在媒体工作台选择字幕任务、目标语言并导出译文/双语 SRT/TXT。

**依赖：** Gate0-02、Gate0-04。
**预计范围：** M。
**主要文件：** `MediaWorkbenchViewModel.cs`、`MainWindow.xaml`、App ViewModel 测试、布局测试。

**验收：**

- [ ] 翻译进度、取消、重试和恢复状态可见。
- [ ] 原文 SRT、译文 SRT、双语 SRT、TXT 均可选择导出。
- [ ] 导出后可打开目录，重启后可重新导出而无需再次调用模型。

**验证：** ViewModel 测试、编码黄金文件、真实 WPF 手测。

### Gate0-06：字幕历史和模型用量

**目标：** 历史页可查看片段、翻译状态和模型/token 用量。

**依赖：** Gate0-05。
**预计范围：** M。
**主要文件：** `HistoryViewModel.cs`、`MainWindow.xaml`、仓储查询、App 测试。

**验收：**

- [ ] 可从媒体任务进入字幕详情并查看原文/译文。
- [ ] 可显示引擎、模型、请求数/token 和脱敏错误。
- [ ] 历史数据可重新导出。

**验证：** 查询测试、ViewModel 测试、WPF 手测。

### Gate 0 检查点

- [ ] 当前 `PROJECT_GUIDE.md` 10.2 的全部字幕验收完成。
- [ ] Release 构建与全部 .NET 测试通过。
- [ ] 更新 `PROJECT_GUIDE.md`、`USER_GUIDE.md` 和 `TEST_REPORT.md` 后再进入 P0 主体。

## 4. 独立旧欠账

### Independent-01：JSON 双栏 Diff 界面

**目标：** 复用 `JsonToolkit` 现有结构 Diff 算法补齐双输入、差异列表和交换操作。

**依赖：** 无；不得与 RSS 数据模型同批修改。
**预计范围：** M。
**主要文件：** `ToolsViewModel.cs`、`MainWindow.xaml`、`ToolboxTests.cs`、App ViewModel 测试。

**验收：**

- [ ] 左右 JSON 分别校验，错误定位不覆盖另一栏。
- [ ] 显示新增、删除、修改路径和值，支持交换左右输入。
- [ ] 大输入有上限和可取消策略，界面不冻结。

**验证：** 算法回归、ViewModel 测试、真实 WPF 手测。

## 5. 兼容和发布要求

- RSS P0 的 schema v3+ 迁移必须从当前 schema v2 原位升级，保留早报、AI 报告、媒体任务和设置。
- 收藏/标签现有空表可以演进，但不得删除后重建整个数据库。
- 当前旧 Setup 不能用于验证新路线；每阶段只声称已验证实际重新生成的制品。
- 现有 91 个 .NET 测试和 Worker typecheck/test 是回归基线，不是 P0/P1/P2 的充分验收。
