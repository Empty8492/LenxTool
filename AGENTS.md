# AGENTS.md

## LenxTool 工作边界

- 这是 .NET 10 + WPF 的 Windows 10/11 x64 本地优先桌面应用。当前交付状态以 `docs/PROJECT_GUIDE.md` 第 10 节为主要文档入口，并结合当前代码、测试、配置和专项计划重新核验。
- 继续开发前先检查分支与 worktree，并对照 `README.md`、`docs/PROJECT_GUIDE.md`、相关专项计划和 `docs/TEST_REPORT.md`；不得按旧会话记忆直接选择下一里程碑。
- 保留用户已有和相邻任务改动。Git 出现 dubious ownership 时只对单次命令使用 `git -c safe.directory=D:/Projects/LenxTool ...`，不得修改全局 Git 配置。
- 先运行聚焦测试，再运行相关 Release 门禁；WPF 运行时测试必须串行。文档只记录本轮真实执行的验证，不把历史测试数字当成本轮结果。

## 项目知识沉淀

- 项目知识目录为 `cairn/`：`LOG.md` 保存倒序进展和指针，`ROADMAP.md` 保存目标与当前焦点，主题笔记保存项目当前真相，`Cited.md` 只保存实际采用的外部知识指针。
- 每个实质性任务开始前，用模块名、类名、API、表名、错误码和行为关键词搜索 `cairn/*.md`，读取命中的当前结论、防复发约束和待验证项。需要项目状态时再读 `cairn/ROADMAP.md` 与最近 LOG。
- 实质性任务结束前自动执行知识复盘。主 Agent 汇总自身和所有审计、评审、测试及其他子 Agent 的发现，回到代码、测试、配置、运行结果或用户确认核验，去重后更新 LOG 与相关主题。没有长期价值时不创建内容。
- 子 Agent 默认只向主 Agent 返回“发现、证据、影响、建议、确认状态”，不并发编辑项目知识文件；只有主 Agent 分配独占文件时例外。
- 对故障或审计发现记录症状/触发条件、根因、误导假设、防复发约束和回归验证。未确认发现只能进入“待验证”，不能写成当前结论或规则。
- 高价值且已确认的结论若可能因任务中断丢失，应在验证后立即做项目层检查点，不必等到最终答复。
- 实质性进展后更新 LOG；稳定结论进入主题笔记。不要把长结论、临时日志、源码或工程资产复制进 LOG。
- 修正旧判断时明确记录原因和新结论，并在 LOG 留修订指针；不能静默覆盖。
- 可能依赖通用经验的工作先按 Codex Home 下的 `knowledge-capture.json` 和 `.cairn/config.yaml` profile 解析并搜索 Obsidian；配置缺失时报告而不猜测路径。只有真正影响产出的笔记才写入 `cairn/Cited.md`。
- 任务后解析 profile 的 `obsidianWriteMode`：`automatic_after_substantive_work` 自动毕业通过门槛且有实质新增的知识，`explicit_only` 等待用户明确授权，`disabled` 禁止写入。自动模式仍不得写入普通问答、未验证推测、重复结论或秘密值。
