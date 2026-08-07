# Windows 通知当前真相

> [!info] 状态
> P2-22 于 2026-08-08 关闭。应用内收件箱是耐久真相，Windows Toast 是默认关闭、可丢失、可降级的本地投递通道。

## 数据与路由

- SQLite 当前为 schema v25。`app_notifications` 保存三类通知、创建/已读状态和 `NONE`、`FEED_ENTRY`、`AI_REPORT` 封闭目标，不保存正文、摘要结果、异常详情或 URI。
- v24→v25 在事务内重建通知表；旧系统健康通知映射为无目标，其余旧通知映射为 Feed 条目，失败不提升版本。
- 系统激活参数必须且只能包含 `notification_id`，值为 64 位小写十六进制。应用按 ID 重读 SQLite，再映射到 `notifications`、`feed_entry` 或 `ai_report`；未知类别、附加参数、URI、大小写或畸形 ID 失败关闭。
- 标记已读会发布真实 unread→read delta。铃铛最近只保留 50 条，但全表 `UnreadCount` 对窗口外 Toast 点击仍必须递减；重复点击保持幂等。

## 隐私与生命周期

- 默认：`Enabled=false`、`GenericOnly`、静默 22:00～07:00、聚合 15 分钟。允许的聚合值只有 0/5/15/30/60；损坏或未知版本设置回到同一安全默认值。
- 通用提示不显示标题、来源或正文；标题模式只显示受限标题，不显示来源/正文。锁屏展示由 Windows 设置决定，应用不宣称能单独控制。
- 业务先写 SQLite，再经容量 128 的有界通道尽力投递；Toast 失败不能回滚业务或收件箱。数据库和通知设置在 Host 生产者启动前恢复，初始化前事件按持久策略处理。
- 设置保存与最终适配器投递共享 `_deliveryGate`，并在 `Show` 前重验当前策略。禁用或从标题降级返回后，不允许在途旧决策继续泄露标题。
- 摘要完成通知只在原子报告/窗口提交真正胜出后发布；确定性缓存命中会幂等补发，修复提交后、通知前崩溃窗口。

## Runtime 与发布

- Windows App SDK 使用框架依赖、手动惰性 bootstrap；Runtime 缺失、注册失败或系统禁用时只关闭系统通知，主窗口和应用内收件箱仍启动。
- 安装器携带 Windows App Runtime 2.3.1 x64。WebView2 与 Windows App Runtime 的缓存复用和首次下载都必须在 Inno 编译前通过固定 SHA-256、Authenticode `Valid` 和 Microsoft 精确发布者校验。
- 当前固定哈希：WebView2 `23A55FBFF920C0F99887848CFC25125F8F915DF35638E01BEB8F8FA9B5A0BC51`；Windows App Runtime `4011748DDF472B7E856D909FDFB4E9B19C3D23FCD8121039AC91F99D5FFA65DB`。升级资产必须显式轮换哈希和测试，不能移除闸门。
- Inno 简体中文语言文件虽不可执行，也会改变安装提示，固定 SHA-256 为 `869E43E7C7B8D20C7E4397C8E98F7D1B7CF0528803ACDF019AD350143EC85469`；下载后必须在编译前验证。

## 防复发验证

- 设置页初始化若读取到已生效的相同策略，不得再次调用 `ApplySettings`；否则会无意义地清空启动突发中尚待发送的聚合计数。
- Release 串行全量：Core 191/191、Infrastructure 763/763、App 500/500；Worker 78/78、strict typecheck；Release build 0 警告/0 错误；NuGet/npm audit 0。
- 真实本机：Windows 通知 `Available` 且 `Show` 成功；设置页在常规和 920×620 最小窗口可访问。
- 正式发布仍需 Windows 10/11、前后台/冷启动 Action Center、权限拒绝、锁屏、安装版/便携版 Runtime 缺失与已签安装包矩阵。单机开发冒烟不能关闭这些发布项。
