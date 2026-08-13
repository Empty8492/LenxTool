# LenxTool 项目知识日志

<!-- 最新记录放在此行下方；每条只写摘要、证据和详情指针，控制在约 20 行内。 -->

## 2026-08-13 · P2-D 执行手册与仓库目标校正

- 文档：补齐 README、PROJECT_GUIDE、P2 专项计划、TEST_REPORT、WORKER_DEPLOYMENT 和 RELEASE_GUIDE 的可执行下一步、真实 provider 矩阵、部署顺序、证据字段、停止/回滚条件和正式制品闸门。
- 仓库：核验 GitHub canonical repository 为 `Empty8492/LenxTool`，本地 `origin` 已从复数旧地址校正为单数地址；当前工作在短期分支 `agent/p2-integrations-docs-release`。
- 边界：本轮只把“代码与假 HTTP 契约完成”推进到可执行发布手册，不伪造真实 token、实例、D1 生产数据或签名制品证据；P2-D 仍开放。
- 证据：文档内部链接、schema v2→0011→Worker→Desktop 顺序和 P2-D 关闭条件已交叉核对；最终测试与发布结果仍以 `docs/TEST_REPORT.md`、`docs/PROJECT_GUIDE.md` 为准。

## 2026-08-13 · P2-16～P2-19 完成，P2-23 选择 A 关闭

- 决策：采用 N2/R1/G1/W1/A；Readeck、Outline、qBittorrent 与受控 Webhook 进入生产 DI，P2-23 不实施邮件、不收邮箱、不扩云端内容权限。
- 协议：D1 migration 0011 与 Worker/Desktop schema v2 分列保存公网、精确私网 endpoint、resource 和 qBit loopback 端口；旧客户端只获兼容投影，advanced 管理写要求升级。
- 本机边界：四个 provider 使用独立目标、DPAPI marker 0→秘密→marker 1、删除反向停用、已保存目标测试、策略/DNS 先于秘密和覆盖正文的 deadline；旧 `default` 槽仍可显式删除但不会自动激活。
- 幂等与副作用：Readeck 以稳定标签且仅明确 count 0 时创建；Outline UUID 绑定目标修订并固定个人草稿；qBit 确认绑定目标修订并写后核对 hash/category；Webhook 固定能力声明、幂等键、ack 与可选正文 HMAC。
- 终审修复：关闭旧客户端投影、笛卡尔授权假设、正文滴流、重复响应头、写后畸形回执、Outline 跨 collection、qBit 202/`.torrent` 暂时故障、设置测试跨 endpoint、凭据 marker 和确认竞态等高风险路径；最终只读审计无剩余 P0/P1。
- 门禁：Core 222/222、Infrastructure 811/811、App 非 WPF 523/523、WPF 14/14、Worker 81/81、typecheck、Release build 0/0、NuGet/npm 漏洞 0、改动 C# 格式和 diff check 通过。
- 边界：未使用真实第三方凭据或实例；P2-D、生产 Worker/D1 部署、签名包与跨机升级矩阵继续开放。
- 详情：`cairn/integration-adapter-boundaries.md`、`docs/TEST_REPORT.md`、`docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md`、`docs/decisions/ADR-004-server-email-digest-gate.md`。

## 2026-08-13 · WPF CalendarAutomationPeer 误报门禁关闭

