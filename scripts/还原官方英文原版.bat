@echo off
chcp 65001 >nul
setlocal EnableExtensions DisableDelayedExpansion
title FModel 一键还原官方英文原版 v1.0.2

set "SCRIPT_DIR=%~dp0"
set "PATCHER=%SCRIPT_DIR%tools\FModelCnPatcher.exe"
set "TARGET=%~1"
set "NO_PAUSE=%~2"

echo ============================================================
echo   FModel 一键还原官方英文原版工具 v1.0.2
echo   通过 *.orig.bak 无损还原为官方原版（哈希 100%% 一致）
echo ============================================================
echo.

if not exist "%PATCHER%" (
    echo [错误] 找不到核心程序: "%PATCHER%"
    echo 请确保完整解压本工具压缩包后再运行。
    goto :end_fail
)

if "%TARGET%"=="" (
    if exist "%SCRIPT_DIR%FModel.exe" (
        set "TARGET=%SCRIPT_DIR%FModel.exe"
    ) else if exist "%SCRIPT_DIR%..\FModel.exe" (
        for %%I in ("%SCRIPT_DIR%..\FModel.exe") do set "TARGET=%%~fI"
    )
)

if "%TARGET%"=="" (
    echo 未在当前目录或上级目录检测到 FModel.exe，请在弹出窗口中选择...
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -STA -Command "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Add-Type -AssemblyName System.Windows.Forms; $d=New-Object System.Windows.Forms.OpenFileDialog; $d.Title='请选择要还原为官方英文原版的 FModel.exe'; $p=[char]124; $d.Filter=('FModel 可执行文件 (FModel.exe)'+$p+'FModel.exe'+$p+'所有程序 (*.exe)'+$p+'*.exe'); if($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK){ Write-Output $d.FileName }"`) do (
        set "TARGET=%%I"
    )
)

if "%TARGET%"=="" (
    echo [提示] 未选择 FModel.exe，操作已取消。
    echo 小提示: 也可以把 FModel.exe 直接拖放到本脚本图标上运行。
    goto :end_fail
)

if not exist "%TARGET%" (
    echo [错误] 找不到目标文件: "%TARGET%"
    goto :end_fail
)

:check_lock
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $s=[System.IO.File]::Open($env:TARGET,'Open','ReadWrite','None'); $s.Close(); exit 0 } catch { exit 1 }" >nul 2>&1
if errorlevel 1 (
    echo.
    echo [提示] 检测到目标文件正在被占用（可能 FModel.exe 正在运行）。
    echo 请先关闭正在运行的 FModel 窗口，然后按任意键继续，或直接关闭本窗口取消...
    if /I "%NO_PAUSE%"=="--no-pause" goto :end_fail
    pause >nul
    goto :check_lock
)

echo 目标程序: "%TARGET%"
echo.
"%PATCHER%" restore "%TARGET%"
if errorlevel 1 (
    echo.
    echo [失败] 还原失败，请确认同目录下存在 FModel.exe.orig.bak 原版备份文件。
    goto :end_fail
)

echo.
echo ============================================================
echo   [完成] FModel 已还原为官方英文原版！
echo ============================================================
if /I not "%NO_PAUSE%"=="--no-pause" pause
exit /b 0

:end_fail
echo.
if /I not "%NO_PAUSE%"=="--no-pause" pause
exit /b 1
