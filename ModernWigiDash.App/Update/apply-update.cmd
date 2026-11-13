@echo off
setlocal EnableExtensions
rem apply-update.cmd <installDir> <stagedVersionDir> <appExeName> [elevated] [logPath]
rem The swap: wait for the app to exit, ensure the install dir is writable
rem (self-elevate if not), then rename-aside the exe and copy the staged
rem payload in. Never delete-first: a crash mid-swap leaves the .old recoverable.
rem Log file: WMD_UPDATE_LOG overrides the default path (tests redirect it to a temp file),
rem and arg 5 (%~5) forwards the caller's resolved log path across UAC elevation.
set "INSTALL=%~1"
set "STAGE=%~2"
set "EXE=%~3"
set "ELEVATED=%~4"
if not "%INSTALL%"=="" if "%INSTALL:~-1%"=="\" if not "%INSTALL:~-2%"==":\" set "INSTALL=%INSTALL:~0,-1%"
if not "%STAGE%"=="" if "%STAGE:~-1%"=="\" if not "%STAGE:~-2%"==":\" set "STAGE=%STAGE:~0,-1%"
set "LOG=%LOCALAPPDATA%\ModernWigiDash\updates\update.log"
if defined WMD_UPDATE_LOG set "LOG=%WMD_UPDATE_LOG%"
if not "%~5"=="" set "LOG=%~5"
for %%D in ("%LOG%") do mkdir "%%~dpD." 2>nul
echo [%date% %time%] updater start args: %* >> "%LOG%"

rem ---- 1. Wait for all app processes to exit (60s cap) ----
set /a WAIT=0
:waitloop
tasklist /FI "IMAGENAME eq %EXE%" 2>nul | find /I "%EXE%" >nul
if errorlevel 1 goto exited
set /a WAIT+=1
if %WAIT% GEQ 60 goto timeout
rem ping instead of timeout: console-independent ~1s delay (timeout fails
rem with "Input redirection is not supported" when stdin has no console)
ping -n 2 127.0.0.1 >nul
goto waitloop
:timeout
echo [%date% %time%] ERROR: app did not exit within 60s >> "%LOG%"
exit /b 2
:exited

rem ---- 2. Writability check; self-elevate when needed ----
set "PROBE=%INSTALL%\.update-write-probe"
echo x > "%PROBE%" 2>nul
if not exist "%PROBE%" goto elevate
del "%PROBE%" 2>nul
goto writable
:elevate
if /I "%ELEVATED%"=="elevated" (
  echo [%date% %time%] ERROR: install dir not writable even after elevation >> "%LOG%"
  exit /b 5
)
echo [%date% %time%] install dir not writable; requesting elevation >> "%LOG%"
rem Pass arguments via environment variables and PowerShell [char]34 quotes so
rem spaces, apostrophes, ampersands, and the caller's log path survive UAC.
set "WMD_CMD=%~f0"
set "WMD_INST=%INSTALL%"
set "WMD_STG=%STAGE%"
set "WMD_EXE=%EXE%"
set "WMD_LOG=%LOG%"
powershell -NoProfile -Command "$ErrorActionPreference = 'Stop'; $q = [char]34; $arg = '/S /c ' + $q + $q + $env:WMD_CMD + $q + ' ' + $q + $env:WMD_INST + $q + ' ' + $q + $env:WMD_STG + $q + ' ' + $q + $env:WMD_EXE + $q + ' elevated ' + $q + $env:WMD_LOG + $q + $q; Start-Process -FilePath $env:ComSpec -ArgumentList $arg -Verb RunAs -WindowStyle Hidden"
if errorlevel 1 (
  echo [%date% %time%] ERROR: UAC elevation cancelled or failed >> "%LOG%"
  exit /b 5
)
exit /b 0
:writable

rem ---- 3. Serialize, validate, then rename-aside (crash-safe) ----
rem mkdir is atomic on NTFS, so it is the swap lock: a second updater that
rem lost the race exits before starting a swap. The app clears any leftover
rem lock at startup (RunStartupPhase).
set "LOCK=%INSTALL%\.update-running"
mkdir "%LOCK%" 2>nul
if errorlevel 1 goto updater_busy
rem Validate the staged payload BEFORE renaming the live exe. The old order
rem moved the exe aside first and then aborted when the stage was missing,
rem leaving the install directory without an exe.
if not exist "%STAGE%\ModernWigiDash-win-x64\%EXE%" (
  echo [%date% %time%] ERROR: staged exe missing >> "%LOG%"
  rd "%LOCK%" 2>nul
  exit /b 3
)
if not exist "%INSTALL%\%EXE%" (
  echo [%date% %time%] ERROR: install exe missing >> "%LOG%"
  rd "%LOCK%" 2>nul
  exit /b 3
)
if exist "%INSTALL%\%EXE%.old" (
  del /F /Q "%INSTALL%\%EXE%.old" >nul 2>&1
  if exist "%INSTALL%\%EXE%.old" (
    echo [%date% %time%] ERROR: could not clear stale .old >> "%LOG%"
    rd "%LOCK%" 2>nul
    exit /b 3
  )
)
move /Y "%INSTALL%\%EXE%" "%INSTALL%\%EXE%.old" >nul
if errorlevel 1 (
  echo [%date% %time%] ERROR: could not rename the exe aside >> "%LOG%"
  rd "%LOCK%" 2>nul
  exit /b 3
)

rem Retry loop for file-in-use (AV scanning etc.)
set /a TRY=0
:copyloop
copy /Y "%STAGE%\ModernWigiDash-win-x64\%EXE%" "%INSTALL%\%EXE%" >nul 2>&1
if not errorlevel 1 goto copied
set /a TRY+=1
if %TRY% GEQ 10 goto copyfail
ping -n 2 127.0.0.1 >nul
goto copyloop
:copyfail
echo [%date% %time%] ERROR: could not copy new exe after 10 tries >> "%LOG%"
rem Restore the previous exe: a failed swap must never leave the install
rem directory without a runnable exe.
if exist "%INSTALL%\%EXE%.old" move /Y "%INSTALL%\%EXE%.old" "%INSTALL%\%EXE%" >nul
rd "%LOCK%" 2>nul
exit /b 4
:copied

rem Copy staged Resources over (fonts/theme/icons) - preserve unknown user files.
if not exist "%STAGE%\ModernWigiDash-win-x64\Resources" goto afterresources
set /a RES_TRY=0
:resloop
xcopy /E /I /Y "%STAGE%\ModernWigiDash-win-x64\Resources" "%INSTALL%\Resources" >nul 2>&1
if not errorlevel 1 goto afterresources
set /a RES_TRY+=1
if %RES_TRY% GEQ 5 goto resfail
ping -n 2 127.0.0.1 >nul
goto resloop
:resfail
echo [%date% %time%] ERROR: could not copy Resources after 5 tries >> "%LOG%"
rd "%LOCK%" 2>nul
exit /b 4
:afterresources

rem ---- 4. Cleanup: drop the .old and the stage, relaunch ----
del "%INSTALL%\%EXE%.old" 2>nul
del /Q "%STAGE%\ModernWigiDash-win-x64\%EXE%" 2>nul
rd /S /Q "%STAGE%\ModernWigiDash-win-x64\Resources" 2>nul
rd /S /Q "%STAGE%" 2>nul
rd "%LOCK%" 2>nul
echo [%date% %time%] swap complete >> "%LOG%"
{{RELAUNCH}}
exit /b 0

:updater_busy
echo [%date% %time%] another updater is already running; nothing to do >> "%LOG%"
exit /b 0
