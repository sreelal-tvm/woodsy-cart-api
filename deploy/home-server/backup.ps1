<#
  WoodsyCart nightly backup (Phase 8). Scheduled at 02:30 by Task Scheduler as SYSTEM; also run by update.ps1.
  1. Database backup to H:\WoodsyCartBackups\db (checked after writing), 14 days kept on this machine
  2. Photos mirrored, encryption keys and live settings copied
  3. Offsite copy to Cloudflare R2 with rclone (skipped with -LocalOnly or when rclone isn't set up)
  4. Ping to Healthchecks.io; a missing ping emails you
#>
param([switch]$LocalOnly)
$ErrorActionPreference = 'Stop'
$root    = 'C:\WoodsyCart'
$target  = 'H:\WoodsyCartBackups'
$sql     = 'localhost\SQLEXPRESS'
$rclone  = "$root\tools\rclone.exe"
$rcConf  = "$root\scripts\rclone.conf"
$pingUrl = if (Test-Path "$root\scripts\healthcheck-url.txt") { (Get-Content "$root\scripts\healthcheck-url.txt" -Raw).Trim() } else { $null }

New-Item -ItemType Directory -Force "$target\db", "$target\media", "$target\config", "$root\logs" | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
Start-Transcript -Path "$root\logs\backup-$stamp.log" | Out-Null
try {
    # Release backups (-LocalOnly) don't ping: a "start" with no "success" after it reads as a failed nightly backup.
    if ($pingUrl -and -not $LocalOnly) { try { Invoke-WebRequest "$pingUrl/start" -UseBasicParsing -TimeoutSec 10 | Out-Null } catch {} }

    $bak = "$target\db\WoodsyCartDb_$stamp.bak"
    sqlcmd -S $sql -E -C -b -Q "BACKUP DATABASE WoodsyCartDb TO DISK=N'$bak' WITH INIT, CHECKSUM"
    if ($LASTEXITCODE) { throw 'Database backup failed' }
    sqlcmd -S $sql -E -C -b -Q "RESTORE VERIFYONLY FROM DISK=N'$bak' WITH CHECKSUM"
    if ($LASTEXITCODE) { throw 'Backup file failed verification' }
    Get-ChildItem "$target\db\*.bak" | Where-Object LastWriteTime -lt (Get-Date).AddDays(-14) | Remove-Item

    robocopy "$root\api\wwwroot\media" "$target\media" /MIR /R:2 /W:5 /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying photos failed (robocopy $LASTEXITCODE)" }
    robocopy "$root\api\App_Data\keys" "$target\config\keys" /MIR /R:2 /W:5 /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying keys failed (robocopy $LASTEXITCODE)" }
    Copy-Item "$root\api\appsettings.Production.json", "$root\caddy\Caddyfile" "$target\config" -Force

    if (-not $LocalOnly -and (Test-Path $rclone) -and (Test-Path $rcConf)) {
        & $rclone copy $bak 'r2:woodsycart-backups/db' --config $rcConf
        if ($LASTEXITCODE) { throw 'Upload of the database backup to R2 failed' }
        & $rclone sync "$target\media" 'r2:woodsycart-backups/media' --config $rcConf
        if ($LASTEXITCODE) { throw 'Upload of photos to R2 failed' }
        & $rclone sync "$target\config" 'r2:woodsycart-backups/config' --config $rcConf
        if ($LASTEXITCODE) { throw 'Upload of settings to R2 failed' }
    } elseif (-not $LocalOnly) {
        Write-Warning 'rclone is not set up yet - offsite copy skipped (see Phase 8).'
    }

    Get-ChildItem "$root\logs\backup-*.log" | Where-Object LastWriteTime -lt (Get-Date).AddDays(-30) | Remove-Item
    if ($pingUrl -and -not $LocalOnly) { Invoke-WebRequest $pingUrl -UseBasicParsing -TimeoutSec 10 | Out-Null }
    Write-Host "Backup finished: $bak"
}
catch {
    Write-Error $_
    if ($pingUrl -and -not $LocalOnly) { try { Invoke-WebRequest "$pingUrl/fail" -UseBasicParsing -TimeoutSec 10 | Out-Null } catch {} }
    exit 1
}
finally { Stop-Transcript | Out-Null }
# robocopy reports 1 for "files copied"; a finished backup always reports success.
exit 0