- 结果：`SelectionControlsWpfRuntimeTests` 不再在原生日历 Automation 树更新时空引用；10 个 WPF runtime 类逐进程共 14/14，完整 App 522/522。
- 根因：测试只应用外层 `Calendar` 模板就强制布局，内层 `CalendarItem` 仍未解析 Previous/Header/Next；WPF peer 在 `MonthControl` 存在后会直接读取这三个部件。
- 修复：不手动应用模板；等待生产 DatePicker 的嵌套控件自然加载后显式查询完整 Calendar Automation 子树，并继续执行键盘、日期、缩放和主题验收；精确独立进程连续 10/10，生产 XAML 与行为未改。
- 门禁：首轮仅有未改性能用例 2.092 秒越过 2 秒预算，精确为 636 ms、Infrastructure 独立全量 763/763；最终全解决方案 App 522、Core 202、Infrastructure 763，共 1487/1487；Release build 0 警告/0 错误。Worker 与依赖审计未重跑。
- 路线边界：P2-23 仍等待 A/B/C；P2-16～P2-19 仍需逐项选择，本轮未替产品负责人放行功能。
- 详情：`cairn/wpf-runtime-test-host.md`、`docs/TEST_REPORT.md`。

## 2026-08-11 · Git 分支清理知识审计

- 结果：形成通用的已合并分支清理证明链，覆盖远端刷新、独有提交、祖先关系、stash、未跟踪文件、worktree 占用、逐目标授权和删除后读回。
- 漂移修复：`cairn/Cited.md` 中 4 个迁移前 Vault 指针已按当前 profile 修正到唯一存在的稳定主题文件，未改动引用语义。
- 证据：GitHub/main、所有本地分支、stash 与工作树均完成只读核验；四个旧指针原目标不存在，新目标各唯一命中。
- 边界：本轮只沉淀知识并修复指针，没有删除分支、stash 或工作树内容；当前未提交的规则与项目知识改动继续保留。
- 详情：`vault:default/08_AI_Workflows_AI与工作流/Git 已合并分支清理的证据链与脏工作树保护.md`、`cairn/Cited.md`。

## 2026-08-10 · 未接通集成凭据入口收口

- 结果：个人通用设置只显示已注册生产 exporter 与健康探针的 Readwise；Readeck、Outline、qBittorrent 与 Webhook 不再暴露 TargetId、DPAPI 凭据或连接测试入口。
- 根因：`EntryIntegrationKind` 同时承担稳定线协议/共享策略契约，界面曾直接枚举它，错误地把“协议已预留”解释为“客户端适配器可用”。
- 兼容：公共枚举和管理员策略值不删除；旧版本保存的占位类型与程序化传入均回到 Readwise 官方固定目标，不能进入外联流程。旧值不会返回界面或交给探针/exporter；当前记录删除后以可重放顺序规范化匹配设置，历史未引用槽位只允许用户凭原类型和 TargetId 定向删除。共享 DPAPI blob 可能在存储层整体解密，不承诺进程从不解密旧值。
- 状态边界：这是防误配门禁，不完成 P2-16～P2-19；Readeck 仍受缺少幂等/外部 ID 契约阻断，其余三项仍待选择和专项安全设计。
- 验证：失败先行证明现状曾暴露 5 项；ViewModel/布局 25/25、App 非 WPF 508/508、Core 202/202、Release build 0 警告/0 错误、NuGet 漏洞 0；WPF 13/14 的唯一失败仍是既有 CalendarAutomationPeer 基线。
- 详情：`cairn/integration-adapter-boundaries.md`、`docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md`。

## 2026-08-10 · Independent-01 JSON 双栏结构 Diff 关闭

