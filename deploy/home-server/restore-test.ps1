<#
  WoodsyCart monthly restore test (Phase 8). Restores the newest backup as WoodsyCartDb_test, shows what is in it,
  then removes the test copy. The live database is never touched.
#>
$ErrorActionPreference = 'Stop'
$sql = 'localhost\SQLEXPRESS'
$bak = Get-ChildItem 'H:\WoodsyCartBackups\db\*.bak' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $bak) { throw 'No backup found in H:\WoodsyCartBackups\db' }
Write-Host "Testing $($bak.Name) ($([math]::Round($bak.Length / 1MB, 1)) MB, $($bak.LastWriteTime))"

$data = (sqlcmd -S $sql -E -C -h -1 -W -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(300))").Trim()
sqlcmd -S $sql -E -C -b -Q @"
RESTORE DATABASE WoodsyCartDb_test FROM DISK = N'$($bak.FullName)' WITH
  MOVE 'WoodsyCartDb' TO N'${data}WoodsyCartDb_test.mdf',
  MOVE 'WoodsyCartDb_log' TO N'${data}WoodsyCartDb_test_log.ldf',
  REPLACE, CHECKSUM, RECOVERY;
"@
if ($LASTEXITCODE) { throw 'Restore failed - the backup may be damaged. Check the newest backup log in C:\WoodsyCart\logs.' }
try {
    sqlcmd -S $sql -E -C -W -Q @"
SET NOCOUNT ON;
SELECT (SELECT COUNT(*) FROM WoodsyCartDb_test.dbo.Products) AS products,
       (SELECT COUNT(*) FROM WoodsyCartDb_test.dbo.Orders) AS orders,
       (SELECT COUNT(*) FROM WoodsyCartDb_test.dbo.Users) AS customers,
       (SELECT MAX(CreatedAt) FROM WoodsyCartDb_test.dbo.Orders) AS newest_order_utc;
"@
    Write-Host 'Restore test passed: the numbers above should match the admin dashboard.' -ForegroundColor Green
}
finally {
    sqlcmd -S $sql -E -C -Q "ALTER DATABASE WoodsyCartDb_test SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE WoodsyCartDb_test;" | Out-Null
}
