# LenxTool 日/周订阅摘要的耐久边界

## 当前结论

- P2-21 使用两个已发布的稳定 GUID 分别表示日摘要与周摘要。任务按计划时区读取上一个本地日历日/周，不用固定 24/168 小时代替日历边界。
- 运行查询始终强制 ACTIVE 目录，再叠加所选 Feed/分类和最多 200 字关键词。候选最多 200，去重后最多 40，单条正文 1,200 字符，总模型源 16,000 字符。
- 内容哈希和报告 ID 只覆盖真正进入模型的截断后输入，并包含计划、范围、窗口、模型和 prompt 版本。空窗口不调模型也不写占位报告；确定性报告已存在时不重复调用。

## schema v24 与原子性

- `local_scheduled_task_payloads` 将版本化摘要范围与 `local_scheduled_tasks` 在同一 `BEGIN IMMEDIATE` 事务中保存。相同 `updated_at` 的幂等身份包含日历字段与载荷，不允许多进程拼出“计划 A + 范围 B”。未知载荷版本和损坏 JSON 失败关闭。
- `local_schedule_run_retries` 与 PENDING 释放在同一事务保存 `retry_not_before`。未到时间的旧窗口不参与领取，其他计划仍可执行；新 owner 领取时删除退避行。
- `FeedDigestExecutionStore` 在成功路径中同事务验证租约令牌、租约到期时间和计划代际，然后共同写入 `ai_reports`/FTS、请求 COMPLETED 和窗口 COMPLETED。如果计划代际已变，请求记为 DISCARDED、窗口取消且不落报告。

## 外部模型防重与重试

供应商没有提供客户端幂等键时，本地系统无法在请求已发送后判断外部是否已计费/生成结果。因此不能同时承诺“崩溃后必定产出”和“绝不重复计费”。当前约束是：

1. 发网前先在 `feed_digest_requests` 写入 STARTED。
2. 进程崩溃、网络/超时、5xx、无效成功响应、无法证明结果的取消，以及恢复时看到的过期 STARTED，都收敛为 AMBIGUOUS 并终止自动重放。
3. 这是 at-most-once 选择：可能跳过一次摘要，但不会因本地恢复再发一次外部请求。
4. 只有可以明确证明未生成可保存结果的安全 4xx/429 才清除 STARTED。明确可重试的 429 使用 Delta 或 HTTP-date 形式的 Retry-After，并由通用处理器限制在轮询周期至一天内；永久 4xx 直接取消窗口，不释放回 PENDING。

## 防复发约束

- 不要把“可以安全清除防重标记”等同于“应该自动重试”。前者描述外部结果确定性，后者由 `AppError.IsRetryable` 决定。
- 不要在业务报告落库之后才单独调用通用 Complete；任何带计划代际语义的业务结果都必须与窗口终态共享原子提交。
- 不要用 `updated_at` 伪装最早重试时间；真实状态时间与调度资格必须分开存储。
- 不要将结果不明的网络/超时/5xx 当作安全重试，除非供应商后续提供了经核验的幂等契约。

## 验证证据

- P2-21 相对 P2-20 新增 40 个自动化用例；最终 Core 191/191、Infrastructure 755/755、App 非 WPF 427/427，共 1373 个通过。
- 真实 SQLite 测试覆盖了原子配置、跨重启退避、报告/FTS/窗口提交、代际丢弃、过期 STARTED 抑制和安全失败清理。假 HTTP 测试覆盖 Retry-After Delta/HTTP-date 和不确定失败；未用真实 DeepSeek Key 发起外部请求。
- 两轮独立审查促成 5 项 P1 和 1 项 P2 修复；最终复核无剩余 P0/P1/P2。
- 完整证据见 `docs/ARCHITECTURE.md`、`docs/THREAT_MODEL.md`、`docs/TEST_REPORT.md` 和 `docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md`。
