-- ==========================================================================================
-- Hospital H45 — incident reports & patient safety (the prototype's #/incidents). Any staff member reports, anonymously
-- if they wish (then no reporter is kept at all); a moderate or severe harm opens a root-cause analysis due in 45 days;
-- quality management reviews, analyses and closes.
--   HospitalIncidents  one row per report
--
-- New table only; nothing existing changes. Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalWards','U') IS NULL
    THROW 51990, 'Run hospital_h1_facility.sql and hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalIncidents','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalIncidents (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int             NOT NULL,
        IncidentNo            nvarchar(20)    NOT NULL,
        Kind                  nvarchar(20)    NOT NULL,
        Harm                  nvarchar(10)    NOT NULL,
        Status                nvarchar(10)    NOT NULL,
        Place                 nvarchar(20)    NOT NULL,
        WardId                int             NULL,
        PatientId             int             NULL,
        Description           nvarchar(2000)  NOT NULL,
        ReportedAt            datetime2       NOT NULL,
        ReporterEmployeeId    int             NULL,
        Anonymous             bit             NOT NULL,
        RcaDueOn              date            NULL,
        RootCause             nvarchar(2000)  NULL,
        Actions               nvarchar(2000)  NULL,
        Outcome               nvarchar(1000)  NULL,
        ReviewedAt            datetime2       NULL,
        ReviewedByEmployeeId  int             NULL,
        ClosedAt              datetime2       NULL,
        ClosedByEmployeeId    int             NULL,
        CONSTRAINT PK_HospitalIncidents PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalIncidents_Ward FOREIGN KEY (WardId) REFERENCES dbo.HospitalWards(ID),
        CONSTRAINT FK_HospitalIncidents_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalIncidents_Kind CHECK (Kind IN (N'MedicationError', N'Fall', N'WrongPatient', N'EquipmentFailure', N'Infection', N'Other')),
        CONSTRAINT CK_HospitalIncidents_Harm CHECK (Harm IN (N'NearMiss', N'NoHarm', N'Mild', N'Moderate', N'Severe')),
        CONSTRAINT CK_HospitalIncidents_Status CHECK (Status IN (N'New', N'Review', N'Rca', N'Closed')),
        CONSTRAINT CK_HospitalIncidents_Place CHECK (Place IN (N'Ward', N'Emergency', N'Theatre', N'Laboratory', N'Imaging', N'Pharmacy', N'Clinics', N'Other')),
        CONSTRAINT CK_HospitalIncidents_WardPlace CHECK ((Place = N'Ward' AND WardId IS NOT NULL) OR (Place <> N'Ward' AND WardId IS NULL)),
        -- an anonymous report keeps no reporter
        CONSTRAINT CK_HospitalIncidents_Anonymous CHECK (Anonymous = 0 OR ReporterEmployeeId IS NULL),
        -- a serious harm is always analysed
        CONSTRAINT CK_HospitalIncidents_SeriousRca CHECK (Harm NOT IN (N'Moderate', N'Severe') OR Status IN (N'Rca', N'Closed'))
    );
    CREATE UNIQUE INDEX IX_HospitalIncidents_CompanyId_IncidentNo ON dbo.HospitalIncidents (CompanyId, IncidentNo);
    CREATE INDEX IX_HospitalIncidents_CompanyId_ReportedAt ON dbo.HospitalIncidents (CompanyId, ReportedAt);
END

COMMIT TRANSACTION;
