# 外部集成的契约与生产可用性边界

## 当前真相

- `EntryIntegrationKind` 是 Worker、客户端和共享策略间的稳定线协议枚举，不是生产适配器注册表。
- 个人通用设置采用显式生产 allowlist；当前只包含已注册 exporter 与健康探针的 Readwise，并固定 `Readwise/default` 和 `https://readwise.io/`。
- Obsidian、Eagle、Zotero 使用各自的专用设置卡；Cubox 已取消。Readeck、Outline、qBittorrent 与 Webhook 只保留协议/策略值，P2-16～P2-19 均未完成。
- 升级前保存的占位类型配置只生成非秘密清理指针。ViewModel 不按旧槽位取值，旧值不会返回界面或交给探针/exporter，也不会自动删除；用户显式删除当前记录时会规范化仍匹配的旧目标，避免下次启动重新生成提示。
- 生产凭据存储使用共享 DPAPI blob；刷新当前 Readwise presence 或删除槽位时，存储层可能解密整个 blob。安全承诺是旧值不离开存储层、不进入 ViewModel/UI、探针、exporter 或网络，不是“进程从不解密旧值”。
- 历史版本可先后保存多组任意 `(kind, TargetId)` 槽位，但槽名只有哈希且存储接口不可枚举。当前提供受控的只删除入口；用户必须仍知道原类型和 TargetId，遗忘标识的槽位无法反推。

## 防复发约束

1. 新增或保留枚举值不能自动生成个人凭据入口。
2. 只有生产 exporter、无副作用健康探针、安全目标契约和回归测试同时就绪后，才能把类型加入个人设置 allowlist。
3. 不受支持的持久化类型或程序化赋值必须回到安全默认值，不能沿用旧 TargetId、Endpoint、凭据槽位或发起探测。
4. 公共枚举兼容性与客户端可用性分开演进；隐藏未接通入口不等于删除线协议，也不等于完成对应 P2 项。
5. 保存已接通适配器不得顺带删除遗留槽位；遗留凭据的销毁必须是用户可见、目标明确的显式动作。
6. 删除当前遗留记录时，目标规范化必须可重放：先写安全的 Readwise kind，再写 TargetId/Endpoint，全部成功后才能清除遗留指针；部分写入后重试仍须识别自身迁移状态。
7. 手工清理旧槽位必须保持 delete-only，不得恢复未接通类型的保存、读取、探测或 exporter 入口，也不得声称能枚举或反推哈希槽名。

## 当前阻断

- Readeck 创建书签缺少可证明的客户端幂等键或外部 ID；在重放契约获批前不得注册凭据、探针或 exporter。
- Outline、qBittorrent 与 Webhook 尚未完成产品选择、威胁边界和专项验收，不应以占位 UI 代替实现。

## 回归证据

- `IntegrationViewModelTests` 覆盖个人列表唯一生产类型、四种未接通类型的程序化赋值拒绝、旧配置安全回退、当前遗留槽位直接删除后不复生、设置逐键失败后的安全重试，以及只删除历史未引用槽位并保留其他槽位；Fake 调用记录额外锁定旧槽位没有 Get/Exists/Set，手工清理期间也没有设置写回或健康探测。
- `IntegrationLayoutTests` 固定界面提示：未接通类型不会显示、保存凭据或发起外部请求；旧值不会返回界面或交给适配器；当前记录和用户指定的旧类型/TargetId 都有显式清理入口。
- 2026-08-10 新鲜门禁：聚焦 25/25、App 非 WPF 508/508、Core 202/202、Release build 0 警告/0 错误、NuGet 漏洞 0；WPF 逐类 13/14，唯一失败仍是未改动的 SelectionControls/CalendarAutomationPeer 基线。
