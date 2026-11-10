-- ==========================================================================================
-- Hospital H64 — restricted files and break-glass access.
--   HospitalRestrictedFiles  a file the administrator restricted (with why); lifted, never erased; one live per patient
--   HospitalBreakGlass       an emergency opening of a restricted file: who, why, until when, and its review
--
-- Additive only (two new tables). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalRestrictedFiles','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRestrictedFiles (
        ID                      int IDENTITY(1,1) NOT NULL,
        CompanyId               int            NOT NULL,
        PatientId               int            NOT NULL,
        Reason                  nvarchar(300)  NOT NULL,
        RestrictedAt            datetime2      NOT NULL,
        RestrictedByEmployeeId  int            NOT NULL,
        LiftedAt                datetime2      NULL,
        LiftedByEmployeeId      int            NULL,
        CONSTRAINT PK_HospitalRestrictedFiles PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRestrictedFiles_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalRestrictedFiles_Lift CHECK ((LiftedAt IS NULL AND LiftedByEmployeeId IS NULL) OR (LiftedAt IS NOT NULL AND LiftedByEmployeeId IS NOT NULL))
    );
    CREATE INDEX IX_HospitalRestrictedFiles_CompanyId_PatientId ON dbo.HospitalRestrictedFiles (CompanyId, PatientId);
    -- one live restriction per patient
    CREATE UNIQUE INDEX UX_HospitalRestrictedFiles_Live ON dbo.HospitalRestrictedFiles (CompanyId, PatientId) WHERE LiftedAt IS NULL;
END

IF OBJECT_ID('dbo.HospitalBreakGlass','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBreakGlass (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        PatientId             int            NOT NULL,
        EmployeeId            int            NOT NULL,
        Reason                nvarchar(500)  NOT NULL,
        OpenedAt              datetime2      NOT NULL,
        ExpiresAt             datetime2      NOT NULL,
        ReviewedAt            datetime2      NULL,
        ReviewedByEmployeeId  int            NULL,
        Outcome               nvarchar(12)   NULL,
        ReviewNote            nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalBreakGlass PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalBreakGlass_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalBreakGlass_Window CHECK (ExpiresAt > OpenedAt),
        -- a review is whole (who, when, the outcome), by someone other than the person who broke the glass
        CONSTRAINT CK_HospitalBreakGlass_Review CHECK ((ReviewedAt IS NULL AND ReviewedByEmployeeId IS NULL AND Outcome IS NULL)
            OR (ReviewedAt IS NOT NULL AND ReviewedByEmployeeId IS NOT NULL AND Outcome IN ('Justified','Unjustified') AND ReviewedByEmployeeId <> EmployeeId))
    );
    CREATE INDEX IX_HospitalBreakGlass_CompanyId_PatientId_EmployeeId ON dbo.HospitalBreakGlass (CompanyId, PatientId, EmployeeId);
    CREATE INDEX IX_HospitalBreakGlass_CompanyId_ReviewedAt ON dbo.HospitalBreakGlass (CompanyId, ReviewedAt);
END

COMMIT TRANSACTION;
