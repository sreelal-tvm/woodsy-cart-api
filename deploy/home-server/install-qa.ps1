<#
  WoodsyCart QA environment: qa.woodsycart.com, qaadmin.woodsycart.com, qaapi.woodsycart.com on this machine.
  Run in PowerShell "as administrator". Safe to run again: existing pieces are left in place.

    .\install-qa.ps1               first-time setup (QA data = copy of live, made now)
    .\install-qa.ps1 -RefreshData  replace QA data with a fresh copy of live

  QA runs from C:\WoodsyCart\qa: API service WoodsyCartApiQa on localhost:5300 (ASPNETCORE_ENVIRONMENT=QA,
  settings in api\appsettings.QA.json), sites on 127.0.0.1:8090 / 8091 (Caddy). Its database WoodsyCartDb_QA is on
  the Developer edition instance (licensed for testing), so it never takes memory or space from the live Express one.
  Each copy of live data is made safe: payment gateways with live keys are switched off and their keys removed,
  and email sending is off (turn it on in QA admin when testing emails).
#>
#Requires -RunAsAdministrator
param([switch]$RefreshData)
$ErrorActionPreference = 'Stop'
$root    = 'C:\WoodsyCart'
$qa      = "$root\qa"
$svc     = 'WoodsyCartApiQa'
$account = "NT SERVICE\$svc"
$liveSql = 'localhost\SQLEXPRESS'
$qaSql   = 'localhost'
$db      = 'WoodsyCartDb_QA'
$backups = 'H:\WoodsyCartBackups\db'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Sql($server, $query) {
    sqlcmd -S $server -E -C -b -Q $query
    if ($LASTEXITCODE) { throw "SQL step failed on $server." }
}

