# 已采用的外部知识

本文件只记录真正影响过当前项目产出的知识指针，不复制来源正文。

| 日期 | 知识 | 来源 | 采用原因 | 应用位置 |
|---|---|---|---|---|
| 2026-08-05 | LenxTool 本地计划窗口幂等与崩溃恢复 | `vault:default/07_Decisions_决策方案/LenxTool 本地计划窗口幂等与崩溃恢复.md` | 采用计划定义与运行窗口分离、明确禁用/在途取消语义的历史约束；以当前代码与失败先行回归重新验证，并把代际取消和最终 SQL 防竞态作为本轮根因级补全 | `LocalScheduledTaskRepository`、`LocalScheduleRunRepository`、`LocalScheduleProcessor` 及回归测试 |
| 2026-08-05 | LenxTool 交付验证与 GitHub 发布闭环 | `vault:default/08_AI_Workflows_AI与工作流/LenxTool 交付验证与 GitHub 发布闭环.md` | 采用“先核对 worktree 与当前路线图、保护相邻改动、历史验证不冒充本轮结果”的流程约束；其中旧提交号和旧状态未作为当前事实 | `AGENTS.md` 的“LenxTool 工作边界” |
| 2026-08-05 | Obsidian 证据分级知识库工作流 | `vault:default/08_AI_Workflows_AI与工作流/Obsidian 证据分级知识库工作流.md` | 作为自动毕业改造的既有流程基线；保留证据分级、分类和不覆盖规则，并明确修订逐次授权边界 | `cairn/knowledge-capture.md`、全局与项目 `AGENTS.md` |
| 2026-08-05 | LenxTool 持久导出队列与安全 Markdown 导出 | `vault:default/07_Decisions_决策方案/LenxTool 持久导出队列与安全 Markdown 导出.md` | 采用真实 SQLite 双仓储、唯一窗口、租约 token、过期接管和陈旧提交拒绝的可恢复状态机约束；仅作为历史设计参考，均由当前代码和测试重新验证 | schema v23、`LocalScheduleRunRepository`、窗口恢复测试 |

## WPF CalendarAutomationPeer 测试宿主

