-- ==========================================================================================
-- Hospital H87 — haemodialysis. Phase 4 (specialty modules).
--   HospitalDialysisPlans         a patient's standing prescription: dry weight, access, hours, flows, weekdays, shift
--   HospitalDialysisSessions      one treatment on one day (or a scheduled day missed): before, after, urea
--   HospitalDialysisObservations  the readings during a session: pressure, pulse, flows, volume removed so far
--
-- Additive only (three new tables). Idempotent (OBJECT_ID guards).
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

IF OBJECT_ID('dbo.HospitalDialysisPlans','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDialysisPlans (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        PatientId             int            NOT NULL,
        Status                nvarchar(8)    NOT NULL,
        DryWeightKg           decimal(5,1)   NOT NULL,
        Access                nvarchar(3)    NOT NULL,
        DurationMin           int            NOT NULL,
        BloodFlow             int            NOT NULL,
        DialysateFlow         int            NULL,
        Dialyzer              nvarchar(60)   NULL,
        Anticoagulation       nvarchar(100)  NULL,
        DaysMask              int            NOT NULL,
        Shift                 int            NOT NULL,
        Note                  nvarchar(500)  NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        UpdatedAt             datetime2      NULL,
        UpdatedByEmployeeId   int            NULL,
        StoppedAt             datetime2      NULL,
        CONSTRAINT PK_HospitalDialysisPlans PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDialysisPlans_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalDialysisPlans_Status CHECK (Status IN ('Active','Stopped')),
        CONSTRAINT CK_HospitalDialysisPlans_Access CHECK (Access IN ('AVF','AVG','CVC')),
        CONSTRAINT CK_HospitalDialysisPlans_Dry CHECK (DryWeightKg BETWEEN 20 AND 250),
        CONSTRAINT CK_HospitalDialysisPlans_Duration CHECK (DurationMin BETWEEN 60 AND 480),
        CONSTRAINT CK_HospitalDialysisPlans_Days CHECK (DaysMask BETWEEN 1 AND 127),
        CONSTRAINT CK_HospitalDialysisPlans_Shift CHECK (Shift BETWEEN 1 AND 3)
    );
    CREATE INDEX IX_HospitalDialysisPlans_CompanyId_PatientId ON dbo.HospitalDialysisPlans (CompanyId, PatientId);
    -- one standing prescription per patient
    CREATE UNIQUE INDEX UX_HospitalDialysisPlans_Active ON dbo.HospitalDialysisPlans (CompanyId, PatientId) WHERE Status = 'Active';
END

IF OBJECT_ID('dbo.HospitalDialysisSessions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDialysisSessions (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        PlanId                int            NOT NULL,
        PatientId             int            NOT NULL,
        SessionDate           date           NOT NULL,
        Shift                 int            NOT NULL,
        Station               nvarchar(20)   NULL,
        Status                nvarchar(8)    NOT NULL,
        DryWeightKg           decimal(5,1)   NOT NULL,
        PlannedMin            int            NOT NULL,
        StartedAt             datetime2      NULL,
        EndedAt               datetime2      NULL,
        PreWeightKg           decimal(5,1)   NULL,
        PreSbp                int            NULL,
        PreDbp                int            NULL,
        PrePulse              int            NULL,
        UfGoalMl              int            NULL,
        PostWeightKg          decimal(5,1)   NULL,
        PostSbp               int            NULL,
        PostDbp               int            NULL,
        UfMl                  int            NULL,
        PreUrea               decimal(6,1)   NULL,
        PostUrea              decimal(6,1)   NULL,
        Complications         nvarchar(100)  NULL,
        MissedReason          nvarchar(200)  NULL,
        Note                  nvarchar(500)  NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        ClosedByEmployeeId    int            NULL,
        CONSTRAINT PK_HospitalDialysisSessions PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDialysisSessions_Plan FOREIGN KEY (PlanId) REFERENCES dbo.HospitalDialysisPlans(ID),
        CONSTRAINT FK_HospitalDialysisSessions_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalDialysisSessions_Status CHECK (Status IN ('Running','Done','Missed')),
        CONSTRAINT CK_HospitalDialysisSessions_Started CHECK (Status = 'Missed' OR (StartedAt IS NOT NULL AND PreWeightKg IS NOT NULL)),
        CONSTRAINT CK_HospitalDialysisSessions_Done CHECK (Status <> 'Done' OR (EndedAt IS NOT NULL AND PostWeightKg IS NOT NULL))
    );
    -- one session per patient per day
    CREATE UNIQUE INDEX IX_HospitalDialysisSessions_CompanyId_PatientId_SessionDate ON dbo.HospitalDialysisSessions (CompanyId, PatientId, SessionDate);
    CREATE INDEX IX_HospitalDialysisSessions_CompanyId_SessionDate ON dbo.HospitalDialysisSessions (CompanyId, SessionDate);
END

IF OBJECT_ID('dbo.HospitalDialysisObservations','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDialysisObservations (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        SessionId             int            NOT NULL,
        PatientId             int            NOT NULL,
        At                    datetime2      NOT NULL,
        Sbp                   int            NULL,
        Dbp                   int            NULL,
        Pulse                 int            NULL,
        BloodFlow             int            NULL,
        VenousPressure        int            NULL,
        UfMl                  int            NULL,
        Note                  nvarchar(300)  NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalDialysisObservations PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDialysisObservations_Session FOREIGN KEY (SessionId) REFERENCES dbo.HospitalDialysisSessions(ID),
        CONSTRAINT FK_HospitalDialysisObservations_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE INDEX IX_HospitalDialysisObservations_CompanyId_SessionId_At ON dbo.HospitalDialysisObservations (CompanyId, SessionId, At);
END

COMMIT TRANSACTION;
