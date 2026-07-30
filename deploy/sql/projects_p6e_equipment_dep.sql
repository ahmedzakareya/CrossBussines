-- Projects & Contracting — P6-هـ: owned-equipment depreciation allocation to a project.
-- Idempotent. Creates the EquipmentDepreciationAllocations source-document table only.
-- No accounting writer, no touch to fixed-asset depreciation tables (FixedAssets / DepreciationRuns / DepreciationLines).

IF OBJECT_ID('dbo.EquipmentDepreciationAllocations','U') IS NULL
BEGIN
    CREATE TABLE dbo.EquipmentDepreciationAllocations
    (
        ID              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EquipmentDepreciationAllocations PRIMARY KEY,
        CompanyID       INT             NOT NULL,
        ProjectId       INT             NOT NULL,
        AllocationNo    INT             NOT NULL,
        FixedAssetId    INT             NOT NULL,
        PeriodDate      DATETIME2       NOT NULL,
        Hours           DECIMAL(19,4)   NULL,
        Rate            DECIMAL(19,4)   NULL,
        Amount          DECIMAL(19,4)   NOT NULL CONSTRAINT DF_EquipDep_Amount DEFAULT (0),
        Note            NVARCHAR(500)   NULL,
        Status          NVARCHAR(20)    NOT NULL CONSTRAINT DF_EquipDep_Status DEFAULT ('Draft'),
        JournalEntryId  INT             NULL,
        CreatedAt       DATETIME2       NULL,
        CreatedBy       INT             NULL,
        PostedAt        DATETIME2       NULL,
        PostedBy        INT             NULL
    );
    CREATE INDEX IX_EquipDep_Project ON dbo.EquipmentDepreciationAllocations (CompanyID, ProjectId);
END
GO
