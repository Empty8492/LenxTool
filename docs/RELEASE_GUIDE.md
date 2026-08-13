# 构建、签名与发布指南

## 版本产物

`scripts\Build-Release.ps1` 生成：

- `Release\publish\`：自包含 .NET 的 win-x64 文件；Windows App SDK Runtime 仍为系统依赖。
- `Release\LenxTool_Setup.exe`：Inno Setup 安装包。
- `Release\LenxTool_Portable_win-x64.zip`：便携版。
- `Release\update-payload.json`：待签名清单正文。
- `Release\update-manifest.json`：客户端读取的签名 envelope。
- `Release\package-signature.txt`：安装包 hash 签名。
- `Release\SHA256SUMS.txt`：公开下载校验值。

## 离线密钥

首次发布在仓库外执行：

```powershell
dotnet run --project tools\LenxTool.ReleaseTool -- keygen D:\Offline\lenxtool-private.pem installer\update-public-key.pem
```

私钥不得提交、复制到 Release、写入 CI 普通变量或放在客户端。正式流程建议使用离线签名机或受保护的硬件/密钥库。公钥作为 EmbeddedResource 编译进客户端。

## 构建发布

安装 .NET 10 SDK 与 Inno Setup 6，然后执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 `
  -Version 0.1.0 `
  -PrivateKeyPath D:\Offline\lenxtool-private.pem `
  -Repository Empty8492/LenxTool
```

脚本会：发布自包含 .NET 应用，准备 WebView2 引导程序与 Windows App Runtime 2.3.1 x64，编译中文安装器，创建 ZIP，签名包和清单，使用公钥反向验证两份签名，最后写出 SHA256SUMS。任何一步失败都会终止发布。

两个 Microsoft 安装资产都使用仓库中显式固定的 SHA-256，并要求 Authenticode 状态为 `Valid`、发布者精确为 Microsoft Corporation。校验无条件发生在缓存复用或首次下载之后、Inno Setup 编译之前；哈希不匹配不能通过“重新下载”或删除校验绕过。升级资产时必须从 Microsoft 官方来源取得新文件，人工复核版本、哈希和签名后再更新常量与回归测试。

会改变安装界面文本的 `ChineseSimplified.isl` 虽不是可执行文件，也必须匹配仓库固定 SHA-256；文件缺失时的下载同样在 Inno 编译前验证。更新翻译要显式审阅内容并同步哈希测试。

安装版会静默运行两项依赖安装器。便携 ZIP 不包含 Windows App Runtime 安装器：目标机器缺失 Runtime 时，Lenx Tools 主窗口和应用内通知收件箱仍能工作，但 Windows 系统通知会显示为不可用。该降级不等于安装版依赖验收通过。

## P2-D 之后的正式发布顺序

只有 P2-D 四个 provider 的真实矩阵全部通过，才进入本节。发布负责人应使用已推送的候选 commit，不在构建过程中修改策略、迁移或 provider 代码。

1. **预检：** 确认 `dotnet build LenxTool.slnx -c Release --no-restore`、完整 .NET/Worker 测试、strict typecheck、NuGet/npm 审计、`dotnet format --verify-no-changes` 与 `git diff --check` 通过；确认工作树没有真实凭据、数据库、私钥或旧制品。
2. **生成：** 在仓库外准备 ECDSA 更新私钥和 Authenticode 证书，安装 Inno Setup 6，按本指南的 `Build-Release.ps1` 命令生成自包含目录、安装包、便携 ZIP、更新清单和 SHA256 文件。所有 Microsoft 资产必须在打包前通过固定哈希、Authenticode 和发布者检查。
3. **安装验收：** 在 Windows 10/11 x64 受控虚拟机或实体机分别验证全新安装、启动登录、P2-D 设置、覆盖升级、卸载后保留用户数据、缺少 Windows App Runtime 时只禁用系统 Toast，以及更新清单签名/哈希反向验证。
4. **发布留证：** 保存源码 commit、版本标签、制品 SHA256、签名验证结果、安装/升级结果和脱敏测试报告；GitHub Release 至少上传 Setup、Portable ZIP、`update-manifest.json` 与 `SHA256SUMS.txt`。旧 `Release\LenxTool_Setup.exe` 不得复用为新版本。
5. **回滚：** 安装/更新签名或升级验证任一失败时，不发布标签或 Release；保留上一版制品和用户数据，修复后重新生成全套产物，不覆盖已发布文件。

## GitHub Releases

创建 `v{version}` Release，至少上传：

- `LenxTool_Setup.exe`
- `LenxTool_Portable_win-x64.zip`
- `update-manifest.json`
- `SHA256SUMS.txt`

将 App 的 `UpdateOptions.ManifestUris` 指向稳定下载地址。清单 mirrors 可同时加入 GitHub、阿里云 OSS、腾讯云 COS；客户端按顺序尝试 HTTPS 地址。

## Authenticode

ECDSA 发布签名保护应用自己的更新协议，但不会消除 Windows SmartScreen 提示。公开分发前需要购买可信代码签名证书，对 `LenxTool.exe`、关键 DLL 和 `LenxTool_Setup.exe` 做 Authenticode 时间戳签名。Inno 脚本已预留 `SignTool`。

## 覆盖升级验证

1. 安装 N 版本并写入测试 Key、数据库记录和导入模型。
2. 用相同 AppId 构建 N+1，静默覆盖安装。
3. 验证 `%LocalAppData%\LenxTool` 内容未变，数据库迁移前生成备份。
4. 验证 N+1 启动、清单强制更新逻辑和卸载。
5. 卸载后确认用户数据仍存在。

## 发布前清单

- Release 编译 0 warnings。
- .NET、Worker 和依赖漏洞检查通过。
- 安装/启动/卸载与覆盖升级通过。
- 中文用户名、空格路径、断网、取消、429、数据库损坏/迁移测试通过。
- 清单与安装包签名反向验证通过。
- WebView2 与 Windows App Runtime 资产的固定哈希、Authenticode、Microsoft 发布者及打包前顺序检查通过。
- 安装版/便携版分别验证 Runtime 已安装与缺失路径；系统通知缺失时必须只降级 Toast，不能阻止主程序启动。
- 仓库和发布目录不含私钥、真实 Key、密码、用户媒体或数据库。
- GitHub Release URL、发行说明和最低版本已替换为真实值。
