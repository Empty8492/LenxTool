# LenxTool 路线图

> [!abstract] 当前焦点
> 以 `docs/PROJECT_GUIDE.md` 第 10 节为当前交付状态唯一准绳；P2-16～P2-19 已完成代码与假 HTTP 自动化，P2-23 已按 Accepted ADR-004 选择 A 关闭。生产 D1 0001～0011、Worker v2、`TOKEN_SECRET`、`/health`、首管理员、schema v2/旧客户端策略契约、Desktop v2/qBittorrent 全部真实 canary，以及全仓 formatter 基线均已关闭；生产已恢复策略版本 12 全禁用。当前焦点是先验收受控 Webhook，再完成 Readeck/Outline。

## 里程碑

- [x] 按 `docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md` 完成 P2-22 的规格、实现、测试与文档闭环。
- [x] 完成 Independent-01 JSON 双栏结构 Diff：单次解析、分块取消、无歧义路径、双栏 UI、虚拟化差异列表和真实最小窗口回归。
- [x] 完成 Readeck、Outline、qBittorrent 与受控 Webhook：schema v2、专用目标/凭据、健康探针、导出器、显式动作及安全回归。
- [x] P2-23 采用 A：不实施邮件摘要、不收集邮箱、不增加云端内容或发信能力，所有 Feed/AI 内容云端保留 0 天。
- [x] 关闭 `SelectionControlsWpfRuntimeTests` 的 `CalendarAutomationPeer` 半初始化误报；当前 10 个 WPF runtime 类逐进程 14/14。
- [x] 创建生产 D1，保存迁移前后恢复证据，完成 0001～0011 与结构复核；发布 Worker v2、注入随机 `TOKEN_SECRET` 并验证公网 `/health` 200。
- [x] 完成一次性首管理员初始化、正常登录与 `/v1/me` ADMIN 身份验证；删除 `BOOTSTRAP_TOKEN` 并确认入口恢复 404。
- [x] 完成生产 schema v2 GET/PUT、强 ETag/条件写、幂等/冲突和旧客户端兼容矩阵；契约检查点的版本 2 为九类全禁用基线，当前经 canary 回滚后为版本 12 全禁用。
- [x] 完成 Desktop v2 与 qBittorrent 5.2.3 的健康、magnet、受控/公网 `.torrent`、200/202/409、暂时故障、幂等重放、精确清理和策略撤销 canary；最终策略版本 12 全禁用、target marker 0、DPAPI 测试凭据删除。
- [ ] 完成 Readeck、Outline、Webhook 与既有 provider 的 P2-D 受控真实连通。
- [ ] 完成正式签名安装包、升级及跨物理机发布矩阵的独立验收。
- [x] 关闭全仓 `dotnet format --verify-no-changes` 的既有 encoding/whitespace/import-order 基线，并固定跨 Git 配置的 C# CRLF 契约。

## 未决问题

1. Readeck、Outline、Webhook 使用哪些受控实例与测试账号？
2. Groq/DeepSeek Provider Secret、正式签名证书/离线更新私钥和跨物理机发布矩阵何时提供？

## 下一步执行顺序

1. 先建立无正文持久化的受控 Webhook 接收端，验证 OPTIONS/HMAC/幂等 ack；再登记 Readeck、Outline 的受控实例、账号、精确 endpoint/resource/port、回滚负责人和时间窗。
2. 每轮从九类全禁用的策略版本 12 出发，只为一个受控对象发布最小权限；目标先于 DPAPI secret，结束后恢复全禁用并清理 marker/测试对象。
3. 保存脱敏首写/重放/撤销/暂时故障证据；秘密不得进入仓库、日志、截图或聊天。
4. P2-D 全部通过后生成签名制品、完成 Windows 10/11 与跨机验证并创建 GitHub Release；在固定发布候选上重跑 formatter 与完整门禁。

## 权威状态入口

- 当前版本边界与下一里程碑：`docs/PROJECT_GUIDE.md` 第 10 节。
- 完整任务与验收条件：`docs/IMPLEMENTATION_PLAN.md`。
- 最新验证证据：`docs/TEST_REPORT.md`。
