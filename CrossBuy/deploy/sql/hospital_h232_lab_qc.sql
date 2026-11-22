-- ==========================================================================================
-- Hospital H232 - laboratory quality control: control materials (lot, level, target mean and SD per analyte) and
-- their runs, judged by the Westgard rules. A rejected latest run stops patient results for that test.
--
--   HospitalLabQcMaterials, HospitalLabQcRuns   (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalLabAnalytes','U') IS NULL
    THROW 52032, 'Run the hospital lab slice (hospital_h8_lab.sql) first.', 1;

IF OBJECT_ID('dbo.HospitalLabQcMaterials','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalLabQcMaterials (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AnalyteId int NOT NULL,
        Lot nvarchar(40) NOT NULL,
        Level int NOT NULL,
        Mean decimal(18,4) NOT NULL,
        Sd decimal(18,4) NOT NULL,
        ExpiresOn datetime2 NULL,
        IsActive bit NOT NULL,
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalLabQcMaterials PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalLabQcMaterials_Analyte FOREIGN KEY (AnalyteId) REFERENCES dbo.HospitalLabAnalytes (ID),
        CONSTRAINT CK_HospitalLabQcMaterials_Level CHECK (Level BETWEEN 1 AND 3),
        CONSTRAINT CK_HospitalLabQcMaterials_Sd CHECK (Sd > 0)
    );
    CREATE UNIQUE INDEX IX_HospitalLabQcMaterials_CompanyId_AnalyteId_Lot_Level ON dbo.HospitalLabQcMaterials (CompanyId, AnalyteId, Lot, Level);
END

IF OBJECT_ID('dbo.HospitalLabQcRuns','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalLabQcRuns (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        MaterialId int NOT NULL,
        Value decimal(18,4) NOT NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        Status nvarchar(10) NOT NULL,
        Violations nvarchar(60) NULL,
        Action nvarchar(300) NULL,
        CONSTRAINT PK_HospitalLabQcRuns PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalLabQcRuns_Material FOREIGN KEY (MaterialId) REFERENCES dbo.HospitalLabQcMaterials (ID),
        CONSTRAINT CK_HospitalLabQcRuns_Status CHECK (Status IN ('Accepted','Warning','Rejected'))
    );
    CREATE INDEX IX_HospitalLabQcRuns_CompanyId_MaterialId_At ON dbo.HospitalLabQcRuns (CompanyId, MaterialId, At);
END

COMMIT TRANSACTION;
