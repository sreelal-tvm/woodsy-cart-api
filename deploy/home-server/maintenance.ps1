<#
  WoodsyCart weekly database tidy-up (Phase 8). Scheduled for Sundays 03:30 by Task Scheduler as SYSTEM.
  Rebuilds indexes and refreshes statistics so searches stay fast. Takes seconds at today's size.
#>
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force 'C:\WoodsyCart\logs' | Out-Null
Start-Transcript -Path "C:\WoodsyCart\logs\maintenance-$(Get-Date -Format yyyyMMdd).log" | Out-Null
try {
    sqlcmd -S localhost\SQLEXPRESS -E -C -b -d WoodsyCartDb -Q @"
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'ALTER INDEX ALL ON ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' REBUILD;'
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0;
EXEC sys.sp_executesql @sql;
EXEC sp_updatestats;
"@
    if ($LASTEXITCODE) { throw 'Index maintenance failed' }
    Get-ChildItem 'C:\WoodsyCart\logs\maintenance-*.log' | Where-Object LastWriteTime -lt (Get-Date).AddDays(-90) | Remove-Item
}
finally { Stop-Transcript | Out-Null }
