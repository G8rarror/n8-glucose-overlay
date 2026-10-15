param([switch]$RemoveData)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name N8GlucoseOverlay -ErrorAction SilentlyContinue) { throw 'Exit N8 Glucose Overlay from its tray menu before uninstalling.' }
$targetRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\N8 Glucose Overlay'))
$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
if ([IO.Path]::GetDirectoryName($targetRoot) -ne $programsRoot -or [IO.Path]::GetFileName($targetRoot) -ne 'N8 Glucose Overlay') { throw 'Unexpected install location; refusing removal.' }
foreach ($folder in @('Programs', 'Desktop', 'Startup')) {
    $link = Join-Path ([Environment]::GetFolderPath($folder)) 'N8 Glucose Overlay.lnk'
    if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force }
}
if (Test-Path -LiteralPath $targetRoot) {
    $resolvedTarget = (Resolve-Path -LiteralPath $targetRoot).ProviderPath
    if ($resolvedTarget -ne $targetRoot -or ((Get-Item -LiteralPath $targetRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected or linked install location; refusing removal.' }
    Remove-Item -LiteralPath $targetRoot -Recurse -Force
}
if ($RemoveData) {
    $dataRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'N8GlucoseOverlay'))
    if ([IO.Path]::GetDirectoryName($dataRoot) -ne [IO.Path]::GetFullPath($env:LOCALAPPDATA) -or [IO.Path]::GetFileName($dataRoot) -ne 'N8GlucoseOverlay') { throw 'Unexpected data location; refusing removal.' }
    if (Test-Path -LiteralPath $dataRoot) {
        if ((Resolve-Path -LiteralPath $dataRoot).ProviderPath -ne $dataRoot -or ((Get-Item -LiteralPath $dataRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected or linked data location; refusing removal.' }
        Remove-Item -LiteralPath $dataRoot -Recurse -Force
    }
}
Write-Host 'N8 Glucose Overlay removed. Settings and encrypted credentials are retained unless -RemoveData was used.'
