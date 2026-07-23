# 测试报告

测试日期：.NET / Worker 2026-07-24（Asia/Shanghai）
版本：0.1.0  
配置：Release / win-x64 / .NET SDK 10.0.302

## 自动化结果

| 测试组 | 结果 |
|---|---:|
| LenxTool.Core.Tests | 39 passed |
| LenxTool.Infrastructure.Tests | 185 passed |
| LenxTool.App.Tests | 100 passed |
| Cloudflare Worker Vitest | 39 passed |
| Worker TypeScript strict typecheck | passed |
| .NET build warnings | 0 |
| NuGet vulnerable packages | 0 detected |

本轮执行 `dotnet build LenxTools.slnx -c Release --no-restore` 与 `dotnet test LenxTools.slnx -c Release --no-build`，结果为 0 警告、0 错误；Core 39、Infrastructure 185、App 100，共 324/324 通过且无跳过。Worker 严格 typecheck 和官方 workerd/D1 Vitest 39/39 保持通过（Wrangler 日志目录受当前沙箱权限限制，但不影响断言）。

P0-08 覆盖 4 项 SQLite 集成场景并同步既有 schema 断言：新建库创建目录状态、分类、Feed、抓取状态、条目、索引和搜索映射；真实 schema v2 哨兵数据经 v3/v4 原位升级后仍可由现有仓储读取；迁移对象冲突时完整回滚且版本停留在 v3；相同外部 ID、URL 和内容哈希可存在于不同 Feed，同一 Feed 内重复外部 ID 被约束拒绝。既有包含未 checkpoint WAL 提交的一致性备份测试继续通过。

P0-09 新增 5 项 SQLite 仓储集成测试：覆盖新库空 ACTIVE 状态与 ALL 不可伪造、完整 ALL 快照往返及 ACTIVE 投影、相同分类排序下按名称/ID 的契约顺序、版本倒退拒绝、中途插入失败后整批回滚、空目录替换，以及删除目录 Feed 后继续保留本地文章。读取状态、分类和 Feed 使用同一读事务，避免观察到混合版本。

P0-10 新增 13 项假 Worker 同步测试、1 项 SQLite 时间戳条件更新测试和 1 项设置页状态测试：覆盖首次 ACTIVE、admin 强制 ALL、304 不重写目录、登录后立即同步、定时同步、401 refresh 后单次重放、断网、超时、调用方取消、指数退避、服务端超前冲突、200 旧快照、首次错误 304 拒绝、10 MiB 响应上限，以及 UI 最后同步时间/stale 文案。全部失败路径均断言不清空或降级本地快照；组合根测试同时确认账号接口与同步服务共享同一会话实例。

P0-11 新增 31 项 Infrastructure 测试：覆盖默认 HTTPS、HTTP/私网双白名单、非默认端口、环回/私网/链路本地/CGNAT/组播/文档 IPv4、IPv6 loopback/ULA/link-local/documentation/NAT64 私网嵌入、混合 DNS、同主机重定向 DNS rebinding、重定向到私网、循环/次数上限、地址钉住、不可解析域名经指定 IP 的真实本地 TCP 连接、HTML 相对 alternate、候选故障隔离、MIME、压缩头、gzip 解压炸弹、XXE、有效标题后的畸形 XML、调用方取消和总超时。测试断言所有被拒目标均未到达传输层。

P0-12 新增 14 项 Infrastructure 测试与 2 份真实 RSS/Atom fixture：覆盖 RSS 2.0、Atom 命名空间、CDATA、Dublin Core 作者/日期、Atom 作者回退、分类、enclosure、重复外部 ID、缺字段、非法日期、id/guid→URL→Feed 内容指纹身份回退、大小写敏感哈希、签名/身份 query 原序保留、明确追踪参数删除、危险协议丢弃、脚本标题/正文清除、DTD/XXE、畸形/不支持文档、4 MiB 字节上限和 2000 条目上限。组合根测试同时确认 `IFeedParser` 可解析。

P0-13 新增 25 项 Infrastructure 测试：20 项刷新服务/传输测试覆盖 200 先写条目后提交验证器、304 不写条目、无条件 304 拒绝、ETag/Last-Modified、429 Retry-After、5xx 指数退避与 6 小时上限、非法 XML、响应大小、跨 authority 重定向验证器清除、生产地址钉住条件头、总超时、调用方/退出取消、同 Feed 单飞、全局并发 2 的测试配置和跨源故障隔离；3 项 SQLite 抓取状态测试覆盖启用目录投影、到期筛选、条件头/时间/错误往返、upsert 和目录删除竞态；2 项条目写入测试覆盖重复外部 ID 更新及批次中途失败整批回滚。组合根测试同时确认抓取状态仓储、条目写入器和刷新服务均可解析。

