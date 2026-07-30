-- Projects & Contracting — P0 foundation. ADDITIVE only: extends the existing Projects dimension with nullable
-- contracting columns + a user-defined activity-type lookup. Does NOT touch GL/stock posting → inv-test-integrity stays 0.
-- Idempotent.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- 1) extend Projects (nullable, additive — existing ProjectId plumbing + ProfitabilityAsync keep working)
IF COL_LENGTH('dbo.Projects','CustomerId')      IS NULL ALTER TABLE dbo.Projects ADD CustomerId      int            NULL;
IF COL_LENGTH('dbo.Projects','Location')        IS NULL ALTER TABLE dbo.Projects ADD [Location]      nvarchar(300)  NULL;
IF COL_LENGTH('dbo.Projects','ContractValue')   IS NULL ALTER TABLE dbo.Projects ADD ContractValue   decimal(19,4)  NULL;
IF COL_LENGTH('dbo.Projects','Status')          IS NULL ALTER TABLE dbo.Projects ADD [Status]        nvarchar(30)   NULL;
IF COL_LENGTH('dbo.Projects','ActivityTypeId')  IS NULL ALTER TABLE dbo.Projects ADD ActivityTypeId  int            NULL;
IF COL_LENGTH('dbo.Projects','CostCenterId')    IS NULL ALTER TABLE dbo.Projects ADD CostCenterId    int            NULL;

-- 2) activity-type lookup (starts EMPTY — the user defines their own activity types)
IF OBJECT_ID('dbo.ProjectActivityTypes','U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectActivityTypes (
        ID        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProjectActivityTypes PRIMARY KEY,
        CompanyID int          NOT NULL,
        Code      nvarchar(30) NOT NULL,
        Name      nvarchar(150) NOT NULL,
        NameEn    nvarchar(150) NOT NULL CONSTRAINT DF_ProjectActivityTypes_NameEn DEFAULT(''),
        IsActive  bit          NOT NULL CONSTRAINT DF_ProjectActivityTypes_IsActive DEFAULT(1),
        CreatedAt datetime2    NULL
    );
    CREATE INDEX IX_ProjectActivityTypes_Company ON dbo.ProjectActivityTypes (CompanyID, IsActive);
END
