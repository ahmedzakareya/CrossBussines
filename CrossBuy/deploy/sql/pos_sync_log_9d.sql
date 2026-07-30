-- POS-9d: idempotency log for replayed offline orders — one row per synced localGuid (prevents double-posting). Idempotent.
IF OBJECT_ID('PosSyncLogs','U') IS NULL
CREATE TABLE PosSyncLogs (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId INT NOT NULL,
    LocalGuid NVARCHAR(64) NOT NULL,
    OrderId INT NULL,
    InvoiceId INT NULL,
    ReceiptNo NVARCHAR(80) NULL,
    SyncedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_PosSyncLogs_Guid' AND object_id=OBJECT_ID('PosSyncLogs'))
    CREATE UNIQUE INDEX UX_PosSyncLogs_Guid ON PosSyncLogs (CompanyId, LocalGuid);
GO
