param(
    [switch]$StartWithWindows,
    [switch]$DesktopShortcut
)
$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$targetRoot = Join-Path $env:LOCALAPPDATA 'Programs\N8 Glucose Overlay'
$sourceExe = Join-Path $sourceRoot 'N8GlucoseOverlay.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) { throw 'Extract the release ZIP first, then run Install.ps1 from that folder.' }
if ([IO.Path]::GetFullPath($sourceRoot).TrimEnd('\') -eq [IO.Path]::GetFullPath($targetRoot).TrimEnd('\')) { throw 'Run this installer from the extracted release folder, outside the installed application folder.' }
$running = Get-Process -Name N8GlucoseOverlay -ErrorAction SilentlyContinue
if ($running) { throw 'Exit N8 Glucose Overlay from its tray menu before installing or updating.' }
if (Test-Path -LiteralPath $targetRoot) {
    if ((Get-Item -LiteralPath $targetRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The install folder must not be a symbolic link or junction.' }
}
New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
$files = @('N8GlucoseOverlay.exe', 'N8GlucoseOverlay.exe.config', 'README.md', 'API_COMPATIBILITY.md', 'Uninstall.ps1', 'LICENSE')
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $targetRoot $name) -Force }
$shell = New-Object -ComObject WScript.Shell
$exe = Join-Path $targetRoot 'N8GlucoseOverlay.exe'
function Add-N8Shortcut([string]$folder) {
    $shortcut = $shell.CreateShortcut((Join-Path $folder 'N8 Glucose Overlay.lnk'))
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $targetRoot
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'N8 Glucose Overlay for Gluroo Global Connect'
    $shortcut.Save()
}
Add-N8Shortcut ([Environment]::GetFolderPath('Programs'))
if ($DesktopShortcut) { Add-N8Shortcut ([Environment]::GetFolderPath('Desktop')) }
if ($StartWithWindows) { Add-N8Shortcut ([Environment]::GetFolderPath('Startup')) }
Write-Host "Installed to $targetRoot"
Write-Host 'Open N8 Glucose Overlay from Start. Exit it from the system tray before updating.'
