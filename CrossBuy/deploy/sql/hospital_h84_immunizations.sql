-- ==========================================================================================
-- Hospital H84 — immunizations. Phase 4 (the owner's first specialty module, 2026-11-10).
--   HospitalVaccineScheduleItems  the schedule (a setting): one dose of one vaccine at an age in days
--   HospitalImmunizations         the doses given: what, which dose, when, lot and expiry, site, route, who, any AEFI
--
-- Additive only (two new tables). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalVaccineScheduleItems','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalVaccineScheduleItems (
        ID          int IDENTITY(1,1) NOT NULL,
        CompanyId   int            NOT NULL,
        Code        nvarchar(12)   NOT NULL,
        Name        nvarchar(100)  NOT NULL,
        NameEn      nvarchar(100)  NOT NULL,
        DoseNo      int            NOT NULL,
        AgeDays     int            NOT NULL,
        Route       nvarchar(6)    NOT NULL,
        IsActive    bit            NOT NULL,
        SortOrder   int            NOT NULL,
        CONSTRAINT PK_HospitalVaccineScheduleItems PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalVaccineScheduleItems_Route CHECK (Route IN ('IM','SC','ID','Oral','Nasal')),
        CONSTRAINT CK_HospitalVaccineScheduleItems_Numbers CHECK (DoseNo BETWEEN 1 AND 10 AND AgeDays BETWEEN 0 AND 36500)
    );
    CREATE UNIQUE INDEX IX_HospitalVaccineScheduleItems_CompanyId_Code_DoseNo ON dbo.HospitalVaccineScheduleItems (CompanyId, Code, DoseNo);
END

IF OBJECT_ID('dbo.HospitalImmunizations','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalImmunizations (
        ID                  int IDENTITY(1,1) NOT NULL,
        CompanyId           int            NOT NULL,
        PatientId           int            NOT NULL,
        ScheduleItemId      int            NULL,
        Code                nvarchar(12)   NOT NULL,
        Name                nvarchar(100)  NOT NULL,
        NameEn              nvarchar(100)  NOT NULL,
        DoseNo              int            NOT NULL,
        GivenAt             datetime2      NOT NULL,
        Lot                 nvarchar(40)   NULL,
        LotExpiry           date           NULL,
        Site                nvarchar(60)   NULL,
        Route               nvarchar(6)    NOT NULL,
        GivenByEmployeeId   int            NULL,
        AdverseEvent        nvarchar(300)  NULL,
        CreatedAt           datetime2      NOT NULL,
        CONSTRAINT PK_HospitalImmunizations PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalImmunizations_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalImmunizations_Schedule FOREIGN KEY (ScheduleItemId) REFERENCES dbo.HospitalVaccineScheduleItems(ID),
        CONSTRAINT CK_HospitalImmunizations_Route CHECK (Route IN ('IM','SC','ID','Oral','Nasal')),
        CONSTRAINT CK_HospitalImmunizations_Dose CHECK (DoseNo BETWEEN 1 AND 10),
        -- an expired lot is never given
        CONSTRAINT CK_HospitalImmunizations_Lot CHECK (LotExpiry IS NULL OR LotExpiry >= CAST(GivenAt AS date))
    );
    CREATE INDEX IX_HospitalImmunizations_CompanyId_PatientId ON dbo.HospitalImmunizations (CompanyId, PatientId);
    -- a schedule dose is given once
    CREATE UNIQUE INDEX UX_HospitalImmunizations_ScheduleDose ON dbo.HospitalImmunizations (CompanyId, PatientId, ScheduleItemId) WHERE ScheduleItemId IS NOT NULL;
END

COMMIT TRANSACTION;
