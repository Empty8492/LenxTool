# 已采用的外部知识

本文件只记录真正影响过当前项目产出的知识指针，不复制来源正文。

| 日期 | 知识 | 来源 | 采用原因 | 应用位置 |
|---|---|---|---|---|
| 2026-08-05 | LenxTool 本地计划窗口幂等与崩溃恢复 | `vault:default/07_Decisions_决策方案/LenxTool 本地计划窗口幂等与崩溃恢复.md` | 采用计划定义与运行窗口分离、明确禁用/在途取消语义的历史约束；以当前代码与失败先行回归重新验证，并把代际取消和最终 SQL 防竞态作为本轮根因级补全 | `LocalScheduledTaskRepository`、`LocalScheduleRunRepository`、`LocalScheduleProcessor` 及回归测试 |
| 2026-08-05 | LenxTool 交付验证与 GitHub 发布闭环 | `vault:default/08_AI_Workflows_AI与工作流/LenxTool 交付验证与 GitHub 发布闭环.md` | 采用“先核对 worktree 与当前路线图、保护相邻改动、历史验证不冒充本轮结果”的流程约束；其中旧提交号和旧状态未作为当前事实 | `AGENTS.md` 的“LenxTool 工作边界” |
| 2026-08-05 | Obsidian 证据分级知识库工作流 | `vault:default/08_AI_Workflows_AI与工作流/Obsidian 证据分级知识库工作流.md` | 作为自动毕业改造的既有流程基线；保留证据分级、分类和不覆盖规则，并明确修订逐次授权边界 | `cairn/knowledge-capture.md`、全局与项目 `AGENTS.md` |
| 2026-08-05 | LenxTool 持久导出队列与安全 Markdown 导出 | `vault:default/07_Decisions_决策方案/LenxTool 持久导出队列与安全 Markdown 导出.md` | 采用真实 SQLite 双仓储、唯一窗口、租约 token、过期接管和陈旧提交拒绝的可恢复状态机约束；仅作为历史设计参考，均由当前代码和测试重新验证 | schema v23、`LocalScheduleRunRepository`、窗口恢复测试 |

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
