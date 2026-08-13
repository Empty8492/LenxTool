# LenxTool 路线图

> [!abstract] 当前焦点
> 以 `docs/PROJECT_GUIDE.md` 第 10 节为当前交付状态唯一准绳；P2-16～P2-19 已完成代码与假 HTTP 自动化，P2-23 已按 Accepted ADR-004 选择 A 关闭。当前焦点是 P2-D 受控真实服务、生产 Worker/D1 与签名发布验收，顺序固定为 `0011 migration → Worker v2 → Desktop v2 → provider 矩阵 → 签名制品`。

## 里程碑

- [x] 按 `docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md` 完成 P2-22 的规格、实现、测试与文档闭环。
- [x] 完成 Independent-01 JSON 双栏结构 Diff：单次解析、分块取消、无歧义路径、双栏 UI、虚拟化差异列表和真实最小窗口回归。
- [x] 完成 Readeck、Outline、qBittorrent 与受控 Webhook：schema v2、专用目标/凭据、健康探针、导出器、显式动作及安全回归。
- [x] P2-23 采用 A：不实施邮件摘要、不收集邮箱、不增加云端内容或发信能力，所有 Feed/AI 内容云端保留 0 天。
- [x] 关闭 `SelectionControlsWpfRuntimeTests` 的 `CalendarAutomationPeer` 半初始化误报；当前 10 个 WPF runtime 类逐进程 14/14。
- [ ] 完成 Readeck、Outline、qBittorrent、Webhook 与既有 provider 的 P2-D 受控真实连通。
- [ ] 完成生产 Worker/D1、正式签名安装包、升级及跨物理机发布矩阵的独立验收。

## 未决问题

1. P2-D 真实服务验收使用哪些受控实例与测试账号，何时启动？
2. D1 migration 0011、Worker v2、Desktop v2 的生产部署窗口何时启动？
3. 正式签名安装包、升级与跨物理机发布矩阵何时启动？

## 下一步执行顺序

1. 由发布负责人登记四个 provider 的受控实例、版本、账号、回滚负责人和时间窗；秘密不得进入仓库、日志、截图或聊天。
2. 记录 D1 备份/Time Travel 书签，应用 0011，部署 Worker v2，验证 schema v2、v2 ETag/If-Match、旧客户端投影和升级拒绝。
3. 发布 Desktop v2，执行 Readeck、Outline、qBittorrent、Webhook 的真实首写/重放/撤销/暂时故障矩阵，保存脱敏证据。
4. P2-D 全部通过后生成签名安装包、便携包和更新清单，完成 Windows 10/11 安装/升级/降级验证，再创建 GitHub Release。

## 权威状态入口

- 当前版本边界与下一里程碑：`docs/PROJECT_GUIDE.md` 第 10 节。
- 完整任务与验收条件：`docs/IMPLEMENTATION_PLAN.md`。
- 最新验证证据：`docs/TEST_REPORT.md`。
