# Read-only: lists each new Tandem Source "newest pump event" seen in tconnectsync's logs,
# when this PC first saw it (lag), and the gap since the previous upload.
param([string]$Folder = "$env:LOCALAPPDATA\tconnectsync")
$files = @("$Folder\sync.previous.log", "$Folder\sync.log") | Where-Object { Test-Path $_ }
$seen = @{}; $rows = @()
foreach ($line in (Get-Content $files)) {
    if ($line -notmatch '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d).*cur_max_date\W+(\d{4}-\d\d-\d\dT[\d:.]+[+-]\d\d:\d\d|\d{10})') { continue }
    $when = [datetime]$Matches[1]; $v = $Matches[2]
    if ($v -match '^\d{10}$') { $t = [DateTimeOffset]::FromUnixTimeSeconds([long]$v).ToLocalTime().DateTime }
    else { $t = [DateTimeOffset]::Parse($v).ToLocalTime().DateTime }
    $k = $t.ToString('s')
    if (-not $seen.ContainsKey($k)) { $seen[$k] = 1; $rows += [pscustomobject]@{ Seen = $when; Newest = $t } }
}
$prev = $null
foreach ($r in ($rows | Sort-Object Newest)) {
    $lag = [int]($r.Seen - $r.Newest).TotalMinutes
    $gap = if ($prev) { [int]($r.Newest - $prev).TotalMinutes } else { '' }
    '{0:MM-dd HH:mm}  first seen {1:HH:mm}  lag {2,4}m  gap {3,4}m' -f $r.Newest, $r.Seen, $lag, $gap
    $prev = $r.Newest
}
