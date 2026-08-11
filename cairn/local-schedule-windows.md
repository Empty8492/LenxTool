---
type: "project-knowledge"
status: "active"
summary: "P2-20 以独立运行账本、租约 fencing、后台处理器和计划代际取消完成通用本地计划执行基础；具体摘要处理器与 UI 留给 P2-21。"
tags:
  - "P2-20"
  - "scheduling"
  - "sqlite"
  - "background-service"
  - "cancellation"
contains:
  - "decision"
  - "experience"
  - "procedure"
  - "open-question"
created: "2026-08-05"
updated: "2026-08-05"
related: []
authoring_mode: "agents"
---

# 本地计划窗口幂等与崩溃恢复

> [!abstract] 当前结论
> `local_schedule_runs` 已把计划定义与执行账本分离，并用唯一窗口、原子游标推进和带过期边界的租约令牌保证重复启动与崩溃恢复收敛；后台处理器现已接线，并以计划 `updated_at` 作为持久代际取消旧窗口。当前没有注册具体业务处理器，因此生产运行安全空转，真实日/周摘要与 UI 属于 P2-21。

## 形成背景

schema v22 的 `local_scheduled_tasks` 能保存计划定义和下一次 UTC 游标，但计划定义不能同时充当执行历史与所有权账本。若先推进游标再记录执行，进程崩溃会丢任务；若只记录执行而缺少唯一窗口与 owner fencing，重复进程和旧 worker 会重复执行或覆盖新结果。

## 当前结论

- schema v23 新增 `local_schedule_runs`，以 `(schedule_id, scheduled_for)` 作为逻辑窗口主键；执行内容、凭据和任务输出不进入该表。
- `ClaimDueAsync` 在 `BEGIN IMMEDIATE` 事务中同时插入窗口并推进计划游标；任一步失败都会整体回滚。
- `scheduled_for < missedBeforeUtc` 才算漏跑。`RunOnce` 最多领取持久游标代表的一次并把游标推进到 `nowUtc` 之后；`Skip` 只推进游标，不制造伪执行历史；等于边界时仍正常领取。
- 领取优先恢复 `PENDING` 或租约到期的 `RUNNING` 窗口。每次接管生成新的 `LeaseToken` 并增加尝试次数，旧 owner 无法续租、完成、取消或释放。
- 租约到期即失权，不需要等待新 owner 接管；续租时间只能单调增加，相同更新时间只接受相同到期时间的幂等重放。
- `Completed` 和 `Cancelled` 是终态；`ReleaseAsync` 把有效租约释放回可恢复状态。单次计划在窗口落盘或按 `Skip` 推进后自动禁用。
- 后台处理器只向仓储提交已注册、稳定且声明幂等的处理器 ID，单并发执行，按租期三分之一续租；未知计划不会被领取，重复或非幂等处理器在启动时被拒绝。
- 每次保存或启停计划都会更新计划代际并终态取消未被有效 owner 持有的旧窗口；已领取处理器通过持久化取消探针协作退出。计划删除也视为取消。
- 完成和释放的最终 SQL 同时检查租约所有权、有效期、计划仍存在且 `task.updated_at <= run.created_at`，避免在取消探针与最终提交之间发生竞态穿透；取消写入仍被允许。
- 生产 DI 已注册仓储、处理器与后台服务；当前没有具体 `ILocalScheduledTaskHandler`，所以不会误消费尚未定义负载契约的计划。

## 原因与证据

- 核心契约：`ILocalScheduleRunRepository`、`ILocalScheduleProcessor`、`ILocalScheduledTaskHandler` 与 `LocalScheduleRunLease`。
- SQLite 实现：`LocalScheduleRunRepository`、`LocalScheduledTaskRepository` 与 `SqliteDatabase` schema v23；本轮无需 schema v24，既有 `updated_at` 即为持久计划代际。
- App 实现：`LocalScheduleProcessor`、`LocalScheduleBackgroundService` 与 `App.xaml.cs` 生产 DI。
- 发布证据：实现提交 `088274055a62d6496dcad3064a2b0744c7d62195` 后以 `9f061775622d7be9aa3c190db90ff0f725d96705` 补齐路线图状态；本地与 GitHub `main` 最终对齐且分叉 0/0。
- 2026-08-05 本轮聚焦验证：运行仓储 20/20、计划仓储 10/10、处理器 5/5、处理器 DI 1/1，均通过且无跳过。
- 完整门禁：Core 184/184、Infrastructure 745/745、App 非 WPF 404/404、Worker 78/78、strict typecheck、Release build 0 警告/0 错误、NuGet 0 漏洞。
- WPF 独立串行 6/9；1 项 Calendar AutomationPeer 环境基线失败，2 项帧进度阈值失败已在未修改的 `b1f754b` 基线复现，均不经过本地计划执行链路。npm 开发/测试工具链仍有 5 项已知漏洞（1 high / 4 moderate），未执行破坏性 `--force` 回退。
- 独立审查首轮发现“计划已删除仍可能完成/释放”和“初次取消探针遇宿主停止未释放租约”两处边界；根因修复及对应失败先行回归后，复审确认无剩余 P0/P1。

## 可复用设计约束

1. 时间计划定义和执行窗口账本必须分表，避免把“下一次何时运行”和“谁正在运行”混成一个状态。
2. 窗口身份必须由稳定业务键唯一约束，不能只靠进程内锁防重复。
3. 创建执行窗口与推进下一游标必须位于同一数据库事务，避免崩溃窗口。
4. 所有完成类写入必须携带不可猜测的 owner token，并同时检查状态、token 和租约有效期。
5. 租约过期边界必须在领取、续租和提交路径使用同一互斥语义；否则旧 worker 在边界时仍可能提交。
6. 跳过漏跑不能伪造成功历史；状态表应只记录真实创建过的执行窗口。
7. 计划变更必须形成持久代际，取消探针与最终数据库写入必须双重校验；仅靠进程内取消令牌无法覆盖崩溃、重启与竞态。
8. 通用调度器只能消费已注册且幂等的处理器，不应在负载契约尚未定义时猜测业务行为。

## 适用边界

- 已完成：本地时区计算、计划定义持久化、RunOnce/Skip 漏跑语义、窗口幂等、原子游标、租约恢复、后台处理、计划代际取消、最终提交防竞态和生产 DI。
- 尚未完成：真实日/周摘要处理器、输入窗口与无新内容语义、结果持久化/检索/导出和用户管理界面；这些属于 P2-21，不把通用基础误述为用户可见完整功能。
- 因此 P2-20 可按通用执行基础完成收口；生产环境当前因零具体处理器而安全空转。

## 防复发约束

- 新增运行状态或终态时同时审查 SQLite CHECK、领取查询、续租、完成、取消、释放和 UI 文案。
- 任何允许旧 token 在到期后写入的路径都属于所有权破坏，必须有边界回归测试。
- 新增计划变更入口时必须复用代际更新与旧窗口取消语义；最终完成/释放查询仍须以数据库状态兜底，不能只依赖取消令牌。
- 新增具体处理器前必须先确定稳定 ID、幂等边界、输入/输出契约和失败重试语义，再注册到生产 DI。

## 毕业到 Obsidian

- `vault:default/00_Inbox_收集箱/07_Data_API_数据与接口/LenxTool 本地计划窗口幂等与崩溃恢复.md`