- 结果：在文档与数据页交付左右独立校验、根 `null`、交换、新增/删除/修改路径和值、回收虚拟化差异列表，以及 2 MiB 单侧、500 项数量和 1,024/256 KiB 路径预算；未修改 SQLite、Worker 或 RSS 模型。
- 根因修复：后台比较完成后仍可能在 UI continuation 恢复前被取消，单靠字符串快照也无法识别 A→B→A；现以取消令牌、单调任务代际、输入修订和快照四重校验阻止旧结果发布。
- 错误语义：`BytePositionInLine` 明确显示为一基“行内 UTF-8 字节位置”，不再误称字符列。
- 审查：发布前独立复核还关闭了合法根 `null` 误判、超长路径内存放大、短路径逐段 SHA 回退、路径预算截断文案和外层滚动导致虚拟化失效；现以延迟路径摘要、总路径预算和生产有限视口防复发。
- 新鲜验证：JsonToolkit 12/12、Core 全量 202/202；App ViewModel 7/7、布局 1/1、非 WPF 508/508、Diff 真实 WPF 1/1；长祖先分配回归、主动大输入取消连续 10/10；全部 WPF 类逐进程为 13/14，唯一失败是既有 SelectionControls Calendar AutomationPeer 基线；Release build 0 警告/0 错误，NuGet 漏洞 0。
- 验证边界：最小 920×620 真实 `MainWindow` 与等效 200% 布局已自动化，真实 Windows 200% DPI/文本缩放人工观察仍属于发布矩阵。
- 详情：`cairn/json-diff-tool.md`、`docs/plans/EXISTING_BACKLOG_ALIGNMENT.md`、`docs/TEST_REPORT.md`。

## 2026-08-08 · P2-23 服务端邮件摘要决策草案

- 结果：ADR-004 保持 Proposed，建议当前不实施、不收集邮箱，Feed 标题/摘要/正文/AI 结果云端保留均为 0 天；等待产品负责人选择 A/B/C。
- 根因判断：当前本地摘要必须由桌面端运行；仅元数据邮件不解决离线生成问题且与 Windows 通知重叠，内容邮件则必然新增云端聚合、邮箱 PII、供应商保留、版权与运营责任。
- 供应商评估：Cloudflare 原生路径与现有 Worker 最贴合，但仍为 Beta，Workers Paid 最低 $5/月，发送域新建后默认约 7 天内容预览；Resend 默认保留邮件数据 30 天；SES 可选无最低消费的 $0.10/千封 à-la-carte，但运维边界更大。
- 安全边界：未新增 migration、Worker API、发信代码、供应商凭据或邮箱字段；任何实现仍需独立批准、威胁模型和删除演练。
- 证据：当前 `users` schema 无邮箱；官方 Cloudflare/Resend/AWS 文档已记录在 `cairn/Cited.md`。
- 详情：`docs/decisions/ADR-004-server-email-digest-gate.md`、`docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md`。

## 2026-08-08 · P2-22 Windows 通知关闭

- 结果：默认关闭、隐私分级、静默/聚合、受控激活、Runtime 降级、schema v25、设置 UI 与安装资产闸门已完成；下一项为 P2-23 决策，不自动进入云端实现。
- 根因修复：终审发现并关闭 5 项 P1——设置返回后旧标题仍可能投递、Host 早于持久策略恢复、Toast 点击后投影/窗口外角标不同步，以及 Windows App Runtime/WebView2 缓存未经验证即打包。
- 安全边界：系统载荷只有严格 64 位小写 `notification_id`；目标必须重读 SQLite 并映射到通知收件箱、Feed 条目或 AI 报告，不接受 URI。
- 发布边界：两个 Microsoft 安装资产的缓存与下载统一验证固定 SHA-256、有效 Authenticode 和精确发布者；缺少 Inno/离线私钥时只生成开发发布目录，不宣称正式 Setup。
- 补充修复：设置页初始化遇到已生效的相同策略时不再重复应用，避免清空启动突发中待发送的聚合计数。
- 证据：Release Core 191、Infrastructure 763、App 500（1454/1454），Worker 78/78、typecheck、0 警告构建、NuGet/npm audit 0；真实 Windows Toast 与设置页常规/最小窗口通过。
- 详情：`cairn/windows-notifications.md`、`docs/TEST_REPORT.md`、`docs/THREAT_MODEL.md`、`docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md`。

## 2026-08-07 · 项目知识层纳入版本控制

