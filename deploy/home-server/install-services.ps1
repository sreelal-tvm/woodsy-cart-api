<#
  WoodsyCart home server: installs the API and Caddy as Windows services (Phase 4 of the hosting plan).
  Run once, in PowerShell "as administrator", after the API has been published to C:\WoodsyCart\api
  and caddy.exe + Caddyfile are in C:\WoodsyCart\caddy. Safe to run again: existing services are left in place.
#>
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$root = 'C:\WoodsyCart'
$sql = 'localhost\SQLEXPRESS'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Restart-OnFailure($name) { sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null }

Step 'Checking files'
foreach ($f in "$root\api\WoodsyCart.Api.exe", "$root\api\appsettings.Production.json", "$root\caddy\caddy.exe", "$root\caddy\Caddyfile") {
    if (-not (Test-Path $f)) { throw "Missing $f - see Phase 3 and Phase 4 of the plan." }
}
New-Item -ItemType Directory -Force "$root\logs", "$root\web", "$root\admin" | Out-Null

Step 'API service (WoodsyCartApi)'
if (-not (Get-Service WoodsyCartApi -ErrorAction SilentlyContinue)) {
    sc.exe create WoodsyCartApi binPath= "`"$root\api\WoodsyCart.Api.exe`"" start= delayed-auto obj= "NT SERVICE\WoodsyCartApi" DisplayName= "WoodsyCart API" | Out-Null
    sc.exe description WoodsyCartApi "WoodsyCart shop API (Kestrel on http://localhost:5000)" | Out-Null
}
Restart-OnFailure WoodsyCartApi
# The service account writes photos, encryption keys and SEO files.
icacls "$root\api" /grant "NT SERVICE\WoodsyCartApi:(OI)(CI)M" | Out-Null
icacls "$root\web" /grant "NT SERVICE\WoodsyCartApi:(OI)(CI)M" | Out-Null

Step 'Database login for the API (Windows sign-in, no password)'
sqlcmd -S $sql -E -C -b -Q @"
IF SUSER_ID(N'NT SERVICE\WoodsyCartApi') IS NULL CREATE LOGIN [NT SERVICE\WoodsyCartApi] FROM WINDOWS;
USE WoodsyCartDb;
IF USER_ID(N'NT SERVICE\WoodsyCartApi') IS NULL CREATE USER [NT SERVICE\WoodsyCartApi] FOR LOGIN [NT SERVICE\WoodsyCartApi];
ALTER ROLE db_owner ADD MEMBER [NT SERVICE\WoodsyCartApi];
"@
if ($LASTEXITCODE) { throw 'Could not create the database login.' }

Step 'Starting the API'
Start-Service WoodsyCartApi
$ok = $false
foreach ($i in 1..30) {
    Start-Sleep -Seconds 2
    try { if ((Invoke-WebRequest http://localhost:5000/api/catalog/home -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { $ok = $true; break } } catch {}
}
if (-not $ok) { throw 'The API did not answer on http://localhost:5000. Open Event Viewer > Windows Logs > Application and look for WoodsyCart.Api errors.' }
Write-Host 'API answers 200' -ForegroundColor Green

Step 'Caddy service (storefront :8080, admin :8081)'
& "$root\caddy\caddy.exe" validate --config "$root\caddy\Caddyfile" --adapter caddyfile
if ($LASTEXITCODE) { throw 'The Caddyfile has an error (see above).' }
if (-not (Get-Service Caddy -ErrorAction SilentlyContinue)) {
    sc.exe create Caddy start= auto binPath= "`"$root\caddy\caddy.exe`" run --config `"$root\caddy\Caddyfile`" --adapter caddyfile" DisplayName= "Caddy (WoodsyCart sites)" | Out-Null
}
Restart-OnFailure Caddy
Start-Service Caddy
Start-Sleep -Seconds 2
foreach ($u in 'http://localhost:8080/', 'http://localhost:8081/') {
    $code = (Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 5).StatusCode
    Write-Host "$u -> $code" -ForegroundColor Green
}

Step 'Done'
Get-Service WoodsyCartApi, Caddy, 'MSSQL$SQLEXPRESS' | Format-Table Name, Status, StartType -AutoSize
