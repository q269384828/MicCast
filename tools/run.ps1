<#
.SYNOPSIS
    编译并启动 MicCast 接收端（WinUI 版）。双击根目录的 run.cmd 就行。
.DESCRIPTION
    产物固定在 windows\MicCastReceiver.WinUI\bin\<Platform>\Release\net9.0-windows10.0.19041.0\win-x64\，
    不用再去 bin 目录里翻。已是最新时增量构建只需几秒。
    参数原样透传给程序，例如 .\run.cmd --status
#>
param(
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Platform = 'x64',
    [switch]$NoBuild,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'windows\MicCastReceiver.WinUI\MicCastReceiver.WinUI.csproj'
$outDir = Join-Path $repo "windows\MicCastReceiver.WinUI\bin\$Platform\Release\net9.0-windows10.0.19041.0\win-x64"
$exe = Join-Path $outDir 'MicCastReceiver.exe'

if (-not $NoBuild) {
    Write-Host "编译 $Platform Release…" -ForegroundColor Cyan
    & dotnet build $proj -c Release -p:Platform=$Platform --nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）" }
}

if (-not (Test-Path $exe)) { throw "没找到 $exe，先跑一次不带 -NoBuild 的构建" }

# 带参数时是命令行调用（--status / --discover 等），要等它输出完；否则直接拉起窗口
if ($AppArgs -and $AppArgs.Count -gt 0) {
    & $exe @AppArgs
    exit $LASTEXITCODE
}

# WinUI 的 .pri 和依赖 dll 都按 exe 所在目录解析，工作目录必须是产物目录
Start-Process -FilePath $exe -WorkingDirectory $outDir
Write-Host "已启动：$exe" -ForegroundColor Green
