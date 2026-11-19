<#
.SYNOPSIS
    构建（含签名）→ 安装到手机 → 启动 → 抓日志。
.DESCRIPTION
    前提：先在 DevEco Studio 里 File → Project Structure → Signing Configs
          勾选 "Automatically generate signature"，让 DevEco 为本工程的
          bundleName (com.local.miccast) 生成 p12/cer/p7b 并写入
          build-profile.json5 的 signingConfigs。
.EXAMPLE
    .\tools\install.ps1 -Device <手机IP:端口>
#>
param(
    [string]$Device = '',
    [string]$DevEco = 'D:\Huawei\DevEco Studio 26.0.1.290',
    [switch]$SkipBuild,
    [int]$LogSeconds = 30
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$hdc = Join-Path $DevEco 'sdk\default\openharmony\toolchains\hdc.exe'
$nodePath = Join-Path $DevEco 'tools\node'
$env:DEVECO_SDK_HOME = Join-Path $DevEco 'sdk'
$env:PATH = "$nodePath;$env:PATH"

# --- 1) 检查签名配置 ---
$profileText = Get-Content (Join-Path $root 'build-profile.json5') -Raw
if ($profileText -match '"signingConfigs"\s*:\s*\[\s*\]') {
    Write-Host ''
    Write-Host '=========================================================' -ForegroundColor Yellow
    Write-Host ' 还没有签名配置，无法安装（鸿蒙拒绝未签名 HAP）' -ForegroundColor Yellow
    Write-Host ' 请先在 DevEco Studio 里做一次：' -ForegroundColor Yellow
    Write-Host '   File -> Project Structure -> Signing Configs' -ForegroundColor Yellow
    Write-Host '   勾选 "Automatically generate signature"（需登录华为开发者账号）' -ForegroundColor Yellow
    Write-Host ' 然后再运行本脚本。' -ForegroundColor Yellow
    Write-Host '=========================================================' -ForegroundColor Yellow
    Write-Host ''
    exit 2
}

# --- 2) 连接设备 ---
Write-Host "[1/5] 连接设备 $Device" -ForegroundColor Cyan
& $hdc tconn $Device | Out-Null
Start-Sleep -Seconds 2
$targets = & $hdc list targets
if ($targets -notmatch [regex]::Escape($Device)) {
    Write-Host "设备未连接成功。当前: $targets" -ForegroundColor Red
    exit 1
}
$model = (& $hdc -t $Device shell 'param get const.product.name') -join ' '
$api = (& $hdc -t $Device shell 'param get const.ohos.apiversion') -join ' '
Write-Host "      $($model.Trim())  API $($api.Trim())"

# --- 3) 构建 ---
if (-not $SkipBuild) {
    Write-Host '[2/5] 构建签名 HAP' -ForegroundColor Cyan
    & (Join-Path $root 'hvigorw.bat') --no-daemon assembleHap --mode module -p product=default -p buildMode=debug | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host '构建失败，请查看上面的错误。' -ForegroundColor Red
        exit 1
    }
}
else {
    Write-Host '[2/5] 跳过构建' -ForegroundColor DarkGray
}

$hap = Get-ChildItem (Join-Path $root 'entry\build\default\outputs\default') -Filter '*signed.hap' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $hap) {
    Write-Host '没找到 signed.hap（只有 unsigned 说明 hvigor 没做签名步骤）' -ForegroundColor Red
    exit 1
}
Write-Host "      $($hap.Name)  $([math]::Round($hap.Length/1MB,2)) MB"

# --- 4) 安装 ---
Write-Host '[3/5] 安装' -ForegroundColor Cyan
$installOut = (& $hdc -t $Device install -r $hap.FullName) -join "`n"
Write-Host "      $($installOut.Trim())"
if ($installOut -match 'failed') { exit 1 }

# --- 5) 启动并抓日志 ---
$bundle = (Get-Content (Join-Path $root 'AppScope\app.json5') -Raw | ConvertFrom-Json).app.bundleName
Write-Host "[4/5] 启动 $bundle" -ForegroundColor Cyan
& $hdc -t $Device shell "aa start -a EntryAbility -b $bundle" | Out-Null

Write-Host "[5/5] 抓 $LogSeconds 秒日志（操作手机，观察行为）" -ForegroundColor Cyan
$job = Start-Job -ScriptBlock {
    param($h, $d, $b)
    & $h -t $d shell "hilog -x" 2>&1 | Where-Object { $_ -match $b -or $_ -match 'MicCast' }
} -ArgumentList $hdc, $Device, $bundle
Start-Sleep -Seconds $LogSeconds
$lines = Receive-Job $job
Stop-Job $job -ErrorAction SilentlyContinue
Remove-Job $job -Force -ErrorAction SilentlyContinue

$logFile = Join-Path $root 'tools\device-log.txt'
$lines | Set-Content $logFile -Encoding UTF8
Write-Host "      抓到 $(@($lines).Count) 行，已存 $logFile" -ForegroundColor Green
if (@($lines).Count -gt 0) { $lines | Select-Object -Last 25 }
