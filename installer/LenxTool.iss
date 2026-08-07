#define AppName "Lenx Tools"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppPublisher "LenxTool"
#define AppExeName "LenxTool.exe"

[Setup]
AppId={{D13CF52E-A89C-4CC6-A888-3CA9F4CCB2B4}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\LenxTool
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\Release
OutputBaseFilename=LenxTool_Setup
SetupIconFile=..\src\LenxTool.App\Assets\LenxTools.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=yes
UninstallDisplayIcon={app}\{#AppExeName}
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
; 正式公开分发时在 CI 中传入 /SMySignTool=... 并取消下一行注释。
; SignTool=MySignTool $f

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "..\Release\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "assets\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "assets\WindowsAppRuntimeInstall-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "正在检查 Microsoft Edge WebView2 Runtime…"; Flags: waituntilterminated skipifdoesntexist
Filename: "{tmp}\WindowsAppRuntimeInstall-x64.exe"; Parameters: "--quiet"; StatusMsg: "正在检查 Windows App Runtime…"; Flags: waituntilterminated skipifdoesntexist
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 用户数据库、模型、设置和 DPAPI 密钥位于 LocalAppData，默认不删除。
Type: filesandordirs; Name: "{app}"
