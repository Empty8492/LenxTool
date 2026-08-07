# Lenx Tools 项目知识日志

<!-- 最新记录放在此行下方；每条只写摘要、证据和详情指针，控制在约 20 行内。 -->

## 2026-08-08 · P2-22 Windows 通知关闭

- 结果：默认关闭、隐私分级、静默/聚合、受控激活、Runtime 降级、schema v25、设置 UI 与安装资产闸门已完成；下一项为 P2-23 决策，不自动进入云端实现。
- 根因修复：终审发现并关闭 5 项 P1——设置返回后旧标题仍可能投递、Host 早于持久策略恢复、Toast 点击后投影/窗口外角标不同步，以及 Windows App Runtime/WebView2 缓存未经验证即打包。
- 安全边界：系统载荷只有严格 64 位小写 `notification_id`；目标必须重读 SQLite 并映射到通知收件箱、Feed 条目或 AI 报告，不接受 URI。
- 发布边界：两个 Microsoft 安装资产的缓存与下载统一验证固定 SHA-256、有效 Authenticode 和精确发布者；缺少 Inno/离线私钥时只生成开发发布目录，不宣称正式 Setup。
- 补充修复：设置页初始化遇到已生效的相同策略时不再重复应用，避免清空启动突发中待发送的聚合计数。
- 证据：Release Core 191、Infrastructure 763、App 500（1454/1454），Worker 78/78、typecheck、0 警告构建、NuGet/npm audit 0；真实 Windows Toast 与设置页常规/最小窗口通过。
- 详情：`cairn/windows-notifications.md`、`docs/TEST_REPORT.md`、`docs/THREAT_MODEL.md`、`docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md`。

## 2026-08-07 · 项目知识层初始化

- 结果：已建立项目知识目录、机器 profile 关联与自动维护规则。
- 当前焦点：以 `docs/PROJECT_GUIDE.md` 第 10 节为准，下一窄切片为 P2-22 Windows 通知与应用内收件箱。
- 证据：`README.md`、`docs/PROJECT_GUIDE.md`、`docs/IMPLEMENTATION_PLAN.md`。
- 详情：`AGENTS.md`、`.cairn/config.yaml`、`cairn/ROADMAP.md`。
