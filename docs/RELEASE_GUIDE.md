# 构建、签名与发布指南

## 版本产物

`scripts\Build-Release.ps1` 生成：

- `Release\publish\`：自包含 win-x64 文件。
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
  -Repository Empty8492/LenxTools
```

脚本会：发布自包含应用、准备 WebView2 引导程序、编译中文安装器、创建 ZIP、签名包和清单、使用公钥反向验证两份签名，最后写出 SHA256SUMS。任何一步失败都会终止发布。

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
- 仓库和发布目录不含私钥、真实 Key、密码、用户媒体或数据库。
- GitHub Release URL、发行说明和最低版本已替换为真实值。
