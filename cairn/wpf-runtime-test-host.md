# WPF 运行时测试宿主与嵌套模板时序

## 当前真相

- `SelectionControlsWpfRuntimeTests` 曾在 `Calendar.UpdateLayout()` 中触发 `CalendarAutomationPeer.GetChildrenCore` 的空元素异常。当前代码与官方 WPF v10.0.10 源码共同证明：产品 `CompactCalendarItemStyle` 完整声明了 Previous/Header/Next 三个必需部件；失败来自测试只应用外层 `Calendar` 模板便强制布局，内层 `CalendarItem.OnApplyTemplate` 尚未沿自然 Loaded/布局流程解析这些部件。
- 修复只改变真实 WPF 测试宿主：不手动调用任何 `ApplyTemplate()`，等待生产 `DatePicker` 的 `CalendarItem` 和三个导航部件自然加载，再显式调用 `CalendarAutomationPeer.GetChildren()`，核对三个导航 peer 及包含日期单元的完整子树；原有键盘、日期回写、窄窗、等效 200% 和深浅主题验收继续保留。
- 不需要修改产品 XAML、绕过 Automation、捕获框架异常或降低断言；此前把该问题长期归类为“环境基线”不够准确。

## 防复发约束

1. 对含嵌套模板的原生 WPF 控件做 Automation 验收时，不在内层 Loaded 前手动强制外层布局或模板；等待真实布局完成后，直接查询 Automation 子树来验证官方模板契约。
2. `CalendarAutomationPeer.GetChildrenCore` 空元素异常不能直接归因于产品模板或环境；先区分“模板缺件”和“部件存在但内层模板尚未应用”。
3. 不以删除 Automation Peer 断言、吞异常、固定等待或跳过用例作为修复。WPF runtime 继续串行或逐类独立进程运行，结构测试不能替代真实控件树。

## 回归证据

- RED：当前 `main` 的精确 `SelectionControlsWpfRuntimeTests` 在第 124 行稳定失败，调用栈进入 `CalendarAutomationPeer.GetChildrenCore`。
- GREEN：不手动应用模板的真实打开路径显式查询完整 Calendar Automation 子树，精确用例 1/1、独立进程连续 10/10；10 个 WPF runtime 类逐进程共 14/14；完整 App 522/522。
- 相关 Release 门禁：Infrastructure 首轮性能预算噪声精确复跑 1/1、独立全量 763/763；最终全解决方案 App 522、Core 202、Infrastructure 763，共 1487/1487；Release build 0 警告/0 错误。

## 外部依据

- `cairn/Cited.md` 的“WPF CalendarAutomationPeer 测试宿主”。
