@echo off
rem 启动 MicCast 接收端。双击即用；带参数则透传给程序，例如：run.cmd --status
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\run.ps1" %*
if errorlevel 1 pause
