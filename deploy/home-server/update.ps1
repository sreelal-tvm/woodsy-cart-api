<#
  WoodsyCart release: publishes the current code from F:\Business\GitHub to the live folders in C:\WoodsyCart.
  Run in PowerShell "as administrator" after committing and testing a change. The shop pauses for about 30 seconds
  while the API restarts, so release at night or early morning.

    .\update.ps1              API + storefront + admin (backup first)
    .\update.ps1 -SitesOnly   storefront + admin only (no restart, no backup)
    .\update.ps1 -ApiOnly     API only
#>
#Requires -RunAsAdministrator
param([switch]$ApiOnly, [switch]$SitesOnly, [switch]$SkipBackup)
$ErrorActionPreference = 'Stop'
$repo = 'F:\Business\GitHub'
$root = 'C:\WoodsyCart'
function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Copy-Tree($from, $to, [string[]]$extra) {
    robocopy $from $to /MIR /R:2 /W:2 /NP /NFL /NDL @extra | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying $from failed (robocopy $LASTEXITCODE)" }
}

# First release: the API service doesn't exist yet (install-services.ps1 creates it), so there is nothing to stop or back up.
$service = Get-Service WoodsyCartApi -ErrorAction SilentlyContinue

if (-not $SitesOnly) {
    if ($service -and -not $SkipBackup) {
        Step 'Backup first (a database change is applied automatically when the new API starts)'
        & "$root\scripts\backup.ps1" -LocalOnly
        if ($LASTEXITCODE) { throw 'Backup failed - nothing was changed.' }
    }

    Step 'Building the API'
    $build = "$root\transfer\api-build"
    if (Test-Path $build) { Remove-Item $build -Recurse -Force }
    dotnet publish "$repo\woodsy-cart-api\src\WoodsyCart.Api" -c Release -o $build --nologo
    if ($LASTEXITCODE) { throw 'API build failed - the live API was not touched.' }

    Step 'Swapping the API (about 30 seconds)'
    if ($service) { Stop-Service WoodsyCartApi }
    # No /MIR here: photos, encryption keys and appsettings.Production.json live only in the live folder.
    robocopy $build "$root\api" /E /R:5 /W:2 /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { if ($service) { Start-Service WoodsyCartApi }; throw "Copying the API failed (robocopy $LASTEXITCODE)" }
    if (-not $service) { Write-Host 'API files are in place; run install-services.ps1 to start it.' -ForegroundColor Yellow }
}
if (-not $SitesOnly -and $service) {
    Start-Service WoodsyCartApi
    $ok = $false
    foreach ($i in 1..30) {
        Start-Sleep -Seconds 2
        try { if ((Invoke-WebRequest http://localhost:5000/api/catalog/home -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { $ok = $true; break } } catch {}
    }
    if (-not $ok) { throw 'The new API does not answer. Event Viewer > Windows Logs > Application shows why; the backup is in H:\WoodsyCartBackups\db.' }
    Write-Host 'API is back (200)' -ForegroundColor Green
}

if (-not $ApiOnly) {
    foreach ($app in @(
        @{ Name = 'woodsy-cart-web'; Live = "$root\web"; Config = '{ "apiUrl": "https://api.woodsycart.com" }';
           Keep = @('/XD', 'share', '/XF', 'config.json', 'sitemap.xml', 'robots.txt', 'web.config') },
        @{ Name = 'woodsy-cart-admin'; Live = "$root\admin"; Config = "{ `"apiUrl`": `"https://api.woodsycart.com`", `"storefrontUrl`": `"https://woodsycart.com`" }";
           Keep = @('/XF', 'config.json', 'web.config') })) {
        Step "Building $($app.Name)"
        Push-Location "$repo\$($app.Name)"
        try {
            npx ng build --configuration production
            if ($LASTEXITCODE) { throw "$($app.Name) build failed - the live site was not touched." }
            # The live config.json, and the SEO files the API writes (share/, sitemap.xml, robots.txt), are kept.
            Copy-Tree "dist\$($app.Name)\browser" $app.Live $app.Keep
        }
        finally { Pop-Location }
        if (-not (Test-Path "$($app.Live)\config.json")) { [IO.File]::WriteAllText("$($app.Live)\config.json", $app.Config) }
        Write-Host "$($app.Name) is live" -ForegroundColor Green
    }
}

Step 'Release finished'
Get-Service WoodsyCartApi, Caddy -ErrorAction SilentlyContinue | Format-Table Name, Status -AutoSize