- 结果：远端主线开始跟踪项目知识目录、机器 profile 关联与自动维护规则；同步时与既有本地历史合并，未覆盖 P2-20/P2-21 与知识工作流记录。
- 当前焦点：以 `docs/PROJECT_GUIDE.md` 第 10 节为准；P2-22 已关闭，P2-23 保持产品决策闸门。
- 证据：`AGENTS.md`、`.cairn/config.yaml`、`cairn/ROADMAP.md`、提交 `5fa4558`。

## 2026-08-07 · 移除 Obsidian 的 LLM Wiki 目录格式

- 结果：`00_Inbox_收集箱`、`01_Dashboard_仪表盘`、`02_Projects_项目`、`03_Research_调研与资料` 和 `17_Archive_归档` 已迁回 Vault 根目录；活动 Vault 不再包含 `.llm-wiki`、`wiki`、`raw`、`purpose.md` 或 `schema.md`。
- 保护：迁移前完整快照位于 `D:\Obsidian\_Backups\Lenx-before-remove-llm-wiki-20260807-104600`，共 58 个文件、3,723,990 字节；移出的 LLM Wiki 外壳另保存在快照的 `_removed-from-active-vault` 下。
- 配置：机器 `default` profile 已解析到根目录 Inbox 与导航，写入模式保持 `explicit_only`；项目内 `vault:default/...` 指针已去掉 `wiki/` 前缀。
- 当前真相：LLM Wiki 单库决策笔记已标记为“已废止”并移入 `17_Archive_归档`，导航页已改为纯 Obsidian 根目录结构。
- 验证：26 篇活动 Markdown 全部可读；原知识文件缺失 0、非预期内容变化 0、`.obsidian` 哈希变化 0、旧目录前缀指针 0。
- 链接边界：仍有迁移前已存在的 3 个缺失目标，共 10 处引用；本次没有新增失效目标，也未伪造历史笔记。
- 详情：`cairn/knowledge-capture.md`、`vault:default/01_Dashboard_仪表盘/Lenx 知识库导航.md`、`vault:default/17_Archive_归档/Obsidian 与 LLM Wiki 单库融合结构.md`。

## 2026-08-07 · P2-21 发布到 GitHub main

- 结果：P2-21 日/周本地摘要已与门禁记录一起快进到本地和远端 `main`，远端 SHA 为 `d5bc645f67d434467eee77ac515f6b857d6d0452`，分歧 `0/0`。
- 新鲜验证：P2-21 聚焦 Core 7/7、Infrastructure 57/57、App 119/119；完整非 WPF 回归 App 423/423、Core 191/191、Infrastructure 755/755；Release 构建 0 警告/0 错误。
- WPF 边界：标记为 WPF runtime 的 9 个类共 13 个用例，9/13 通过；4 项仍为 `CalendarAutomationPeer.GetChildrenCore` 空元素基线栈，未进入摘要链路。
- 安全门禁：NuGet 0 漏洞，Worker 生产依赖 `npm audit --omit=dev` 为 0；完整 npm 审计的 5 项开发/测试依赖漏洞仍独立开放，未执行破坏性自动修复。
- 提交：`3bfd55a` 功能提交，`d5bc645` 发布门禁文档提交；未跟踪的 `AGENTS.md`、`.cairn/` 和 `cairn/` 保留未发布。
- 详情：`docs/TEST_REPORT.md`、`cairn/feed-digest-schedules.md`、`docs/PROJECT_GUIDE.md`。
- Obsidian：已检索到既有 `vault:default/00_Inbox_收集箱/07_Data_API_数据与接口/LenxTool 外部模型摘要的耐久防重与原子提交.md`；本轮无新增跨项目知识，不重复创建笔记。

## 2026-08-06 · LLM Wiki 调用 Codex CLI 的缓存兼容故障收口

