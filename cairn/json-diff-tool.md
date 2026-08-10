# JSON 双栏结构 Diff

## 当前状态

Independent-01 于 2026-08-10 完成。入口位于“文档与数据”页的“JSON 结构 Diff”页签，复用 Core `JsonToolkit`，不依赖 SQLite、网络、模型服务或 Worker。

## 行为边界

- 左右输入独立校验；一侧错误不会覆盖另一侧状态。
- 差异按 JSON Path 显示新增、删除、修改及左右值；交换输入会使旧结果失效。
- 每侧最多 2,097,152 个字符，最多显示前 500 处差异；恰好 500 处不标记截断，第 501 处才标记。
- 单个值预览最多 2,048 个字符，差异列表使用 WPF 回收虚拟化。
- `JsonException.BytePositionInLine` 是 UTF-8 字节偏移，用户文案不得称为字符“列”。

## 并发与取消约束

比较只使用命令启动时捕获的左右快照并在后台执行。对象与数组递归都必须传播取消令牌；编辑、交换和显式取消均须使当前代际失效。

后台计算完成不代表结果仍可发布：continuation 回到 UI 后必须再次检查取消令牌、任务代际、输入修订和左右快照。输入修订是独立单调计数，不能只比较字符串，否则 A→B→A 会让旧快照伪装成当前输入。

## 回归证据

- Core：路径/类型/值、取消、最大数量、500/501 精确边界。
- ViewModel：独立错误、超限、交换、UTF-8 字节位置、完成后取消、A→B→A。
- 真实 WPF：原生 Automation Peer、Tab 焦点、绑定命令、差异列表、760px、等效 200% 布局与深浅主题。

等效 200% `LayoutTransform` 是稳定组件级证据，不替代正式发布矩阵中的真实 Windows 200% DPI/文本缩放人工观察。仓库既有 `SelectionControlsWpfRuntimeTests` 仍可能在 `CalendarAutomationPeer.GetChildrenCore` 出现环境基线失败；判断 JSON Diff 回归时须独立进程运行其真实 WPF 用例。