- [dotnet/wpf v10.0.10：CalendarAutomationPeer.GetChildrenCore](https://github.com/dotnet/wpf/blob/v10.0.10/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Automation/Peers/CalendarAutomationPeer.cs#L102-L126)：确认 peer 在 `MonthControl` 存在后直接读取 Previous/Header/Next 三个按钮，不容忍内层模板仍未应用的半初始化状态。
- [dotnet/wpf v10.0.10：CalendarItem.OnApplyTemplate](https://github.com/dotnet/wpf/blob/v10.0.10/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/CalendarItem.cs#L146-L169)：确认三个按钮字段只在 `CalendarItem` 应用模板时从对应 `PART_*` 部件解析；据此把修复限定在测试宿主时序而非产品模板。

## D1 生产迁移

- [Cloudflare workers-sdk #14991](https://github.com/cloudflare/workers-sdk/issues/14991) 与 [修复 PR #15044](https://github.com/cloudflare/workers-sdk/pull/15044)：确认 Windows CRLF 会让包含 SQLite trigger 的远程 D1 migration 以 `incomplete input` 失败；据此采用仓库级 LF 规范和测试前置字节检查，同时保留标准迁移账本流程。
- [Cloudflare Workers Web Crypto](https://developers.cloudflare.com/workers/runtime-apis/web-crypto/) 与 [Workers limits](https://developers.cloudflare.com/workers/platform/limits/)：用于确认原生 PBKDF2 支持、CPU 观测口径和生产运行时边界；远程预览进一步实测 `iterations > 100000` 会被运行时拒绝，因此密码派生固定为 100,000 并由契约测试锁定。

## P2-16～P2-19 外部集成

- [Readeck 官方 bookmarks API](https://codeberg.org/readeck/readeck/src/commit/145a52fcf0db57082c2705f38388471cf303cdf0/docs/api/bookmarks/routes-bookmarks.yaml)：采用 Bearer、label 查询、创建与更新路由；以可见稳定标签实现写前查找与重放收敛。
- [Outline 官方 API 指南](https://docs.getoutline.com/s/guide/doc/api-1rEIXDfLF6) 与 [官方 OpenAPI](https://raw.githubusercontent.com/outline/openapi/main/spec3.json)：采用 Bearer、`documents.info/create/update`、Document collectionId 回执和 `publish` 草稿语义；据此拒绝跨 collection 移动并固定个人草稿。
- [qBittorrent API key 认证](https://github.com/qbittorrent/qBittorrent/wiki/API-Key-Authentication-%28%E2%89%A5v5.2.0%29)：采用 5.2+ API key，避免引入 username/password 与 SID cookie 生命周期。
- [qBittorrent WebUI API](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-5.0%29) 与 [WebAPI changelog](https://github.com/qbittorrent/qBittorrent/blob/master/WebAPI_Changelog.md)：采用显式 category、WebAPI 2.14.1+ add JSON/202 语义及 info-hash/category 写后复核，不把排队或畸形回执误报为完成。

## P2-22 Windows 通知

- [Microsoft：App notifications overview](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/)：采用 App Notifications 当前平台边界与系统设置可用状态。
- [Microsoft：Send a local app notification from a C# app](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet)：采用 WPF/.NET 注册顺序、激活处理和 unpackaged 应用约束。
- [Microsoft：Deploy self-contained apps](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps) 与 [Deploy unpackaged apps](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)：用于选择框架依赖 Windows App SDK、手动 bootstrap 和缺失 Runtime 降级。
- [Microsoft：Windows App SDK downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)：采用 2.3.1 x64 官方 Runtime 安装资产。
- [Microsoft：Distribute your app and the WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)：保留 Evergreen 引导程序并将其作为需固定哈希/签名验证的安装资产。
- `vault:default/07_Decisions_决策方案/LenxTool WPF Shell 与原生控件验收规则.md`：用于最小窗口、真实 WPF 与可访问性验收方式；结论已由当前 XAML、UI Automation 和截图复核。
- `vault:default/07_Decisions_决策方案/LenxTool 本地优先 RSS 架构与安全边界.md`：用于保持本地收件箱为耐久真相、云端不新增内容字段；结论已由当前代码、SQLite 与 Worker schema 复核。

## P2-23 服务端邮件摘要决策闸门

- [Cloudflare Email Service pricing](https://developers.cloudflare.com/email-service/platform/pricing/)、[Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/) 与 [Email Sending Beta 公告](https://developers.cloudflare.com/changelog/post/2026-04-16-email-sending-public-beta/)：采用 Workers Paid 最低费用、每月包含量、超量单价和当前产品阶段作为条件候选基线。
- [Cloudflare Email Service limits](https://developers.cloudflare.com/email-service/platform/limits/)、[suppression lists](https://developers.cloudflare.com/email-service/concepts/suppressions/) 与 [email headers](https://developers.cloudflare.com/email-service/reference/headers/)：用于定义任意收件人前置条件、退信/投诉抑制、反滥用和一键退订最低要求。
- [Cloudflare sending domain configuration](https://developers.cloudflare.com/email-service/configuration/domains/)：确认新发送域默认开启内容预览，预览约保留 7 天；该默认值使“发送但不保留内容”不能靠假设成立。
- [Resend pricing](https://resend.com/docs/knowledge-base/what-is-resend-pricing) 与 [email data retention](https://resend.com/docs/dashboard/webhooks/how-to-store-webhooks-data)：用于比较免费/付费门槛和各计划默认 30 天邮件数据保留。
- [Amazon SES pricing](https://aws.amazon.com/ses/pricing/) 与 [list/subscription management](https://docs.aws.amazon.com/ses/latest/dg/lists-and-subscriptions.html)：用于比较 2026 新账号默认 Essentials、可切换的 $0.10/千封 à-la-carte、订阅管理和抑制能力；未因此选择 AWS。
