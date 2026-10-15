@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo The Windows .NET Framework compiler was not found. Enable .NET Framework 4.8 in Windows.
  exit /b 1
)
if not exist build mkdir build
set "N8ICON="
if exist assets\N8.ico set "N8ICON=/win32icon:assets\N8.ico"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 %N8ICON% /out:build\N8GlucoseOverlay.exe /win32manifest:app.manifest /reference:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Net.Http.dll,System.Security.dll,System.Web.Extensions.dll src\*.cs
if errorlevel 1 exit /b 1
copy /y N8GlucoseOverlay.exe.config build\N8GlucoseOverlay.exe.config >nul
echo Built build\N8GlucoseOverlay.exe
