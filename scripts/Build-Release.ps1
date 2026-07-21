param(
    [string]$Version = "0.1.0",
    [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
    [string]$Repository = "Empty8492/LenxTools"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ReleaseDir = Join-Path $ProjectRoot "Release"
$PublishDir = Join-Path $ReleaseDir "publish"
$InstallerAssets = Join-Path $ProjectRoot "installer\assets"
$ReleaseTool = Join-Path $ProjectRoot "tools\LenxTool.ReleaseTool\LenxTool.ReleaseTool.csproj"
$SetupPath = Join-Path $ReleaseDir "LenxTool_Setup.exe"
$PortablePath = Join-Path $ReleaseDir "LenxTool_Portable_win-x64.zip"
$PayloadPath = Join-Path $ReleaseDir "update-payload.json"
$ManifestPath = Join-Path $ReleaseDir "update-manifest.json"
$PublicKeyPath = Join-Path $ProjectRoot "installer\update-public-key.pem"

if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
    throw "Offline update private key not found: $PrivateKeyPath"
}

New-Item -ItemType Directory -Force -Path $ReleaseDir, $InstallerAssets | Out-Null
if (Test-Path -LiteralPath $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}

dotnet publish (Join-Path $ProjectRoot "src\LenxTool.App\LenxTool.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
dotnet build $ReleaseTool -c Release
if ($LASTEXITCODE -ne 0) { throw "Release tool build failed" }

$WebViewBootstrapper = Join-Path $InstallerAssets "MicrosoftEdgeWebview2Setup.exe"
if (-not (Test-Path -LiteralPath $WebViewBootstrapper)) {
    Invoke-WebRequest -UseBasicParsing `
        -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" `
        -OutFile $WebViewBootstrapper
}

$ChineseLanguage = Join-Path $ProjectRoot "installer\ChineseSimplified.isl"
if (-not (Test-Path -LiteralPath $ChineseLanguage)) {
    Invoke-WebRequest -UseBasicParsing `
        -Uri "https://raw.githubusercontent.com/kira-96/Inno-Setup-Chinese-Simplified-Translation/main/ChineseSimplified.isl" `
        -OutFile $ChineseLanguage
}

$Iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $Iscc) { throw "Inno Setup 6 is required. Install it, then rerun this script." }

& $Iscc "/DAppVersion=$Version" (Join-Path $ProjectRoot "installer\LenxTool.iss")
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $SetupPath)) { throw "Inno Setup compilation failed" }

if (Test-Path -LiteralPath $PortablePath) { Remove-Item -LiteralPath $PortablePath -Force }
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $PortablePath -CompressionLevel Optimal

$SignaturePath = Join-Path $ReleaseDir "package-signature.txt"
$Sha256 = dotnet run --project $ReleaseTool -c Release --no-build -- `
    sign-package $SetupPath $PrivateKeyPath $SignaturePath
if ($LASTEXITCODE -ne 0) { throw "Package signing failed" }
$PackageSignature = (Get-Content -LiteralPath $SignaturePath -Raw).Trim()
$SetupSize = (Get-Item -LiteralPath $SetupPath).Length

$Payload = [ordered]@{
    SchemaVersion = 1
    Channel = "stable"
    Releases = @([ordered]@{
        Version = $Version
        Size = $SetupSize
        Sha256 = ($Sha256 | Select-Object -Last 1).Trim()
        PackageSignature = $PackageSignature
        ReleaseNotes = "Lenx Tools $Version initial installable preview."
        MinimumSupportedVersion = "0.1.0"
        MandatorySecurityUpdate = $false
        Mirrors = @("https://github.com/$Repository/releases/download/v$Version/LenxTool_Setup.exe")
    })
}
$PayloadJson = $Payload | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($PayloadPath, $PayloadJson, (New-Object System.Text.UTF8Encoding($false)))
dotnet run --project $ReleaseTool -c Release --no-build -- `
    sign-manifest $PayloadPath $PrivateKeyPath $ManifestPath
if ($LASTEXITCODE -ne 0) { throw "Manifest signing failed" }
dotnet run --project $ReleaseTool -c Release --no-build -- `
    verify-package $SetupPath $SignaturePath $PublicKeyPath
if ($LASTEXITCODE -ne 0) { throw "Package signature verification failed" }
dotnet run --project $ReleaseTool -c Release --no-build -- `
    verify-manifest $ManifestPath $PublicKeyPath
if ($LASTEXITCODE -ne 0) { throw "Manifest signature verification failed" }

Get-FileHash -Algorithm SHA256 -LiteralPath $SetupPath, $PortablePath |
    ForEach-Object { "{0}  {1}" -f $_.Hash.ToLowerInvariant(), (Split-Path $_.Path -Leaf) } |
    Set-Content -LiteralPath (Join-Path $ReleaseDir "SHA256SUMS.txt") -Encoding ascii

Write-Host "Release completed: $ReleaseDir"
