$ErrorActionPreference = 'Stop'
foreach ($script in @('Install.ps1', 'Uninstall.ps1', 'Package.ps1')) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $script), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw "PowerShell syntax errors in $script" }
}
$releaseRoot = Join-Path $PSScriptRoot 'dist\N8-Glucose-Overlay-1.1.0'
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$files = @('README.md', 'API_COMPATIBILITY.md', 'Install.ps1', 'Install.cmd', 'Uninstall.ps1', 'LICENSE', 'build.cmd', 'app.manifest', 'package.cmd', 'Package.ps1')
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $releaseRoot $name) -Force }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\N8GlucoseOverlay.exe') -Destination $releaseRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'N8GlucoseOverlay.exe.config') -Destination $releaseRoot -Force
foreach ($name in @('src', 'assets', 'docs')) {
    $destination = Join-Path $releaseRoot $name
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $name) -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Force }
}
$verification = Join-Path $releaseRoot 'verification'
New-Item -ItemType Directory -Path $verification -Force | Out-Null
foreach ($name in @('self-test.txt', 'smoke-test.txt', 'overlay-current.png', 'overlay-stale.png', 'settings-0.png', 'settings-1.png', 'settings-2.png')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "artifacts\$name") -Destination $verification -Force
}
$zipPath = Join-Path $PSScriptRoot 'dist\N8-Glucose-Overlay-1.1.0.zip'
Compress-Archive -Path (Join-Path $releaseRoot '*') -DestinationPath $zipPath -Force
$digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zipPath.sha256" -Value "$digest  N8-Glucose-Overlay-1.1.0.zip" -Encoding ASCII
Write-Host "Release: $zipPath"
Write-Host "SHA-256: $digest"
