-- Run on the DEV machine (SSMS or: sqlcmd -S . -d master -E -i 00_backup_dev_db.sql)
-- Produces a compressed full backup you copy to the server.
-- Note: COMPRESSION requires SQL Server Standard/Enterprise. On Express, remove COMPRESSION.
BACKUP DATABASE CrossBuyDB2
  TO DISK = N'C:\temp\CrossBuyDB2.bak'
  WITH INIT, FORMAT, COMPRESSION, STATS = 5,
       NAME = N'CrossBuyDB2-full-for-deploy';
GO
PRINT 'Backup written to C:\temp\CrossBuyDB2.bak';