- 症状：LLM Wiki Chat 调用本地 Codex CLI 时退出码为 1，报 `models_cache.json` 缺少 `supports_reasoning_summaries`，随后刷新模型超时。
- 根因：LLM Wiki 按 PATH 选中了 npm 安装的 `codex-cli 0.141.0`，而 Codex Desktop 共用目录中的模型缓存由 `0.147.0` 写入；旧 CLI 无法反序列化新缓存格式。
- 修复：npm CLI 升级为当前稳定版 `0.146.1` 后，登录状态仍为 ChatGPT 登录；LLM Wiki 已重新检测到新版本。
- 验证：使用 LLM Wiki 源码完全相同的 `read-only`、`ephemeral`、`--json` 与自定义模型参数执行最小请求成功；LLM Wiki 聊天记录也已保存升级后的正常回复。
- 配置边界：Codex CLI 提供商设置即时持久化，无需额外 Save；`Isolate local CLI configuration` 不解决二进制与缓存 schema 不兼容。
- Obsidian：`vault:default/00_Inbox_收集箱/03_Troubleshooting_故障排查/LLM Wiki 调用 Codex CLI 的版本与模型缓存兼容.md`。
- 详情：`cairn/knowledge-capture.md`。

## 2026-08-06 · Obsidian 与 LLM Wiki 单库融合迁移

- 结果：`D:\Obsidian\Lenx` 继续作为唯一物理 Vault，同时成为 LLM Wiki 项目根；原有知识分类整体迁入 `wiki/`，新增 `schema.md`、`purpose.md`、标准生成目录与 `raw/sources`。
- 配置：机器 profile 的 Inbox 与导航路径分别改为 `wiki/00_Inbox_收集箱` 和 `wiki/01_Dashboard_仪表盘/Lenx 知识库导航.md`；解析结果仍为 `automatic_after_substantive_work`。
- 保护边界：根目录 `.obsidian` 与 `99_Templates_模板` 未移动；原有 Markdown 内容按路径映射核验无丢失，`.obsidian` 的 10 个文件哈希逐项不变。
- 链接审计：迁移没有新增失效双链；仍存在 3 个迁移前已有的历史悬空目标，未在本轮伪造补全。
- 回滚：完整快照为 `D:\Obsidian\_Backups\Lenx-before-llm-wiki-unification-2026-08-06-153818.zip`，SHA-256 为 `6F9A7E9103B91F50DBD43ABAA10E2B4C0D80404BD3F429A04D66ED5A8D757AC4`。
- 验收边界：文件结构与 LLM Wiki 当前项目识别条件已通过静态核验；实际 GUI 打开需安装 LLM Wiki 后再验证。
- 详情：`cairn/knowledge-capture.md`、`vault:default/17_Archive_归档/Obsidian 与 LLM Wiki 单库融合结构.md`。

## 2026-08-06 · P2-21 日/周本地摘要与外部调用边界收口

- 结果：两个稳定计划 ID 已接入 ACTIVE Feed/分类/关键词、上一本地日历日/周、有界去重输入、本地报告/FTS、AI 报告管理卡和原子 `.txt` 导出。
- schema v24：`local_scheduled_task_payloads` 原子保存计划+范围，`local_schedule_run_retries` 持久保存最早重试时间，`feed_digest_requests` 记录 STARTED/COMPLETED/AMBIGUOUS/DISCARDED。
- 根因边界：供应商无幂等键时无法同时承诺崩溃必定产出和绝不重复计费；现选择 at-most-once，结果不明时取消窗口并抑制自动重放，代价是可能跳过一次摘要。
- 原子提交：成功报告/FTS、请求与窗口终态在同一 SQLite 事务中验证租约和计划代际；代际已变则只记 DISCARDED 且不落报告。
- 重试语义：明确可重试的 429 支持 Delta/HTTP-date 并持久退避；永久 4xx 终止窗口，不热循环或饿死其他计划。
- 审查：两轮独立审查共促成 5 项 P1 和 1 项 P2 修复；最终复核无剩余 P0/P1/P2。
- 新鲜验证：Core 191/191、Infrastructure 755/755、App 非 WPF 427/427（共 1373）、Worker 78/78、strict typecheck、Release build 0 警告/0 错误、NuGet 0 漏洞；WPF 有效隔离 8/9，唯一失败是既有 CalendarAutomationPeer 环境基线。
- 本地提交：`3bfd55a` (`feat: add resilient scheduled feed digests`)；未推送远端。
- 开放边界：npm 工具链仍有 5 项（1 high / 4 moderate）；未用真实 DeepSeek Key 发起外部请求；未进行正式签名发布。
- 详情：`cairn/feed-digest-schedules.md`、`docs/ARCHITECTURE.md`、`docs/THREAT_MODEL.md`、`docs/TEST_REPORT.md`。

