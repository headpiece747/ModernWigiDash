@echo off
setlocal EnableExtensions
rem apply-update.cmd <installDir> <stagedVersionDir> <appExeName>
rem The swap: wait for the app to exit, ensure the install dir is writable
rem (self-elevate if not), then rename-aside the exe and copy the staged
rem payload in. Never delete-first: a crash mid-swap leaves the .old recoverable.
set "LOG=%LOCALAPPDATA%\ModernWigiDash\updates\update.log"
mkdir "%LOCALAPPDATA%\ModernWigiDash\updates" 2>nul
echo [%date% %time%] updater start args: %* >> "%LOG%"

set "INSTALL=%~1"
set "STAGE=%~2"
set "EXE=%~3"

rem ---- 1. Wait for all app processes to exit (60s cap) ----
set /a WAIT=0
:waitloop
tasklist /FI "IMAGENAME eq %EXE%" 2>nul | find /I "%EXE%" >nul
if errorlevel 1 goto exited
set /a WAIT+=1
if %WAIT% GEQ 60 goto timeout
timeout /t 1 /nobreak >nul
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
echo [%date% %time%] install dir not writable; requesting elevation >> "%LOG%"
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs" 
exit /b 0
:writable

rem ---- 3. Rename-aside swap (crash-safe) ----
if not exist "%INSTALL%\%EXE%.old" (
  move /Y "%INSTALL%\%EXE%" "%INSTALL%\%EXE%.old" >nul
)
if not exist "%STAGE%\ModernWigiDash-win-x64\%EXE%" (
  echo [%date% %time%] ERROR: staged exe missing >> "%LOG%"
  exit /b 3
)

rem Retry loop for file-in-use (AV scanning etc.)
set /a TRY=0
:copyloop
copy /Y "%STAGE%\ModernWigiDash-win-x64\%EXE%" "%INSTALL%\%EXE%" >nul 2>&1
if not errorlevel 1 goto copied
set /a TRY+=1
if %TRY% GEQ 10 goto copyfail
timeout /t 1 /nobreak >nul
goto copyloop
:copyfail
echo [%date% %time%] ERROR: could not copy new exe after 10 tries >> "%LOG%"
exit /b 4
:copied

rem Copy staged Resources over (fonts/theme/icons) — preserve unknown user files.
if exist "%STAGE%\ModernWigiDash-win-x64\Resources" (
  xcopy /E /I /Y "%STAGE%\ModernWigiDash-win-x64\Resources" "%INSTALL%\Resources" >nul
)

rem ---- 4. Cleanup: drop the .old and the stage, relaunch ----
del "%INSTALL%\%EXE%.old" 2>nul
del /Q "%STAGE%\ModernWigiDash-win-x64\%EXE%" 2>nul
rd /S /Q "%STAGE%\ModernWigiDash-win-x64\Resources" 2>nul
rd /S /Q "%STAGE%" 2>nul
echo [%date% %time%] swap complete >> "%LOG%"
{{RELAUNCH}}
exit /b 0