P0-14 新增 6 项 Infrastructure 测试：4 项条目仓储测试覆盖同 Feed/外部 ID 幂等更新、唯一 FTS 文档、稳定分页和 `HasMore`、Feed/分类/日期/未读占位筛选、统一搜索结果来源/URL，以及 180 天清理对收藏和标签的保护与 FTS 同步删除；2 项 schema v4→v5 测试覆盖既有 Feed 条目索引回填、三个同步触发器和触发器冲突时回填/版本整批回滚。既有批次写入中途失败测试新增 FTS 零残留断言，旧 schema v1/v2 迁移断言同步提升至 v5。

P0-C 新增 21 项 Infrastructure 测试实例：1 项语料清单断言锁定至少 20 个独立文件、11 个 RSS/9 个 Atom 及中文/异常编码覆盖；20 项参数化解析实例逐份验证文档类型、稳定身份、内容哈希和脚本净化，并单独验证 ISO-8859-1、带 BOM 的 UTF-16 LE/BE、重复 guid 去重和签名 query 保留。该检查点同时复用 P0-11 的 SSRF/XXE/压缩炸弹/重定向绕过拒绝测试，以及 P0-13 的跨源故障隔离、既有缓存保留和巨型响应拒绝测试。

P0-15 新增 4 项 Infrastructure 测试和 9 项 App 测试：写客户端覆盖六类分类/Feed CRUD 请求中的版本头、幂等键和 JSON 映射，401 刷新后保持同一幂等键，409 不自动覆盖，以及普通用户直接构造管理请求仍被 Worker 403 拒绝；ViewModel 覆盖 admin/user 目录隔离、安全发现预览、当前版本写入后同步、409 刷新不重放、写入成功但同步失败锁定、同步中角色降级清空、启停/排序/两步删除和删除后编辑清理；XAML/DI 测试覆盖管理服务解析、全部操作绑定、自动化名称、实时状态与窄窗滚动边界。真实 Worker 管理端点继续复用既有 34/34 workerd/D1 集成结果。

P0-16 新增 2 项 Core、10 项 Infrastructure、4 项 App 和 4 项 Worker 测试实例。Core 预览规划覆盖新增/重复/冲突/无效、Unicode NFKC 分类复用、嵌套分组展平和 80 字符上限；Infrastructure 覆盖 OPML 中文/UTF-16、嵌套组、转义往返、畸形 XML、XXE、2 MiB 上限、原子文件替换，以及批量客户端的 `categoryRef`、版本/幂等头、结果顺序和请求前重复 ID 拒绝；App 覆盖预览零提交、发现后分类+Feed 单批提交、发现失败整批零提交、目录字段白名单导出、DI 和可访问布局绑定。Worker 在真实 D1 中覆盖跨操作分类引用、同批重复回滚、顺序 patch/delete、user 403、101 项拒绝和 100 项成功；完整 Worker 结果提升至 38/38。

P0-17 新增 8 项 App 测试。6 项 ViewModel 测试覆盖 ACTIVE 目录首屏只取 50 条并选中原生阅读模型、分类/Feed/本地日期/关键词组合筛选从 offset 0 重载、第二页稳定追加且 ID 不重复、断网/stale 状态显示最后抓取与目录同步时间、目录版本前进后筛选项与阅读模型同步刷新，以及 10k 假缓存仍只物化首屏并在 2 秒门限内完成。2 项 XAML 测试覆盖独立视图组件、`PagedListBox` Recycling 虚拟化、滚动加载命令、四类筛选自动化名称、原生阅读器绑定和时间线内无订阅编辑按钮；组合根测试同步确认带三项 Feed 依赖的 `NewsCenterViewModel` 可解析。

P0-18 新增 1 项 Infrastructure 和 3 项 App 测试：抓取状态仓储一次查询返回全部 Feed（含停用项）并验证固定错误码不携带响应细节；ViewModel 覆盖管理员状态映射/HTTP 错误脱敏、强制重试结果和异常提示；XAML 覆盖健康 Tab、回收虚拟化、自动化名称、状态字段和安全重试绑定。

