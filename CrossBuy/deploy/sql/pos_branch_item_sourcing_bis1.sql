-- BIS-1: per-branch item sourcing method (setup only, no GL/stock). Idempotent, additive.
IF OBJECT_ID('BranchItemSourcings','U') IS NULL
CREATE TABLE BranchItemSourcings (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    BranchId INT NOT NULL,
    ItemId INT NOT NULL,
    Method NVARCHAR(40) NOT NULL DEFAULT 'WorkOrder',
    SourceBranchId INT NULL,
    SemiFinishedItemId INT NULL,
    TransferTiming NVARCHAR(20) NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    Notes NVARCHAR(500) NULL,
    CreatedAt DATETIME2 NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_BranchItemSourcings_Branch_Item' AND object_id=OBJECT_ID('BranchItemSourcings'))
    CREATE UNIQUE INDEX UX_BranchItemSourcings_Branch_Item ON BranchItemSourcings (BranchId, ItemId);
GO
