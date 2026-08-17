# LenxTool 路线图

> [!abstract] 当前焦点
> 以 `docs/PROJECT_GUIDE.md` 第 10 节为当前交付状态唯一准绳；P2-16～P2-19 已完成代码与假 HTTP 自动化，P2-23 已按 Accepted ADR-004 选择 A 关闭。2026-08-17 已完成生产 D1 0001～0011、Worker v2、`TOKEN_SECRET`、`/health`、首管理员和 schema v2/旧客户端策略契约；当前焦点是从全禁用安全基线发布最小真实授权、配置 Desktop v2、完成受控 provider 和签名发布验收。

## 里程碑

- [x] 按 `docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md` 完成 P2-22 的规格、实现、测试与文档闭环。
- [x] 完成 Independent-01 JSON 双栏结构 Diff：单次解析、分块取消、无歧义路径、双栏 UI、虚拟化差异列表和真实最小窗口回归。
- [x] 完成 Readeck、Outline、qBittorrent 与受控 Webhook：schema v2、专用目标/凭据、健康探针、导出器、显式动作及安全回归。
- [x] P2-23 采用 A：不实施邮件摘要、不收集邮箱、不增加云端内容或发信能力，所有 Feed/AI 内容云端保留 0 天。
- [x] 关闭 `SelectionControlsWpfRuntimeTests` 的 `CalendarAutomationPeer` 半初始化误报；当前 10 个 WPF runtime 类逐进程 14/14。
- [x] 创建生产 D1，保存迁移前后恢复证据，完成 0001～0011 与结构复核；发布 Worker v2、注入随机 `TOKEN_SECRET` 并验证公网 `/health` 200。
- [x] 完成一次性首管理员初始化、正常登录与 `/v1/me` ADMIN 身份验证；删除 `BOOTSTRAP_TOKEN` 并确认入口恢复 404。
- [x] 完成生产 schema v2 GET/PUT、强 ETag/条件写、幂等/冲突和旧客户端兼容矩阵；策略版本 2 为九类全禁用、无授权元数据的安全基线。
- [ ] 完成 Readeck、Outline、qBittorrent、Webhook 与既有 provider 的 P2-D 受控真实连通。
- [ ] 完成 Desktop v2、正式签名安装包、升级及跨物理机发布矩阵的独立验收。

## 未决问题

1. P2-D 真实服务验收使用哪些受控实例与测试账号，何时启动？
2. Groq/DeepSeek Provider Secret、正式签名证书/离线更新私钥和跨物理机发布矩阵何时提供？

## 下一步执行顺序

1. 由发布负责人登记四个 provider 的受控实例、版本、账号、精确 endpoint/resource/port、回滚负责人和时间窗；秘密不得进入仓库、日志、截图或聊天。
2. 从九类全禁用的策略版本 2 出发，只为本轮受控对象发布最小权限；再配置 Desktop v2 的非秘密目标和 DPAPI 凭据 marker。
3. 执行 Readeck、Outline、qBittorrent、Webhook 的真实首写/重放/撤销/暂时故障矩阵，保存脱敏证据。
4. P2-D 全部通过后生成签名安装包、便携包和更新清单，完成 Windows 10/11 安装/升级/降级验证，再创建 GitHub Release。

## 权威状态入口

- 当前版本边界与下一里程碑：`docs/PROJECT_GUIDE.md` 第 10 节。
- 完整任务与验收条件：`docs/IMPLEMENTATION_PLAN.md`。
- 最新验证证据：`docs/TEST_REPORT.md`。