## 2026-08-06 · 删除误模板化的 document 导入产物

- 用户纠正：`C:\Users\admin\Desktop\document` 是原始交接文档目录，不是待逐篇套用知识沉淀模板的问题集合。
- 根因：此前导入没有先区分“原始资料归档/索引”和“知识提炼”，把文档主张强行组织成背景、原因与实施步骤，产生了错误语义。
- 删除：移除 11 篇主题化导入笔记和 1 篇 `document` 导入审计，共 12 篇；同步删除导航页 4 个入站入口。
- 完整性：删除后知识区剩余 26 篇 Markdown、69 个双向链接，缺失与歧义均为 0；`D:\Projects` 未发现相关外部标题或源路径引用。
- 原始资料：桌面 `document` 仍为 7095 个文件、总大小 5419056473 字节，元数据 SHA-256 仍为 `AA20AA4619951283EDA2C737023DD28EE3FF75DB6D9ADA5F09658DA62E2F80BF`。
- 回滚：删除前完整 Vault 快照为 `D:\Obsidian\_Backups\Lenx-before-document-import-note-removal-2026-08-06.zip`，SHA-256 为 `80E7A66BF041FC2E2CD10E5A85DF1F52D243A59769C267B7468FCD62A37B40F9`。
- 待办：本轮按用户要求先删除；公开 `knowledge-capture` 技能尚未增加“原始资料归档模式”，后续需单独修正规则并发布。
- 详情：`cairn/knowledge-capture.md`。

## 2026-08-06 · Obsidian 历史文件名迁移与结构校准

- 结果：用户明确授权后，Vault 内 34 篇日期前缀笔记全部改为稳定主题文件名；日期继续保留在 YAML `created`、`updated` 中。
- 链接安全：同步更新 65 处旧名称引用，并为 34 篇迁移笔记保留旧文件名 `aliases`；迁移后 111 个双向链接无缺失、无歧义、无日期前缀目标。
- 结构：删除 Obsidian 默认 `欢迎.md`；Inbox 的 11 个路由分类、项目区、调研区、归档区、仪表盘和模板区均有明确后续用途，继续保留。
- 导航：`Lenx 知识库导航.md` 已改成实际存在的目录与当前自动毕业流程，不再列出尚未创建的长期分类。
- 回滚：迁移前完整快照为 `D:\Obsidian\_Backups\Lenx-before-no-date-migration-2026-08-06.zip`，SHA-256 为 `0FA27B7CF4722738504516D981B38694BB4EBF55C5888FE0FDFB24EFB6BBA38D`。
- 知识毕业：运行中的 Obsidian 会刷新 `workspace.json`、别名不能修复路径型外部指针是本轮新确认的通用迁移边界，已单独沉淀为故障排查笔记。
- 详情：`cairn/knowledge-capture.md`、`vault:default/01_Dashboard_仪表盘/Lenx 知识库导航.md`。

## 2026-08-05 · P2-20 后台执行与计划代际取消收口

