---
type: "project-knowledge"
status: "active"
summary: "LenxTool 自动维护 cairn 项目当前真相；Obsidian 采用人工审核写入，知识目录直接位于 Vault 根目录。"
tags:
  - "knowledge-capture"
  - "obsidian"
contains:
  - "decision"
  - "procedure"
  - "reference"
created: "2026-08-05"
updated: "2026-08-07"
related: []
authoring_mode: "agents"
---

# Knowledge Capture 人工审核与项目知识维护

> [!abstract] 当前结论
> LenxTool 的 `cairn/` 继续由实质性任务自动维护；当前机器 profile 为 `explicit_only`，Obsidian 只在用户审核并明确批准候选后写入。

## 形成背景

LenxTool 已初始化项目 `cairn/`，项目层用于保存高频变化的当前真相。Obsidian 曾短暂启用自动毕业和 LLM Wiki 单库目录，之后用户将长期知识写入改回人工审核，并于 2026-08-07 要求移除 LLM Wiki 专用格式。

## 当前结论

- 当前机器 `default` profile 已解析为 `obsidianWriteMode: explicit_only`。
- 全局 `AGENTS.md` 负责让所有项目在实质性任务最终答复前调用 `knowledge-capture` 做复盘；在 `explicit_only` 下只能报告候选，等待用户明确批准后才能写入 Obsidian。
- 项目层先更新 `cairn/LOG.md` 和主题当前真相；只有已验证、可跨项目复用、已抽象、可追溯且有实质新增的候选才进入 Obsidian Inbox。
- 自动模式不是后台服务，不会在 Codex 未运行时扫描项目，也不会全量同步 `cairn/`。
- 旧配置缺少 `writeMode` 时回退 `explicit_only`；`disabled` 禁止写入；未知值直接报错。
- 新建 Obsidian 笔记使用不带日期或时间前缀的稳定主题文件名；日期只保存在 YAML `created` 与 `updated` 属性中。同名冲突使用有意义的主题限定词，已有日期文件默认不自动迁移。
- 用户于 2026-08-06 明确授权历史迁移后，Vault 内 34 篇日期前缀笔记已改为稳定主题文件名；旧文件名保留为 YAML `aliases`，Vault 内引用与项目层 `vault:default/...` 指针同步更新。
- 原始文档目录与知识沉淀是两种不同模式：交接材料、手册、方案、表格等资料目录默认只是证据或档案来源，不能把每份文件自动改写成“背景、原因、实施步骤”式知识笔记。只有用户明确要求提炼，且内容通过知识毕业门槛时，才创建主题笔记。
- `D:\Obsidian\Lenx` 仍是唯一物理知识库；`00_Inbox_收集箱`、`01_Dashboard_仪表盘`、`02_Projects_项目`、`03_Research_调研与资料`、`17_Archive_归档` 和 `99_Templates_模板` 直接位于 Vault 根目录。
- LLM Wiki 专用的 `.llm-wiki/`、`wiki/`、`raw/`、`schema.md` 和 `purpose.md` 已从活动 Vault 移出，原状态保存在迁移前恢复快照中。
- 机器 profile 的 `inboxPath` 与 `indexPath` 已改回根目录相对路径；项目内 `vault:default/...` 指针同步去掉 `wiki/` 前缀。

## 历史工具记录

- LLM Wiki 的 Codex CLI 提供商按 PATH 解析 `codex`，模型快捷按钮来自应用静态建议，不是账号模型发现；`Custom` 模型 ID 会原样传给 `codex exec --model`。
- 本机曾同时存在 npm `codex-cli 0.141.0` 与写出 `0.147.0` 模型缓存的 Codex Desktop，导致旧 CLI 读取共享 `models_cache.json` 时缺少 `supports_reasoning_summaries` 并退出。npm CLI 升级到稳定版 `0.146.1` 后，按 LLM Wiki 同参数执行的最小 Chat 请求已通过。

## 原因与证据

