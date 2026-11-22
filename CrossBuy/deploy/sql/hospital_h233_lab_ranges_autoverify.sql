-- ==========================================================================================
-- Hospital H233 - reference ranges by age and sex, the delta check and autoverification.
--
--   HospitalLabRefRanges                         (new)
--   HospitalLabAnalytes.DeltaPercent, AutoVerify (new columns)
--   HospitalLabResults.DeltaPrevious, DeltaFailed (new columns)
--   HospitalLabOrders.AutoVerified               (new column)
--
-- Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalLabAnalytes','U') IS NULL
    THROW 52033, 'Run the hospital lab slice (hospital_h8_lab.sql) first.', 1;

IF COL_LENGTH('dbo.HospitalLabAnalytes','DeltaPercent') IS NULL ALTER TABLE dbo.HospitalLabAnalytes ADD DeltaPercent decimal(9,2) NULL;
IF COL_LENGTH('dbo.HospitalLabAnalytes','AutoVerify') IS NULL ALTER TABLE dbo.HospitalLabAnalytes ADD AutoVerify bit NOT NULL CONSTRAINT DF_HospitalLabAnalytes_AutoVerify DEFAULT(0);
IF COL_LENGTH('dbo.HospitalLabResults','DeltaPrevious') IS NULL ALTER TABLE dbo.HospitalLabResults ADD DeltaPrevious decimal(18,4) NULL;
IF COL_LENGTH('dbo.HospitalLabResults','DeltaFailed') IS NULL ALTER TABLE dbo.HospitalLabResults ADD DeltaFailed bit NOT NULL CONSTRAINT DF_HospitalLabResults_DeltaFailed DEFAULT(0);
IF COL_LENGTH('dbo.HospitalLabOrders','AutoVerified') IS NULL ALTER TABLE dbo.HospitalLabOrders ADD AutoVerified bit NOT NULL CONSTRAINT DF_HospitalLabOrders_AutoVerified DEFAULT(0);

IF OBJECT_ID('dbo.HospitalLabRefRanges','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalLabRefRanges (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AnalyteId int NOT NULL,
        Sex nvarchar(1) NULL,
        AgeFromDays int NOT NULL,
        AgeToDays int NULL,
        RefLow decimal(18,4) NULL,
        RefHigh decimal(18,4) NULL,
        CriticalLow decimal(18,4) NULL,
        CriticalHigh decimal(18,4) NULL,
        CONSTRAINT PK_HospitalLabRefRanges PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalLabRefRanges_Analyte FOREIGN KEY (AnalyteId) REFERENCES dbo.HospitalLabAnalytes (ID),
        CONSTRAINT CK_HospitalLabRefRanges_Sex CHECK (Sex IS NULL OR Sex IN ('M','F')),
        CONSTRAINT CK_HospitalLabRefRanges_Age CHECK (AgeFromDays >= 0 AND (AgeToDays IS NULL OR AgeToDays > AgeFromDays))
    );
    CREATE INDEX IX_HospitalLabRefRanges_CompanyId_AnalyteId ON dbo.HospitalLabRefRanges (CompanyId, AnalyteId);
END

COMMIT TRANSACTION;
