<#
.SYNOPSIS
    构建 MicCast Windows 接收端并打包成安装程序（setup.exe）。
.DESCRIPTION
    1) dotnet publish 出自包含的 win-x64 产物（用户机器不需要预装 .NET）
    2) 用 Inno Setup 打成一个 MicCast-Setup-<版本>-win-x64.exe
.EXAMPLE
    .\tools\build-installer.ps1
    .\tools\build-installer.ps1 -Version 1.0.1
#>
param(
    [string]$Version = '1.0.0',
    [string]$Iscc = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'windows\MicCastReceiver.WinUI'
$publish = Join-Path $repo 'dist\publish'
$iss = Join-Path $repo 'windows\installer\MicCast.iss'

if (-not $Iscc) {
    $Iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) {
    throw '没找到 ISCC.exe，请先安装 Inno Setup 6：winget install JRSoftware.InnoSetup'
}

# --- 1) 发布自包含产物 ---
Write-Host '[1/2] 发布 WinUI 自包含产物…' -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& dotnet publish $proj -c Release -p:Platform=x64 -p:SelfContained=true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# PRI 缺失时程序会以 0xc000027b 启动即崩，这里提前拦住
$pri = Get-ChildItem $publish -Filter '*.pri' | Where-Object { $_.Name -ne 'Microsoft.UI.pri' -and $_.Name -ne 'Microsoft.UI.Xaml.Controls.pri' }
if (-not $pri) { throw "发布目录里没有应用自己的 .pri（resources.pri / MicCastReceiver.pri），产物不可用" }

# --- 2) 打包 ---
Write-Host '[2/2] 生成安装包…' -ForegroundColor Cyan
& $Iscc "/DAppVersion=$Version" "/DSourceDir=$publish" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC 失败（退出码 $LASTEXITCODE）" }

$setup = Get-ChildItem (Join-Path $repo 'dist') -Filter "MicCast-Setup-$Version-win-x64.exe" | Select-Object -First 1
Write-Host ''
Write-Host "完成：$($setup.FullName)" -ForegroundColor Green
Write-Host ("大小：{0:N1} MB" -f ($setup.Length / 1MB))
