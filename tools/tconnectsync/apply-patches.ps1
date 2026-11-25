# Applies two small fixes to an installed tconnectsync 3.0.x. Safe to run more than once.
# Re-run after every "pip install --upgrade tconnectsync", which overwrites them.
#  1. api\tandemsource.py: allow 60 s of clock skew when checking the Tandem login token
#     (otherwise "ImmatureSignatureError: The token is not yet valid" stops the sync).
#  2. sync\tandemsource\process.py: treat the naive end time as local time, not UTC
#     (otherwise on a non-UTC PC the newest basal can get a negative duration).
param([string]$Folder = "$env:LOCALAPPDATA\tconnectsync")
$ErrorActionPreference = 'Stop'
$pkg = Join-Path $Folder 'venv\Lib\site-packages\tconnectsync'
if (-not (Test-Path $pkg)) { throw "tconnectsync is not installed in $Folder\venv" }

function Patch([string]$rel, [string]$find, [string]$replace, [string]$marker, [int]$expected) {
    $path = Join-Path $pkg $rel
    $text = [IO.File]::ReadAllText($path)
    if ($text.Contains($marker)) { Write-Host "already patched: $rel"; return }
    $count = ([regex]::Matches($text, [regex]::Escape($find))).Count
    if ($count -ne $expected) { throw "$rel has changed upstream (found $count matches, expected $expected). Not patched; check whether the fix is still needed." }
    [IO.File]::WriteAllText($path, $text.Replace($find, $replace), (New-Object Text.UTF8Encoding $false))
    Write-Host "patched: $rel" -ForegroundColor Green
}

Patch 'api\tandemsource.py' "algorithms=['RS256']," "algorithms=['RS256'], leeway=60,  # N8 PATCH: clock skew" 'leeway=' 2
Patch 'sync\tandemsource\process.py' 'time_end_aware = arrow.get(time_end)' "time_end_aware = arrow.get(time_end).replace(tzinfo='local') if getattr(time_end, 'tzinfo', 1) is None else arrow.get(time_end)  # N8 PATCH: naive is local" "tzinfo='local'" 1
