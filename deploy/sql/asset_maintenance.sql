-- ============================================================================
-- Asset scheduled maintenance (Level-1). Idempotent. OPEX only — no capitalization, no touch to depreciation.
-- ============================================================================
IF OBJECT_ID('dbo.MaintenanceSchedules','U') IS NULL
CREATE TABLE dbo.MaintenanceSchedules (
    ID             int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID      int NOT NULL,
    AssetId        int NOT NULL,
    Title          nvarchar(200) NOT NULL,
    Type           nvarchar(20) NOT NULL CONSTRAINT DF_MntSch_Type DEFAULT('Preventive'),
    IntervalMonths int NOT NULL CONSTRAINT DF_MntSch_Interval DEFAULT(1),
    NextDueDate    datetime2(7) NOT NULL,
    LastDoneDate   datetime2(7) NULL,
    EstimatedCost  decimal(19,4) NULL,
    IsActive       bit NOT NULL CONSTRAINT DF_MntSch_Active DEFAULT(1),
    CreatedAt      datetime2(7) NULL
);
GO
IF OBJECT_ID('dbo.MaintenanceSchedules','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MntSch_Asset' AND object_id=OBJECT_ID('dbo.MaintenanceSchedules'))
    CREATE INDEX IX_MntSch_Asset ON dbo.MaintenanceSchedules(CompanyID, AssetId, IsActive);
GO
IF OBJECT_ID('dbo.MaintenanceRecords','U') IS NULL
CREATE TABLE dbo.MaintenanceRecords (
    ID             int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID      int NOT NULL,
    AssetId        int NOT NULL,
    ScheduleId     int NULL,
    Date           datetime2(7) NOT NULL,
    Description    nvarchar(500) NULL,
    Cost           decimal(19,4) NOT NULL CONSTRAINT DF_MntRec_Cost DEFAULT(0),
    Vendor         nvarchar(200) NULL,
    Status         nvarchar(15) NOT NULL CONSTRAINT DF_MntRec_Status DEFAULT('Done'),
    Notes          nvarchar(1000) NULL,
    JournalEntryId int NULL,
    ProjectId      int NULL,
    CreatedAt      datetime2(7) NULL
);
GO
IF OBJECT_ID('dbo.MaintenanceRecords','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MntRec_Asset' AND object_id=OBJECT_ID('dbo.MaintenanceRecords'))
    CREATE INDEX IX_MntRec_Asset ON dbo.MaintenanceRecords(CompanyID, AssetId);
GO
-- maintenance-expense account 520110 (clones the dep-expense account 520103's type/parent per company)
INSERT INTO Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive)
SELECT a.CompanyID, '520110', N'مصروف صيانة الأصول', N'Asset maintenance expense', a.AccountTypeId, a.ParentId, 1, 1
FROM Accounts a
WHERE a.Code = '520103'
  AND NOT EXISTS (SELECT 1 FROM Accounts b WHERE b.CompanyID = a.CompanyID AND b.Code = '520110');
GO
