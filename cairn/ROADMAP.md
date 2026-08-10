# LenxTool 路线图

> [!abstract] 当前焦点
> 以 `docs/PROJECT_GUIDE.md` 第 10 节为当前交付状态唯一准绳；Independent-01 已关闭，P2-23 保持 Proposed ADR-004，当前等待产品负责人选择 A/B/C。

## 里程碑

- [x] 按 `docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md` 完成 P2-22 的规格、实现、测试与文档闭环。
- [x] 完成 Independent-01 JSON 双栏结构 Diff：有界/可取消 Core、双栏 UI、交换、虚拟化差异列表和真实 WPF 回归。
- [x] 为 P2-23 完成隐私、保留、成本、退订、反滥用、删除与版权决策草案；建议当前不实施，所有内容云端保留 0 天。
- [ ] 等待产品负责人批准 ADR-004 的 A/B/C 选择；批准前不创建实现计划，不增加云端文章表、邮箱字段或邮件发送代码。
- [ ] 完成生产 Worker/D1、正式签名安装包、升级及跨物理机发布矩阵的独立验收。

## 未决问题

1. 产品负责人是否批准 ADR-004 的 A（当前不实施，建议）、B（仅元数据提醒）或 C（另立内容聚合 ADR）？
2. P2-16～P2-19 中哪些能力进入后续实施序列？
3. 生产 Worker/D1 与正式签名发布的受控验收何时启动？

## 权威状态入口

- 当前版本边界与下一里程碑：`docs/PROJECT_GUIDE.md` 第 10 节。
- 完整任务与验收条件：`docs/IMPLEMENTATION_PLAN.md`。
- 最新验证证据：`docs/TEST_REPORT.md`。
