@echo off
setlocal EnableExtensions EnableDelayedExpansion
title ModernWigiDash - Telemetry Setup

rem ============================================================
rem  Installs the two optional telemetry services:
rem    LibreHardwareService.msi   -> LibreHardwareService service
rem    PresentMon*.msi            -> PresentMon Shared Service
rem  Safe to re-run (msiexec repair/upgrade semantics).
rem ============================================================

rem --- Auto-elevate: if not admin, relaunch as admin (UAC prompt) ---
net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator rights...
    set "WMD_SETUP=%~f0"
    set "WMD_SETUP_DIR=%~dp0"
    powershell -NoProfile -Command "$ErrorActionPreference = 'Stop'; Start-Process -FilePath $env:WMD_SETUP -WorkingDirectory $env:WMD_SETUP_DIR -Verb RunAs"
    if errorlevel 1 (
        echo Administrator elevation was cancelled or failed.
        pause
        exit /b 1
    )
    exit /b 0
)

cd /d "%~dp0"

set "LHS_MSI=%~dp0telemetry\LibreHardwareService\LibreHardwareService.msi"
set "PM_MSI="
set "PM_BOOT="
for %%F in ("%~dp0telemetry\PresentMon\PresentMon*.msi") do set "PM_MSI=%%F"
for %%F in ("%~dp0telemetry\PresentMon\PresentMon*-x64.exe") do set "PM_BOOT=%%F"

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
set "LHS_OK=0"
if "!LHS_RC!"=="0" set "LHS_OK=1"
if "!LHS_RC!"=="3010" set "LHS_OK=1"
if "!LHS_RC!"=="0" (
    echo   [OK] LibreHardwareService installed
) else if "!LHS_RC!"=="3010" (
    echo   [OK] LibreHardwareService installed ^(reboot recommended^)
) else (
    echo   [FAIL] LibreHardwareService - msiexec exit code !LHS_RC!
)

echo.
echo Installing PresentMon Shared Service...
if not defined PM_MSI (
    echo   [FAIL] PresentMon - installer not found in telemetry\PresentMon\
    set "PM_RC=2"
) else if not exist "%PM_MSI%" (
    echo   [FAIL] PresentMon - installer not found:
    echo          %PM_MSI%
    set "PM_RC=2"
) else (
    start /wait "" msiexec.exe /i "%PM_MSI%" /qn /norestart
    set "PM_RC=!errorlevel!"
    if not "!PM_RC!"=="0" if not "!PM_RC!"=="3010" if defined PM_BOOT if exist "%PM_BOOT%" (
        echo   msiexec exit code !PM_RC! - retrying with the bootstrapper...
        start /wait "" "%PM_BOOT%" /quiet /norestart
        set "PM_RC=!errorlevel!"
    )
)
set "PM_OK=0"
if "!PM_RC!"=="0" set "PM_OK=1"
if "!PM_RC!"=="3010" set "PM_OK=1"
if "!PM_RC!"=="0" (
    echo   [OK] PresentMon installed
) else if "!PM_RC!"=="3010" (
    echo   [OK] PresentMon installed ^(reboot recommended^)
) else (
    echo   [FAIL] PresentMon - installer exit code !PM_RC!
)

echo.
if "!LHS_OK!"=="1" if "!PM_OK!"=="1" (
    if "!LHS_RC!"=="3010" if "!PM_RC!"=="3010" (
        echo Done. Both telemetry installers requested a reboot ^(exit code 3010^).
    ) else if "!LHS_RC!"=="3010" (
        echo Done. LibreHardwareService requested a reboot ^(code 3010^); PresentMon installed.
    ) else if "!PM_RC!"=="3010" (
        echo Done. LibreHardwareService installed; PresentMon requested a reboot ^(code 3010^).
    ) else (
        echo Done. Both telemetry services are installed and start automatically.
    )
    if "!LHS_RC!"=="3010" echo Note: reboot Windows before using LibreHardwareService ^(installer requested reboot, code 3010^).
    if "!PM_RC!"=="3010" echo Note: reboot Windows before using PresentMon ^(installer requested reboot, code 3010^).
    echo Start the app and add the Hardware Monitor and FPS / Frame Time widgets.
) else (
    echo One or both installers did not complete. Re-run this script after
    echo checking any error messages above.
    echo.
    pause
    exit /b 1
)
echo.
pause