- `resolve-vault.ps1` 当前输出确认 profile 为 `explicit_only`，Inbox 与导航均解析到 Vault 根目录下的编号目录。
- 独立测试覆盖自动、旧配置回退、禁用和非法模式，Windows PowerShell 运行通过。
- 技能通过 `skill-creator` 的 `quick_validate.py`。
- 公开仓库 `https://github.com/Empty8492/codex-knowledge-capture` 为 `PUBLIC`；无日期稳定主题名规则已发布到提交 `2a8a7e7b0e662ab0c50265cda5826cf0a442fd1a`，远端 `main` 与本地 SHA 一致。
- 历史迁移前确认 34 个目标名称均无冲突；迁移后 37 篇 Markdown 均无日期前缀，111 个双向链接无缺失或歧义，34 个旧名称别名可用于兼容历史链接。
- 迁移前快照位于 `D:\Obsidian\_Backups\Lenx-before-no-date-migration-2026-08-06.zip`，SHA-256 为 `0FA27B7CF4722738504516D981B38694BB4EBF55C5888FE0FDFB24EFB6BBA38D`。
- 用户确认 `C:\Users\admin\Desktop\document` 是原始交接文档目录后，已删除 12 篇直接由该目录生成的模板化笔记；删除后 Vault 不再包含该源路径，剩余 69 个双向链接无缺失或歧义，原始目录 7095 个文件的数量、总大小和元数据指纹保持不变。
- 单库迁移前创建完整快照 `D:\Obsidian\_Backups\Lenx-before-llm-wiki-unification-2026-08-06-153818.zip`（SHA-256 `6F9A7E9103B91F50DBD43ABAA10E2B4C0D80404BD3F429A04D66ED5A8D757AC4`）；迁移后原有 Markdown 内容按映射路径无丢失，`.obsidian` 的 10 个文件路径与哈希逐项不变，且没有新增失效双链。
- 移除 LLM Wiki 格式前创建完整恢复快照 `D:\Obsidian\_Backups\Lenx-before-remove-llm-wiki-20260807-104600`；活动 Vault 中的 LLM Wiki 外壳同时保存在该快照的 `_removed-from-active-vault` 下。
- CLI 修复后 `codex login status` 返回 ChatGPT 已登录；使用 `-a never exec --json --skip-git-repo-check --sandbox read-only --ephemeral --model <MODEL_ID> -` 的最小请求退出码为 0，证明无需额外 API Key、Embedding 或外部搜索设置即可使用基础 Chat。

## 防复发约束

- 不把“机器配置为自动”误写成“存在后台守护进程”；每次自动写入仍由 Codex 任务收尾触发。
- 不因自动模式降低证据门槛或复制聊天流水、临时日志、重复结论和秘密值。
- 不硬编码 Vault 绝对路径到技能或项目仓库；路径只保存在 Codex Home 的机器配置。
- 不因外部知识工具的项目契约改造 Obsidian 主目录；若以后再次试用 LLM Wiki 或类似工具，优先指向副本或独立试验目录，并在迁移前创建可验证恢复点。
- LLM Wiki 报模型缓存字段缺失时，先同时核对其界面显示的 CLI 路径/版本与 `%USERPROFILE%\.codex\models_cache.json` 的 `client_version`；不要先归因于模型 ID、知识库结构或隔离开关。
- 不覆盖既有 Obsidian 笔记；相同结论跳过，实质修订新建草稿并建立真实关联。
- 不用日期、时间、随机数或无意义序号规避文件名冲突；不在自动毕业时顺手重命名已有日期文件。历史迁移必须由用户明确授权，并在执行前检查冲突、引用、别名与回滚点。
- 导入目录前先区分“原始资料归档/索引”和“知识提炼”。用户只要求保存或整理原始文档时，不套知识笔记模板、不虚构问题背景与原因分析，也不把文档主张升级为已验证结论。

## 毕业到 Obsidian

- `vault:default/00_Inbox_收集箱/02_Decisions_决策/Codex Obsidian 自动知识毕业机制.md`
- `vault:default/00_Inbox_收集箱/02_Decisions_决策/Obsidian 笔记使用稳定主题文件名.md`
- `vault:default/00_Inbox_收集箱/03_Troubleshooting_故障排查/Obsidian 批量重命名的链接与工作区状态保护.md`
- `vault:default/17_Archive_归档/Obsidian 与 LLM Wiki 单库融合结构.md`
- `vault:default/00_Inbox_收集箱/03_Troubleshooting_故障排查/LLM Wiki 调用 Codex CLI 的版本与模型缓存兼容.md`
