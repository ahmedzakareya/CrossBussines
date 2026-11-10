-- ==========================================================================================
-- Hospital H81 — quality indicators. Phase 2 of the 2026-11-09 gap research.
--   HospitalQualityTargets  the hospital's own target per indicator (a setting; absent = the published default)
-- The indicators themselves are computed from the hospital's records and are not stored.
--
-- Additive only (one new table). Idempotent (OBJECT_ID guards). Not a patient table: no prerequisite.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalQualityTargets','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalQualityTargets (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        Indicator             nvarchar(20)   NOT NULL,
        Target                decimal(10,2)  NOT NULL,
        UpdatedAt             datetime2      NOT NULL,
        UpdatedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalQualityTargets PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalQualityTargets_Indicator CHECK (Indicator IN ('Alos','Occupancy','Readmission30','Mortality','Hai','Falls','MedErrors','MedRec24h','ConsultOnTime','ChartOnTime','Satisfaction','Nps')),
        CONSTRAINT CK_HospitalQualityTargets_Target CHECK (Target >= -100 AND Target <= 100000)
    );
    CREATE UNIQUE INDEX IX_HospitalQualityTargets_CompanyId_Indicator ON dbo.HospitalQualityTargets (CompanyId, Indicator);
END

COMMIT TRANSACTION;