- 结果：新增通用幂等处理器契约、单并发后台轮询、租约心跳、失败/宿主停止释放及生产 DI；没有具体处理器时安全空转。
- 根因修复：计划变更不能只修改未来游标，还必须使旧执行窗口失效；现以计划 `updated_at` 作为持久代际，保存、启停或删除会请求取消，最终完成/释放 SQL 再次校验以封闭竞态。
- 审查闭环：独立审查发现“计划删除后仍可完成/释放”与“初次取消探针遇宿主停止未释放租约”；补充失败先行回归并修复后，复审无剩余 P0/P1。
- 新鲜验证：运行仓储 20/20、计划仓储 10/10、处理器 5/5、DI 1/1；Core 184/184、Infrastructure 745/745、App 非 WPF 404/404、Worker 78/78、strict typecheck、Release build 0 警告/0 错误、NuGet 0 漏洞。
- 环境边界：WPF 独立串行 6/9，3 项均为既有/基线可复现且不经过计划执行链路；npm 工具链仍有 5 项漏洞（1 high / 4 moderate），未执行破坏性 `--force` 回退。
- 发布：实现提交 `088274055a62d6496dcad3064a2b0744c7d62195` 与状态文档补齐提交 `9f061775622d7be9aa3c190db90ff0f725d96705` 均已推送；GitHub `main` 最终指向 `9f06177`，远端 SHA 一致且分叉 0/0。
- 下一片：P2-21 的具体日/周摘要处理器、输入与无新内容边界、结果持久化/检索/导出和管理 UI。
- 知识边界：本轮只更新项目 `cairn/`；共享 Obsidian 按上级只读约束未写回。
- 详情：`cairn/local-schedule-windows.md`、`docs/ARCHITECTURE.md`、`docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md`、`docs/TEST_REPORT.md`。

## 2026-08-05 · Obsidian 新笔记取消日期前缀

- 结果：`knowledge-capture` 此后使用稳定主题文件名，日期仅保存在 YAML `created` 与 `updated` 中。
- 冲突策略：没有实质新增则跳过；不同主题使用项目、组件、场景或结论限定词，不追加日期、随机数或无意义序号。
- 历史边界：现有日期前缀文件不自动重命名，避免破坏双向链接和外部引用；迁移须由用户单独明确授权。
- 验证：新静态契约测试通过，原 Vault 解析测试与技能结构校验通过，本机安装副本和发布副本一致。
- 发布：公开仓库远端 `main` 已更新到 `2a8a7e7b0e662ab0c50265cda5826cf0a442fd1a`。
- Obsidian：`vault:default/00_Inbox_收集箱/02_Decisions_决策/Obsidian 笔记使用稳定主题文件名.md`。
- 详情：`cairn/knowledge-capture.md`。

## 2026-08-05 · P2-20 自动毕业缺口补齐

- 审计结果：前一实现任务已自动更新 `cairn/LOG.md` 与 `ROADMAP.md`，但没有创建稳定主题笔记，Obsidian 也未检索到 P2-20 或 `LocalScheduleRun` 记录，因此此前只完成项目层的部分闭环。
- 当前核验：提交 `b1f754b` 已在 `main`/`origin/main`；本轮重新运行 `LocalScheduleRunRepositoryTests` 为 14/14 通过。
- 补齐结果：新增 `cairn/local-schedule-windows.md`，并自动毕业到 Obsidian 的数据与接口 Inbox 分类。
- 产品边界：窗口与恢复契约已完成；后台处理器、禁用与在途取消协同、生产 DI 和 UI 仍未完成，P2-20 不能称为完整可用产品功能。
- 未确认项：仓库证据无法证明上次遗漏 Obsidian 写入的具体触发原因，不把推测写成根因。
- Obsidian：`vault:default/00_Inbox_收集箱/07_Data_API_数据与接口/LenxTool 本地计划窗口幂等与崩溃恢复.md`。
- 详情：`cairn/local-schedule-windows.md`。

## 2026-08-05 · P2-20 窗口幂等与崩溃恢复契约

