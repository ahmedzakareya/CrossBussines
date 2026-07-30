-- POS-9e: non-blocking sync conflicts (negative stock / inactive item / price diff) noticed while replaying offline orders.
-- Recorded for manager review; never gates the sale. Idempotent.
IF OBJECT_ID('PosSyncConflicts','U') IS NULL
CREATE TABLE PosSyncConflicts (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId INT NOT NULL,
    SyncLogId INT NOT NULL,
    OrderId INT NULL,
    ConflictType NVARCHAR(40) NOT NULL,
    ItemId INT NULL,
    Detail NVARCHAR(400) NULL,
    OfflineValue DECIMAL(19,4) NULL,
    ServerValue DECIMAL(19,4) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Open',
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    AckedByEmployeeId INT NULL,
    AckedAt DATETIME2 NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PosSyncConflicts_Status' AND object_id=OBJECT_ID('PosSyncConflicts'))
    CREATE INDEX IX_PosSyncConflicts_Status ON PosSyncConflicts (CompanyId, Status);
GO
