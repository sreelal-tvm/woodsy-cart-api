<#
  WoodsyCart release: publishes the current code from F:\Business\GitHub to the live folders in C:\WoodsyCart,
  or with -QA to the QA folders in C:\WoodsyCart\qa. Run in PowerShell "as administrator" after committing a change.
  Release to QA first, test on qa.woodsycart.com, then release live. The live shop pauses for about 30 seconds
  while the API restarts, so release live at night or early morning.

    .\update.ps1              API + storefront + admin (backup first)
    .\update.ps1 -SitesOnly   storefront + admin only (no restart, no backup)
    .\update.ps1 -ApiOnly     API only
    .\update.ps1 -QA          the same, to QA (no backup; QA data can be refreshed with install-qa.ps1 -RefreshData)
#>
#Requires -RunAsAdministrator
param([switch]$ApiOnly, [switch]$SitesOnly, [switch]$SkipBackup, [switch]$QA)
$ErrorActionPreference = 'Stop'
$repo = 'F:\Business\GitHub'
$root = 'C:\WoodsyCart'
$t = if ($QA) {
    @{ Name = 'QA'; Dir = "$root\qa"; Service = 'WoodsyCartApiQa'; Port = 5300
       Api = 'https://qaapi.woodsycart.com'; Store = 'https://qa.woodsycart.com'; Install = 'install-qa.ps1' }
} else {
    @{ Name = 'live'; Dir = $root; Service = 'WoodsyCartApi'; Port = 5000
       Api = 'https://api.woodsycart.com'; Store = 'https://woodsycart.com'; Install = 'install-services.ps1' }
}
function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Copy-Tree($from, $to, [string[]]$extra) {
    robocopy $from $to /MIR /R:2 /W:2 /NP /NFL /NDL @extra | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying $from failed (robocopy $LASTEXITCODE)" }
}

# First release: the API service doesn't exist yet (the install script creates it), so there is nothing to stop or back up.
$service = Get-Service $t.Service -ErrorAction SilentlyContinue

if (-not $SitesOnly) {
    if ($service -and -not $SkipBackup -and -not $QA) {
        Step 'Backup first (a database change is applied automatically when the new API starts)'
        & "$root\scripts\backup.ps1" -LocalOnly
        if ($LASTEXITCODE) { throw 'Backup failed - nothing was changed.' }
    }

    Step "Building the API ($($t.Name))"
    $build = "$root\transfer\api-build-$($t.Name)"
    if (Test-Path $build) { Remove-Item $build -Recurse -Force }
    dotnet publish "$repo\woodsy-cart-api\src\WoodsyCart.Api" -c Release -o $build --nologo
    if ($LASTEXITCODE) { throw "API build failed - the $($t.Name) API was not touched." }

    Step 'Swapping the API (about 30 seconds)'
    if ($service) { Stop-Service $t.Service }
    # No /MIR here: photos, encryption keys and the appsettings file live only in the server folder.
    robocopy $build "$($t.Dir)\api" /E /R:5 /W:2 /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { if ($service) { Start-Service $t.Service }; throw "Copying the API failed (robocopy $LASTEXITCODE)" }
    if (-not $service) { Write-Host "API files are in place; run $($t.Install) to start it." -ForegroundColor Yellow }
}
if (-not $SitesOnly -and $service) {
    Start-Service $t.Service
    $ok = $false
    foreach ($i in 1..30) {
        Start-Sleep -Seconds 2
        try { if ((Invoke-WebRequest "http://localhost:$($t.Port)/api/catalog/home" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { $ok = $true; break } } catch {}
    }
    if (-not $ok) { throw 'The new API does not answer. Event Viewer > Windows Logs > Application shows why; the backup is in H:\WoodsyCartBackups\db.' }
    Write-Host 'API is back (200)' -ForegroundColor Green
}

if (-not $ApiOnly) {
    foreach ($app in @(
        @{ Name = 'woodsy-cart-web'; Live = "$($t.Dir)\web"; Config = "{ `"apiUrl`": `"$($t.Api)`" }";
           Keep = @('/XD', 'share', '/XF', 'config.json', 'sitemap.xml', 'robots.txt', 'web.config') },
        @{ Name = 'woodsy-cart-admin'; Live = "$($t.Dir)\admin"; Config = "{ `"apiUrl`": `"$($t.Api)`", `"storefrontUrl`": `"$($t.Store)`" }";
           Keep = @('/XF', 'config.json', 'web.config') })) {
        Step "Building $($app.Name)"
        Push-Location "$repo\$($app.Name)"
        try {
            npx ng build --configuration production
            if ($LASTEXITCODE) { throw "$($app.Name) build failed - the $($t.Name) site was not touched." }
            # The server's config.json, and the SEO files the API writes (share/, sitemap.xml, robots.txt), are kept.
            Copy-Tree "dist\$($app.Name)\browser" $app.Live $app.Keep
        }
        finally { Pop-Location }
        if (-not (Test-Path "$($app.Live)\config.json")) { [IO.File]::WriteAllText("$($app.Live)\config.json", $app.Config) }
        Write-Host "$($app.Name) is out ($($t.Name))" -ForegroundColor Green
    }
}

Step "Release finished ($($t.Name))"
Get-Service $t.Service, Caddy -ErrorAction SilentlyContinue | Format-Table Name, Status -AutoSize