P0-19 新增 2 项 Infrastructure 和 4 项 App 测试：Feed 条目查询的 ACTIVE 投影过滤、favorites 计数读取；首页 ViewModel 并行聚合真实 Feed/旧早报/热点/媒体任务/收藏，验证旧早报与 Feed 相同 URL/指纹去重、空库兼容种子、动态状态和 DI；首页 XAML 不再绑定固定更新时间，历史全文搜索过滤旧早报与 Feed 的重复 URL。真实旧 schema v2 数据仍由既有迁移测试覆盖，聚合只读本地缓存并支持离线启动。

P0-20 / P1-01 新增 schema v6 与 5 项状态测试：`user_entry_states` 按 `(entry_id, local_profile)` 隔离，局部 patch 保留未修改的已读/收藏/进度/备注字段，非法进度和过长备注被拒绝；并发局部 patch 不丢独立字段，数据库重开后状态仍可读；旧 Feed 条目清理保护拥有私人状态的条目，schema v2/v4→v6 迁移和重复执行继续通过。Feed 时间线首屏/追加页批量读取状态，并通过异步命令切换已读/收藏、显示进度；状态不上传 Worker，也不改变共享目录版本。

P1-02 新增 3 项 Infrastructure 测试：通用实体收藏备注可更新/批量读取/删除，标签名称 NFKC 规范化和颜色更新，实体标签事务替换、未知/超量标签拒绝，以及删除标签只清理关联而保留收藏备注。

P1-03 时间线编辑切片新增 8 个 App 场景与 1 个 Infrastructure 场景：收藏/备注/标签通过私人仓储往返，备注不隐式收藏，跨 favorites 与 user_entry_states 写入失败会恢复原值，切换条目期间异步写入仍绑定原条目，XAML 覆盖备注和标签输入上限、保存/添加/移除命令、自动化名称和状态反馈；基础仓储覆盖多行备注。时间线批量读取收藏，标签按选中条目异步加载并用代次丢弃过期结果；写入不调用共享目录服务。

P1-03 私人筛选切片新增 1 个 App 场景并扩展 Feed 仓储集成场景：阅读状态、仅收藏和标签条件可组合传入查询，清除操作同时恢复三项默认值；SQLite 在 LIMIT/OFFSET 前应用默认 profile 的已读状态、favorites/旧 is_starred 收藏兼容和实体标签 `EXISTS` 条件，XAML 为三个键盘原生控件提供自动化名称。

P1-03 阅读交互切片新增 2 个 App 场景：首次选择未读条目会持久化已读，用户随后可手动恢复未读；未保存备注在已读/收藏状态更新时不会被覆盖，取消命令只恢复已保存文本且不写仓储。阅读器按钮和列表图标随已读/收藏状态变化，取消按钮具有独立自动化名称。

P1-03 历史页一致入口新增 1 个 App 场景：统一搜索选中 Feed 结果后加载已读/收藏/备注/标签，支持同一组保存、取消和移除动作；非 Feed 搜索结果不开放 Feed 私人状态编辑器，历史右栏提供滚动、键盘原生控件和独立自动化名称。

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

P0 最终检查点第一片新增 `p0-final-acceptance.test.ts`：真实 bootstrap/login 后完成管理员分类与 Feed 发布、目录刷新、普通用户同步/阅读、六类管理员写端点 403 拒绝、Feed 停用后的 ACTIVE/ALL 投影和最小审计字段。目标测试 1/1、Worker typecheck 通过；该证据关闭 P0 最终检查点前两项，OPML/断网/坏源/旧库迁移/10k 汇总及最终文档闸门仍未关闭。

P0 最终检查点第二片复核完整回归：OPML 中文/UTF-16、嵌套分组、畸形 XML/XXE/2 MiB 限制和导出原子替换；Feed 刷新断网、坏源跨源隔离及缓存保留；`SchemaVersionTwoUpgradePreservesExistingDataAndAddsFeedSchema` 的 v2 原位升级；`TenThousandCachedEntriesOnlyMaterializeTheFirstPage` 的 10k 首屏性能。对应 .NET 310/310、Worker 39/39、Worker typecheck 和 Release build 0 警告/0 错误均通过。

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

以下测试需要真实账号、模型或较长运行时间，不在无凭据自动化环境中执行：真实 Groq/DeepSeek 请求、真实 Worker 管理员账号下的目录写入与冲突演练、Cloudflare 生产 D1 并发压测、超长视频全程转写、各代 CPU 的本地大型模型性能、真实 GitHub Release 更新覆盖、已签 Authenticode 的 SmartScreen 声誉、Windows 10/11 多台物理机 100%～200% DPI（含管理页 900×620～4K）矩阵。对应测试步骤已写入规格与发布指南，生产发布前必须执行。
