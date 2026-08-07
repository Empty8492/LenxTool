# 实际采用的外部知识指针

## P2-22 Windows 通知

- [Microsoft：App notifications overview](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/)：采用 App Notifications 当前平台边界与系统设置可用状态。
- [Microsoft：Send a local app notification from a C# app](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet)：采用 WPF/.NET 注册顺序、激活处理和 unpackaged 应用约束。
- [Microsoft：Deploy self-contained apps](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps) 与 [Deploy unpackaged apps](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)：用于选择框架依赖 Windows App SDK、手动 bootstrap 和缺失 Runtime 降级。
- [Microsoft：Windows App SDK downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)：采用 2.3.1 x64 官方 Runtime 安装资产。
- [Microsoft：Distribute your app and the WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)：保留 Evergreen 引导程序并将其作为需固定哈希/签名验证的安装资产。
- `G:\Obsidian\Lenx\07_Decisions_决策方案\LenxTool WPF Shell 与原生控件验收规则.md`：用于最小窗口、真实 WPF 与可访问性验收方式；结论已由当前 XAML、UI Automation 和截图复核。
- `G:\Obsidian\Lenx\07_Decisions_决策方案\LenxTool 本地优先 RSS 架构与安全边界.md`：用于保持本地收件箱为耐久真相、云端不新增内容字段；结论已由当前代码、SQLite 与 Worker schema 复核。

## P2-23 服务端邮件摘要决策闸门

- [Cloudflare Email Service pricing](https://developers.cloudflare.com/email-service/platform/pricing/)、[Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/) 与 [Email Sending Beta 公告](https://developers.cloudflare.com/changelog/post/2026-04-16-email-sending-public-beta/)：采用 Workers Paid 最低费用、每月包含量、超量单价和当前产品阶段作为条件候选基线。
- [Cloudflare Email Service limits](https://developers.cloudflare.com/email-service/platform/limits/)、[suppression lists](https://developers.cloudflare.com/email-service/concepts/suppressions/) 与 [email headers](https://developers.cloudflare.com/email-service/reference/headers/)：用于定义任意收件人前置条件、退信/投诉抑制、反滥用和一键退订最低要求。
- [Cloudflare sending domain configuration](https://developers.cloudflare.com/email-service/configuration/domains/)：确认新发送域默认开启内容预览，预览约保留 7 天；该默认值使“发送但不保留内容”不能靠假设成立。
- [Resend pricing](https://resend.com/docs/knowledge-base/what-is-resend-pricing) 与 [email data retention](https://resend.com/docs/dashboard/webhooks/how-to-store-webhooks-data)：用于比较免费/付费门槛和各计划默认 30 天邮件数据保留。
- [Amazon SES pricing](https://aws.amazon.com/ses/pricing/) 与 [list/subscription management](https://docs.aws.amazon.com/ses/latest/dg/lists-and-subscriptions.html)：用于比较 2026 新账号默认 Essentials、可切换的 $0.10/千封 à-la-carte、订阅管理和抑制能力；未因此选择 AWS。