- 结果：schema v23 新增 `local_schedule_runs`；RunOnce/Skip 漏跑语义、唯一窗口、原子游标推进、PENDING/过期租约接管、完成/取消/释放和陈旧 owner 拒绝已实现。
- 根因边界：计划定义不能同时承担执行账本；窗口插入与游标推进现处于同一立即写事务，避免“推进后崩溃丢任务”和重复启动重复执行。
- 审查修复：租约到期即失权；续租单调且同时间戳只允许相同 payload，领取和提交的到期边界互斥。
- 新鲜验证：窗口 14/14、Core 184/184、Infrastructure 739/739、App 非 WPF 399/399、WPF 独立串行 8/9（1 项既有 Calendar AutomationPeer 环境失败）、Worker 78/78、strict typecheck、Release build 0 警告/0 错误、NuGet 0 漏洞。
- 开放门禁：npm 开发/测试工具链 5 项漏洞（1 high / 4 moderate）；未执行不兼容的强制回退。
- 下一片：后台处理器、禁用与在途窗口取消协同、生产 DI 和 UI。
- 详情：`docs/ARCHITECTURE.md`、`docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md`、`docs/TEST_REPORT.md`。

## 2026-08-05 · Obsidian 自动毕业启用并公开技能

- 结果：本机 `default` profile 已切换为 `automatic_after_substantive_work`，全局与 LenxTool 项目规则会在实质性任务收尾时自动毕业合格知识。
- 修订：本条取代下方同日记录中的“Obsidian 毕业仍需用户明确授权”；原因是用户明确将全局策略改为自动模式。
- 边界：自动模式只免除逐次确认，不是后台同步，也不降低“已验证、可复用、已抽象、可追溯”的毕业门槛。
- 验证：解析器的自动、旧配置回退、禁用与非法值测试通过；技能结构校验通过；安装副本与发布副本 SHA-256 一致。
- 发布：`https://github.com/Empty8492/codex-knowledge-capture` 为公开仓库，远端 `main` 与提交 `0ffbc7ebcb9be1a77834f822d0dab14ff58ff15e` 一致。
- Obsidian：`vault:default/00_Inbox_收集箱/02_Decisions_决策/Codex Obsidian 自动知识毕业机制.md`。
- 详情：`cairn/knowledge-capture.md`。

## 2026-08-05 · Vault 路径改为主机级 profile

- 结果：项目配置升级为 v3，只保存 `graduation.profile: default`；Vault 绝对路径移至当前主机的 `%USERPROFILE%\.codex\knowledge-capture.json`。
- 原因：项目仓库和技能中的 `D:\Obsidian\Lenx` 会把另一台主机绑定到本机盘符，破坏可移植性。
- 行为边界：`cairn/` 继续自动维护，但不会自动复制到 Obsidian；Obsidian 毕业仍需用户明确授权。
- 兼容性：另一台主机只需把 `default` 映射到当地 Vault，无须修改项目文件。
- 详情：`.cairn/config.yaml`、`AGENTS.md`、`cairn/Cited.md`。

## 2026-08-05 · 项目知识层初始化

- 结果：已建立项目级 `AGENTS.md`、`.cairn/config.yaml`、知识日志和轻量路线图；自动沉淀对后续实质性 Codex 任务生效。
- 初始化前仓库状态：`main`，提交 `6c69445`，worktree 干净。
- 当前焦点（文档主张）：P2-20 已完成本地时区计算与 schema v22 计划持久化；下一窄片为错过执行策略、窗口幂等领取、崩溃恢复与后台处理。
- 证据：`README.md`、`docs/PROJECT_GUIDE.md` 第 10 节、`docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md`、`docs/TEST_REPORT.md`。
- 注意：初始化未重跑构建或测试；具体下一任务仍须根据当前代码和新鲜验证重新确定。
- 详情：`AGENTS.md`、`.cairn/config.yaml`、`cairn/ROADMAP.md`、`cairn/Cited.md`。
