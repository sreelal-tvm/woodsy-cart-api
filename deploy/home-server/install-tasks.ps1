<#
  WoodsyCart home server: schedules the nightly backup and the weekly database tidy-up (Phase 8).
  Run once, in PowerShell "as administrator". Safe to run again.
#>
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$scripts = 'C:\WoodsyCart\scripts'
$sql = 'localhost\SQLEXPRESS'

# SQL Server writes the backup files itself, so its own account needs the backup folder.
New-Item -ItemType Directory -Force 'H:\WoodsyCartBackups\db', 'H:\WoodsyCartBackups\media', 'H:\WoodsyCartBackups\config' | Out-Null
icacls 'H:\WoodsyCartBackups' /grant 'NT Service\MSSQL$SQLEXPRESS:(OI)(CI)M' | Out-Null

# The tasks run as SYSTEM, which SQL Server doesn't trust by default: allow it to back up, verify and tidy WoodsyCartDb.
sqlcmd -S $sql -E -C -b -Q @"
IF SUSER_ID(N'NT AUTHORITY\SYSTEM') IS NULL CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
ALTER SERVER ROLE dbcreator ADD MEMBER [NT AUTHORITY\SYSTEM];
USE WoodsyCartDb;
IF USER_ID(N'NT AUTHORITY\SYSTEM') IS NULL CREATE USER [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM];
ALTER ROLE db_owner ADD MEMBER [NT AUTHORITY\SYSTEM];
"@
if ($LASTEXITCODE) { throw 'Could not give the scheduled tasks access to the database.' }

function Register($name, $script, $trigger) {
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scripts\$script`""
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)
    Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
    Write-Host "Scheduled: $name"
}
Register 'WoodsyCart nightly backup' 'backup.ps1' (New-ScheduledTaskTrigger -Daily -At '02:30')
Register 'WoodsyCart weekly maintenance' 'maintenance.ps1' (New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At '03:30')

Write-Host "`nRunning the backup once now..."
Start-ScheduledTask -TaskName 'WoodsyCart nightly backup'
Start-Sleep -Seconds 20
Get-ScheduledTaskInfo -TaskName 'WoodsyCart nightly backup' | Format-List LastRunTime, LastTaskResult
Get-ChildItem 'H:\WoodsyCartBackups\db\*.bak' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 Name, Length, LastWriteTime
Write-Host 'LastTaskResult 0 means success. The log is in C:\WoodsyCart\logs.'
