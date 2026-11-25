# Writes tconnectsync's .env next to this script, readable only by the current Windows user.
# Nothing typed here is echoed or sent anywhere by this script.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$envPath = Join-Path $dir '.env'

function Plain([Security.SecureString]$s) {
  $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
  try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}
function Quote([string]$v) { '"' + $v.Replace('\', '\\').Replace('"', '\"') + '"' }

Write-Host ''
Write-Host 'tconnectsync setup (reads Tandem Source, uploads basal and bolus records to Gluroo)' -ForegroundColor Cyan
Write-Host ''

$email = Read-Host 'Tandem Source email (the account the pump uploads to)'
$password = Plain (Read-Host 'Tandem Source password' -AsSecureString)
$region = (Read-Host 'Tandem region, US or EU [US]').Trim().ToUpperInvariant()
if (-not $region) { $region = 'US' }
if ($region -notin @('US', 'EU')) { throw 'Region must be US or EU.' }
$tz = (Read-Host 'Pump time zone, IANA name such as America/New_York or Europe/London [America/New_York]').Trim()
if (-not $tz) { $tz = 'America/New_York' }

# If N8 Glucose Overlay is set up for this Windows user, reuse its Gluroo URL and check the token against it.
$settingsPath = Join-Path $env:LOCALAPPDATA 'N8GlucoseOverlay\settings.json'
$expected = $null; $server = $null
if (Test-Path $settingsPath) {
  $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
  $server = $settings.Server
  try {
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($settings.ProtectedCredential), $null, 'CurrentUser')
    $expected = [Text.Encoding]::UTF8.GetString($bytes).Trim().ToLowerInvariant()
    [Array]::Clear($bytes, 0, $bytes.Length)
  } catch { $expected = $null }
}
if (-not $server) { $server = (Read-Host 'Gluroo Nightscout URL, such as https://your-id.ns.gluroo.com').Trim() }
if ($server -notmatch '^https://[a-z0-9-]+\.ns\.gluroo\.com/?$') { throw 'That is not a Gluroo Nightscout URL.' }

$sha = [Security.Cryptography.SHA1]::Create()
while ($true) {
  Write-Host ''
  Write-Host 'Gluroo API Secret Token: the plain token, not the 40-character hex header.'
  $token = (Plain (Read-Host 'Gluroo API Secret Token' -AsSecureString)).Trim()
  if ($token -match '^[0-9a-fA-F]{40}$') { Write-Host 'That looks like the hashed header. tconnectsync needs the plain token.' -ForegroundColor Yellow; continue }
  if (-not $token) { continue }
  if (-not $expected) { break }
  $hash = (($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($token)) | ForEach-Object { $_.ToString('x2') }) -join '')
  if ($hash -eq $expected) { Write-Host 'Token matches the one the overlay uses.' -ForegroundColor Green; break }
  Write-Host "That doesn't match the overlay's saved credential. Try again." -ForegroundColor Yellow
}

$lines = @(
  ('TCONNECT_EMAIL=' + (Quote $email)),
  ('TCONNECT_PASSWORD=' + (Quote $password)),
  ('TCONNECT_REGION=' + (Quote $region)),
  ('NS_URL=' + (Quote ($server.TrimEnd('/') + '/'))),
  ('NS_SECRET=' + (Quote $token)),
  ('TIMEZONE_NAME=' + (Quote $tz)),
  'AUTOUPDATE_USE_FIXED_SLEEP="true"',
  'AUTOUPDATE_DEFAULT_SLEEP_SECONDS="120"'
)
if (Test-Path $envPath) { Remove-Item $envPath -Force }
New-Item -ItemType File -Path $envPath | Out-Null
# Owner-only access before any secret is written.
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls $envPath /inheritance:r /grant:r ("*" + $sid + ":(R,W,D)") | Out-Null
if ($LASTEXITCODE -ne 0) { Remove-Item $envPath -Force; throw 'Could not restrict access to the .env file; nothing was written.' }
[IO.File]::WriteAllLines($envPath, $lines, (New-Object Text.UTF8Encoding $false))
$password = $null; $token = $null
Write-Host ''
Write-Host "Saved $envPath (only your Windows account can read it)." -ForegroundColor Green
