# 外部集成的契约与生产可用性边界

## 当前真相

- `EntryIntegrationKind` 是 Worker、客户端和共享策略间的稳定线协议枚举，不是 UI 注册表。通用个人设置只管理固定官方端点的 Readwise；Obsidian、Eagle、Zotero、Readeck、Outline、qBittorrent 与 Webhook 使用专用设置卡和目标存储，Cubox 已取消。
- P2-16～P2-19 已完成代码与假 HTTP 自动化。共享 schema v2 分列保存公网 HTTPS 主机、精确私网 HTTPS `{host,port}`、Outline collection/qBittorrent category 和 qBittorrent loopback HTTP 端口；部署顺序固定为 D1 migration 0011、Worker v2、Desktop v2。
- 2026-08-17 已完成生产 D1 0001～0011、关键结构复核、Worker v2、随机 `TOKEN_SECRET` 和公网 `/health` 200；远端无待迁移且仍为 0 用户。首管理员、策略 v2/旧客户端契约、Desktop v2 与真实 provider 尚未验收，不能把基础部署等同 P2-D 关闭。
- Worker 密码派生受 Cloudflare Web Crypto 生产上限约束，PBKDF2-SHA256 必须固定为 100,000 次；本地 Miniflare 会接受生产拒绝的更高值，因此该参数属于显式平台契约，必须同时由测试与远程预览验证。
- 旧客户端只取得兼容投影；advanced-only ACTIVE 项不下发。存在高级约束时，旧 ALL/PUT 失败关闭并要求升级。管理员客户端只有加载到 schema v2 才允许发布，避免 Worker-first 窗口丢字段。
- 每种新 provider 首版只保存一个本机 `default` 目标。资源白名单按 kind 全局授权，ACTIVE endpoint/resource 元数据会下发同一 Worker 的登录账号，因此只适用于同一信任域并被视为非秘密；凭据、完整 Webhook 路径、条目内容和完整 magnet 不进入 D1。
- 凭据存储仍为 Windows DPAPI CurrentUser。专用设置先保存 `CredentialVersion=0` 的非秘密目标，再写秘密，最后提交 marker 1；删除先降为 marker 0，再删秘密。marker 0、无目标文档或 endpoint 变更后遗留的 `kind/default` 秘密仍可从专用卡显式删除，但绝不会自动激活。
- 健康检查的单一 deadline 覆盖策略授权、DNS、凭据读取和 provider probe；策略、endpoint 与全部 DNS 地址先于秘密读取。真实导出另重验 Outline collection 或 qBittorrent category，并使用禁代理、跳转、Cookie、自动解压和地址钉住的有界客户端。
- Readeck 以可见 `lenxtool:<stable-id>` 标签收敛重放。只有搜索结果数组为空且单一合法 `Total-Count=0` 才创建；分页、计数或 label 投影不一致时可重试失败关闭，绝不再次 POST。
- Outline UUIDv5 绑定不透明 queue target 修订与 entry ID。同一 endpoint/collection 更新同一草稿；切换目标或 collection 创建另一份确定性草稿，不移动旧文档。首版固定 `publish=false`，由用户在 Outline 内复核后发布。
- qBittorrent 固定 5.2+ / WebAPI 2.14.1+ API key、非空已存在 category、唯一 BTIH 或经验证的 2 MiB `.torrent`。投递确认绑定准备时的目标修订；add 前后按 info-hash/category 查询，202、畸形回执或未观察到实际落地不会标记完成。localhost HTTP 只允许管理员批准的精确端口，并在 UI 明示 API key 经本机 TCP 明文传输。
- Webhook 只支持固定 v1 JSON、OPTIONS 能力声明、稳定 `Idempotency-Key`、精确 `LenxTool-Ack` 和可选实际 UTF-8 正文 HMAC；不开放自定义方法、Authorization、请求头或正文模板。
- P2-23 已采用 ADR-004 A：不实现邮件摘要、不收集邮箱、不增加云端文章表、邮箱字段、供应商凭据或发信代码。

## 防复发约束

1. 新增枚举值不能自动生成个人凭据入口；生产可用性必须由专用目标契约、无副作用探针、exporter、DI 和回归共同证明。
2. 网络授权顺序固定为 ACTIVE policy → endpoint/port/resource → 全 DNS 地址与 pin → credential → provider protocol；DNS 或策略失败不得读取秘密。
3. HTTP deadline 必须覆盖响应头和正文；读超时映射暂时不可用，成功写后的超时、畸形或不一致回执映射未知写结果，不能永久失败或虚报完成。
4. 幂等查找只有在第三方明确证明不存在时才允许创建；分页、计数、重复响应头、身份或 resource 不一致都失败关闭。
5. 目标修订必须进入队列作用域；确认型操作还必须绑定准备时修订。endpoint/resource 改变后旧任务不能静默投向新目标。
6. marker 1 是凭据激活权限，DPAPI 槽位存在本身不是权限；删除 marker 必须先于秘密删除，崩溃或旁路重写不能重新激活。
7. Worker 表示版本、ETag、`If-Match`、幂等哈希和数据库列预算必须按同一 schema 演进；不能只加顶层版本号并假设旧客户端安全忽略字段。
8. P2-23 若未来重开，必须另立 ADR 并重新评估邮箱、内容保留、退订、反滥用、删除与供应商边界，不能复用本次 A 结论扩权。

## 当前阻断

- 代码与假 HTTP 契约无剩余 P0/P1。生产 D1 migration 与 Worker v2 基础部署已完成；尚未完成的是首管理员、策略 schema v2/兼容契约、Desktop v2、真实 Readeck/Outline/qBittorrent/Webhook 受控连通、签名安装包和跨物理机升级矩阵。
- 自动化完成不等于第三方真实实例或生产发布完成；在 P2-D 前不得宣称端到端生产验收通过。

## 回归证据

- 2026-08-13 完整门禁：Core 222/222、Infrastructure 811/811、App 非 WPF 523/523、10 个 WPF runtime 类逐进程 14/14、Worker 81/81、strict typecheck、Release build 0 警告/0 错误、NuGet/npm 漏洞 0。
- 2026-08-17 重新运行同一发布门禁并保持上述结果；11 个 D1 migration 另通过 LF-only 前置检查。远端 11/11、三组 0011 列、0008 表与四个触发器均已只读核对。
- 集成终审使用 `gpt-5.6-sol`（max）只读复核，最终未发现剩余 P0/P1；本轮 56 个改动 C# 文件的格式验证与 `git diff --check` 通过。
- 受控真实实例、token/API key 和 Webhook 接收端均未使用；详细当前状态与历史门禁见 `docs/TEST_REPORT.md` 和 `docs/plans/RSS_P2_VIEWS_INTEGRATIONS.md`。
