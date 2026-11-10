-- ==========================================================================================
-- Hospital H57 — merging DUPLICATE patient files (the prototype's "medical records: duplicate merge"). The hospital
-- administrator merges one file into another: every record of the duplicate moves to the surviving file in one
-- transaction; the duplicate is NOT deleted — it is closed and points at the survivor, and opening it opens the
-- survivor. Each merge is recorded with what moved.
--   HospitalPatients       + MergedIntoPatientId, + MergedAt
--   HospitalPatientMerges  one row per merge (who, when, which files, why, what moved)
--
-- Additive only (two nullable columns, one new table). Idempotent (COL_LENGTH / OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF COL_LENGTH('dbo.HospitalPatients', 'MergedIntoPatientId') IS NULL
    ALTER TABLE dbo.HospitalPatients ADD MergedIntoPatientId int NULL;
IF COL_LENGTH('dbo.HospitalPatients', 'MergedAt') IS NULL
    ALTER TABLE dbo.HospitalPatients ADD MergedAt datetime2 NULL;

IF OBJECT_ID('dbo.HospitalPatientMerges','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPatientMerges (
        ID                  int IDENTITY(1,1) NOT NULL,
        CompanyId           int             NOT NULL,
        SurvivorPatientId   int             NOT NULL,
        MergedPatientId     int             NOT NULL,
        MergedMrn           nvarchar(20)    NOT NULL,
        Reason              nvarchar(500)   NOT NULL,
        Moved               nvarchar(max)   NOT NULL,
        MergedAt            datetime2       NOT NULL,
        MergedByEmployeeId  int             NOT NULL,
        CONSTRAINT PK_HospitalPatientMerges PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalPatientMerges_Survivor FOREIGN KEY (SurvivorPatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalPatientMerges_Merged FOREIGN KEY (MergedPatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalPatientMerges_Two CHECK (SurvivorPatientId <> MergedPatientId)
    );
    -- a file is merged once
    CREATE UNIQUE INDEX IX_HospitalPatientMerges_MergedPatientId ON dbo.HospitalPatientMerges (MergedPatientId);
    CREATE INDEX IX_HospitalPatientMerges_CompanyId ON dbo.HospitalPatientMerges (CompanyId);
END

COMMIT TRANSACTION;
