@echo off
setlocal
cd /d "%~dp0"
call build.cmd
if errorlevel 1 exit /b 1
start "" /wait build\N8GlucoseOverlay.exe --preview --output artifacts
if errorlevel 1 exit /b 1
if not exist assets mkdir assets
copy /y artifacts\N8.ico assets\N8.ico >nul
call build.cmd
if errorlevel 1 exit /b 1
start "" /wait build\N8GlucoseOverlay.exe --self-test --output artifacts
if errorlevel 1 (
  type artifacts\self-test.txt
  exit /b 1
)
start "" /wait build\N8GlucoseOverlay.exe --smoke-test --output artifacts
if errorlevel 1 (
  type artifacts\smoke-test.txt
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Package.ps1"
exit /b %errorlevel%
