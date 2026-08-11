@echo off
setlocal EnableExtensions EnableDelayedExpansion
title ModernWigiDash - Telemetry Setup

rem ============================================================
rem  Installs the two optional telemetry services:
rem    LibreHardwareService.msi   -> LibreHardwareService service
rem    PresentMon-v2.5.1.msi      -> PresentMon Shared Service
rem  Safe to re-run (msiexec repair/upgrade semantics).
rem ============================================================

rem --- Auto-elevate: if not admin, relaunch as admin (UAC prompt) ---
net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -WorkingDirectory '%~dp0' -Verb RunAs"
    exit /b 0
)

cd /d "%~dp0"

set "LHS_MSI=%~dp0telemetry\LibreHardwareService\LibreHardwareService.msi"
set "PM_MSI=%~dp0telemetry\PresentMon\PresentMon-v2.5.1.msi"
set "PM_BOOT=%~dp0telemetry\PresentMon\PresentMon-2.5.1-x64.exe"

echo.
echo Installing LibreHardwareService...
if not exist "%LHS_MSI%" (
    echo   [FAIL] LibreHardwareService - installer not found:
    echo          %LHS_MSI%
    set "LHS_RC=2"
) else (
    start /wait "" msiexec.exe /i "%LHS_MSI%" /qn /norestart
    set "LHS_RC=!errorlevel!"
)
if "!LHS_RC!"=="0" (echo   [OK] LibreHardwareService installed) else (echo   [FAIL] LibreHardwareService - msiexec exit code !LHS_RC!)

echo.
echo Installing PresentMon Shared Service...
if not exist "%PM_MSI%" (
    echo   [FAIL] PresentMon - installer not found:
    echo          %PM_MSI%
    set "PM_RC=2"
) else (
    start /wait "" msiexec.exe /i "%PM_MSI%" /qn /norestart
    set "PM_RC=!errorlevel!"
    if not "!PM_RC!"=="0" if exist "%PM_BOOT%" (
        echo   msiexec exit code !PM_RC! - retrying with the bootstrapper...
        start /wait "" "%PM_BOOT%" /quiet /norestart
        set "PM_RC=!errorlevel!"
    )
)
if "!PM_RC!"=="0" (echo   [OK] PresentMon installed) else (echo   [FAIL] PresentMon - installer exit code !PM_RC!)

echo.
if "!LHS_RC!"=="0" if "!PM_RC!"=="0" (
    echo Done. Both telemetry services are installed and start automatically.
    echo Start the app and add the Hardware Monitor / FPS + Frame Time widgets.
) else (
    echo One or both installers did not complete. Re-run this script after
    echo resolving the problem, or see the Troubleshooting section of README.txt.
)
echo.
pause
endlocal
