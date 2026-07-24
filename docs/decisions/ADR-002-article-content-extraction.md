# ADR-002：全文下载与 DOM 提取保持独立安全边界

## 状态

Accepted

## 日期

2026-07-24

## 背景

P1-07 需要从不可信网页生成可由原生 WPF 阅读器消费的正文块。该能力同时涉及公网下载、编码识别、容错 HTML 解析、正文候选判断和净化；如果直接采用会自行联网的“Readability”封装，P0 已建立的 SSRF、DNS 钉住、重定向、大小和超时控制会被旁路。

.NET 基础类库提供 HTTP、编码和 XML 能力，但没有浏览器兼容的容错 HTML DOM 或正文识别器。因此在写实现前评估了以下方案：

| 方案 | 许可证/维护状态（核对日） | WPF / .NET 10 | AOT | 结论 |
|---|---|---|---|---|
| 只用 .NET 原生字符串/XML | .NET 自带 | 可用 | 不新增依赖 | XML 不能可靠解析畸形 HTML，自写完整 HTML5 tokenizer/DOM 超出本任务且风险高；拒绝 |
| AngleSharp 1.4.0 | MIT；2025-11 发布稳定版，官方仓库持续维护 | 包含 `net8.0`/`netstandard2.0`，可由当前项目引用 | 未把作者级 AOT 保证作为包契约 | NuGet 在核对日将稳定版标注为存在中危公告，当前预发布版也未解除标记；本阶段拒绝 |
| SmartReader | 开源、支持 .NET Standard 2.0，移植 Mozilla Readability | 可由当前项目引用 | 未提供当前项目可验证的作者级保证 | 高层 API 可自行抓网页，图片 API会发 HEAD/下载请求，与 LenxTool 网络安全边界冲突；拒绝 |
| HtmlAgilityPack 1.12.4 | MIT；稳定版 2025-10-03，2026-06 仍有 1.13 beta 发布 | 包含 `net7.0`/`netstandard2.0` 等资产，可由当前 .NET 10 WPF 项目引用 | 包元数据不作为 AOT 保证；当前 WPF 也不支持裁剪 | 仅作为无网络能力的内存 DOM 容错解析器；接受 |

核对来源：

- [HtmlAgilityPack 1.12.4 NuGet 元数据](https://www.nuget.org/packages/HtmlAgilityPack/1.12.4)
- [AngleSharp 1.4.0 NuGet 元数据与安全标记](https://www.nuget.org/packages/AngleSharp/1.4.0)
- [AngleSharp 官方仓库与 MIT/平台说明](https://github.com/AngleSharp/AngleSharp)
- [SmartReader 官方仓库与联网行为说明](https://github.com/Strumenta/SmartReader)
- [.NET Native AOT 限制与分析器说明](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
- [WPF 不支持裁剪的官方仓库记录](https://github.com/dotnet/wpf/issues/4216)

## 决策

1. Core 只定义 `IArticleContentExtractor` 和稳定结果模型，不引用 HtmlAgilityPack，也不返回 DOM/HTML 字符串。
2. Infrastructure 的全文提取器拥有完整流程，但内部严格分层：固定-IP 传输只下载字节；编码层只生成字符串；HtmlAgilityPack 只解析已经在内存中的字符串，不能访问网络。
3. 下载逐跳复用 `FeedNetworkPolicy`，每次重定向重新校验 URI、DNS 和固定地址；同时限制总超时、跳转、下载/解压大小、MIME 和同主机并发。
4. LenxTool 自行选择正文候选并生成类型化白名单块：标题、段落、列表项、引用和图片。链接/图片只接受无凭据的 HTTP/HTTPS；脚本、样式、表单、iframe、object/embed、SVG、导航和注释在提取前移除。
5. 输出带 `article-content-v1` 提取版本和结构化警告。后续算法或依赖变化必须提升版本或明确保持结果兼容。
6. 固定使用 HtmlAgilityPack 1.12.4；合并前运行包含传递依赖的 NuGet 漏洞扫描。本次扫描为 0 个已知易受攻击包。

## AOT 与 WPF 边界

当前应用是 `net10.0-windows` WPF，WPF 官方仍不支持裁剪，而 Native AOT 强制裁剪。因此本阶段的实际门禁是 .NET 10 WPF Release 构建、运行与自动测试，不宣称 Native AOT 支持。

Core 契约保持无第三方类型，下载、解析和正文判断也通过内部边界隔离。若未来迁移到支持 Native AOT 的 UI/宿主，必须重新执行 AOT 分析器和真实 `dotnet publish -p:PublishAot=true` 验证；HtmlAgilityPack 未提供可直接替代该验证的作者级保证。

## 后果

- P1-08 可以直接排队调用一个安全契约，而不接触 DOM 或自行拼装 `HttpClient`。
- 第三方解析漏洞或维护变化只影响 Infrastructure 内部适配器，Core、队列和阅读器模型不必同步改签名。
- 当前正文识别是 LenxTool 的确定性启发式，不复制 Folo/Mozilla Readability 源码；复杂站点需要通过新增合成/获准 fixture 逐步改进。
- 提取结果不是浏览器沙箱，也不保存可执行 HTML；P1-09 仍须把类型化块交给现有原生只读渲染器。
