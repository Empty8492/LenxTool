# 实际采用的外部知识指针

## P2-22 Windows 通知

- [Microsoft：App notifications overview](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/)：采用 App Notifications 当前平台边界与系统设置可用状态。
- [Microsoft：Send a local app notification from a C# app](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet)：采用 WPF/.NET 注册顺序、激活处理和 unpackaged 应用约束。
- [Microsoft：Deploy self-contained apps](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps) 与 [Deploy unpackaged apps](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)：用于选择框架依赖 Windows App SDK、手动 bootstrap 和缺失 Runtime 降级。
- [Microsoft：Windows App SDK downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)：采用 2.3.1 x64 官方 Runtime 安装资产。
- [Microsoft：Distribute your app and the WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)：保留 Evergreen 引导程序并将其作为需固定哈希/签名验证的安装资产。
- `G:\Obsidian\Lenx\07_Decisions_决策方案\LenxTool WPF Shell 与原生控件验收规则.md`：用于最小窗口、真实 WPF 与可访问性验收方式；结论已由当前 XAML、UI Automation 和截图复核。
- `G:\Obsidian\Lenx\07_Decisions_决策方案\LenxTool 本地优先 RSS 架构与安全边界.md`：用于保持本地收件箱为耐久真相、云端不新增内容字段；结论已由当前代码、SQLite 与 Worker schema 复核。