Step 'Folders and settings'
New-Item -ItemType Directory -Force "$qa\api", "$qa\web", "$qa\admin" | Out-Null
$settings = "$qa\api\appsettings.QA.json"
if (-not (Test-Path $settings)) {
    $chars = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $bytes = New-Object byte[] 64
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $key = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    $json = [ordered]@{
        ConnectionStrings = @{ Default = "Server=$qaSql;Database=$db;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true" }
        Jwt          = @{ Key = $key }
        Cors         = @{ Origins = @('https://qa.woodsycart.com', 'https://qaadmin.woodsycart.com') }
        Smtp         = @{ StorefrontUrl = 'https://qa.woodsycart.com'; AdminUrl = 'https://qaadmin.woodsycart.com'; ApiUrl = 'https://qaapi.woodsycart.com' }
        Seo          = @{ StorefrontFolder = '' }
        ImageCleaner = @{ AutoStart = $false }
        Vendor       = @{ AutoSyncHours = 0 }
        Kestrel      = @{ Endpoints = @{ Http = @{ Url = 'http://localhost:5300' } } }
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($settings, $json)
    Write-Host "Created $settings (new sign-in key, different from live)"
}

$service = Get-Service $svc -ErrorAction SilentlyContinue
if (-not (Test-Path "$qa\api\WoodsyCart.Api.exe")) {
    Step 'First release to QA (builds the API, storefront and admin)'
    & "$root\scripts\update.ps1" -QA
}

Step 'Photos and encryption keys from live'
# The keys let QA read the saved settings (email password, test gateway keys) in its copy of the live data.
robocopy "$root\api\wwwroot\media" "$qa\api\wwwroot\media" /E /R:2 /W:2 /NP /NFL /NDL | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying photos failed.' }
robocopy "$root\api\App_Data\keys" "$qa\api\App_Data\keys" /E /R:2 /W:2 /NP /NFL /NDL | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying keys failed.' }

$exists = (sqlcmd -S $qaSql -E -C -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'$db'") -join ''
if ($RefreshData -or $exists.Trim() -ne '1') {
    Step 'Copying live data into QA'
    if ($service -and $service.Status -eq 'Running') { Stop-Service $svc }
    $bak = "$backups\WoodsyCartDb_qa-copy.bak"
    icacls $backups /grant 'NT Service\MSSQLSERVER:(OI)(CI)RX' | Out-Null
    Sql $liveSql "BACKUP DATABASE WoodsyCartDb TO DISK=N'$bak' WITH INIT, COPY_ONLY, CHECKSUM"
    $data = ((sqlcmd -S $qaSql -E -C -h -1 -W -Q "SET NOCOUNT ON; SELECT SERVERPROPERTY('InstanceDefaultDataPath')") -join '').Trim()
    Sql $qaSql @"
IF DB_ID(N'$db') IS NOT NULL ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [$db] FROM DISK=N'$bak' WITH REPLACE, CHECKSUM,
  MOVE N'WoodsyCartDb' TO N'$data$db.mdf', MOVE N'WoodsyCartDb_log' TO N'$data${db}_log.ldf';
ALTER DATABASE [$db] SET MULTI_USER;
ALTER DATABASE [$db] SET RECOVERY SIMPLE;
"@
    Remove-Item $bak

    Step 'Making the QA copy safe'
    Sql $qaSql @"
USE [$db];
UPDATE PaymentProviderConfigs SET IsEnabled = 0, IsTestMode = 1, SettingsJson = N'{}', ProtectedSecrets = NULL
 WHERE Code IN (N'Razorpay', N'PhonePe', N'Cashfree', N'PayU', N'PaytmPg') AND (IsTestMode = 0 OR SettingsJson LIKE N'%live%');
UPDATE EmailConfigs SET Enabled = 0;
"@
}

Step "API service ($svc)"
# The service comes first: its Windows account (NT SERVICE\...) exists only once the service does.
if (-not $service) {
    sc.exe create $svc binPath= "`"$qa\api\WoodsyCart.Api.exe`"" start= delayed-auto obj= $account DisplayName= "WoodsyCart API (QA)" | Out-Null
    sc.exe description $svc "WoodsyCart QA API (Kestrel on http://localhost:5300)" | Out-Null
    sc.exe failure $svc reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
}
# ASPNETCORE_ENVIRONMENT=QA makes the API read appsettings.QA.json.
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$svc" -Name Environment -Type MultiString -Value @('ASPNETCORE_ENVIRONMENT=QA')
icacls "$qa\api" /grant "${account}:(OI)(CI)M" | Out-Null

Step 'Database login for the QA API'
Sql $qaSql @"
IF SUSER_ID(N'$account') IS NULL CREATE LOGIN [$account] FROM WINDOWS;
USE [$db];
IF USER_ID(N'$account') IS NOT NULL DROP USER [$account];
CREATE USER [$account] FOR LOGIN [$account];
ALTER ROLE db_owner ADD MEMBER [$account];
"@

Step 'Starting the QA API'
Restart-Service $svc -ErrorAction SilentlyContinue
if ((Get-Service $svc).Status -ne 'Running') { Start-Service $svc }
$ok = $false
foreach ($i in 1..30) {
    Start-Sleep -Seconds 2
    try { if ((Invoke-WebRequest http://localhost:5300/api/catalog/home -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { $ok = $true; break } } catch {}
}
if (-not $ok) { throw 'The QA API did not answer on http://localhost:5300. Event Viewer > Windows Logs > Application shows why.' }
Write-Host 'QA API answers 200' -ForegroundColor Green

Step 'QA sites (Caddy :8090 and :8091)'
& "$root\caddy\caddy.exe" validate --config "$root\caddy\Caddyfile" --adapter caddyfile
if ($LASTEXITCODE) { throw 'The Caddyfile has an error (see above).' }
Restart-Service Caddy
Start-Sleep -Seconds 2
foreach ($u in 'http://localhost:8090/', 'http://localhost:8091/') {
    Write-Host "$u -> $((Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 5).StatusCode)" -ForegroundColor Green
}

Step 'Done'
Get-Service WoodsyCartApi, $svc, Caddy, 'MSSQL$SQLEXPRESS', MSSQLSERVER | Format-Table Name, Status, StartType -AutoSize
