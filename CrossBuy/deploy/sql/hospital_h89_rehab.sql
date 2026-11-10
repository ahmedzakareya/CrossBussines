-- ==========================================================================================
-- Hospital H89 — physiotherapy / rehabilitation. Phase 4 (specialty modules).
--   HospitalRehabEpisodes   a referral, assessed into an episode, discharged with a reason
--   HospitalRehabGoals      the goals of an episode, met or not
--   HospitalRehabSessions   each session: attended (what was done, pain, the measure) or missed
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

IF OBJECT_ID('dbo.HospitalRehabEpisodes','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRehabEpisodes (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        EpisodeNo             nvarchar(20)   NOT NULL,
        PatientId             int            NOT NULL,
        AdmissionId           int            NULL,
        Status                nvarchar(10)   NOT NULL,
        Region                nvarchar(10)   NOT NULL,
        Problem               nvarchar(300)  NOT NULL,
        Precautions           nvarchar(300)  NULL,
        PlannedSessions       int            NOT NULL,
        PerWeek               int            NOT NULL,
        ReferredAt            datetime2      NOT NULL,
        ReferredByEmployeeId  int            NULL,
        AssessedAt            datetime2      NULL,
        TherapistEmployeeId   int            NULL,
        MeasureCode           nvarchar(5)    NULL,
        BaselineScore         decimal(5,1)   NULL,
        BaselinePain          int            NULL,
        Assessment            nvarchar(1000) NULL,
        EndedAt               datetime2      NULL,
        EndReason             nvarchar(15)   NULL,
        Summary               nvarchar(1000) NULL,
        CONSTRAINT PK_HospitalRehabEpisodes PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRehabEpisodes_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalRehabEpisodes_Status CHECK (Status IN ('Referred','Active','Discharged','Cancelled')),
        CONSTRAINT CK_HospitalRehabEpisodes_Measure CHECK (MeasureCode IS NULL OR MeasureCode IN ('ODI','NDI','LEFS','DASH','BI')),
        CONSTRAINT CK_HospitalRehabEpisodes_Pain CHECK (BaselinePain IS NULL OR BaselinePain BETWEEN 0 AND 10),
        CONSTRAINT CK_HospitalRehabEpisodes_Plan CHECK (PlannedSessions BETWEEN 1 AND 60 AND PerWeek BETWEEN 1 AND 7),
        CONSTRAINT CK_HospitalRehabEpisodes_Assessed CHECK (Status IN ('Referred','Cancelled') OR (AssessedAt IS NOT NULL AND MeasureCode IS NOT NULL AND BaselineScore IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalRehabEpisodes_CompanyId_EpisodeNo ON dbo.HospitalRehabEpisodes (CompanyId, EpisodeNo);
    CREATE INDEX IX_HospitalRehabEpisodes_CompanyId_PatientId ON dbo.HospitalRehabEpisodes (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalRehabGoals','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRehabGoals (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        EpisodeId             int            NOT NULL,
        PatientId             int            NOT NULL,
        Text                  nvarchar(300)  NOT NULL,
        TargetDate            date           NULL,
        Status                nvarchar(6)    NOT NULL,
        CreatedAt             datetime2      NOT NULL,
        ClosedAt              datetime2      NULL,
        CONSTRAINT PK_HospitalRehabGoals PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRehabGoals_Episode FOREIGN KEY (EpisodeId) REFERENCES dbo.HospitalRehabEpisodes(ID),
        CONSTRAINT FK_HospitalRehabGoals_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalRehabGoals_Status CHECK (Status IN ('Open','Met','NotMet'))
    );
    CREATE INDEX IX_HospitalRehabGoals_CompanyId_EpisodeId ON dbo.HospitalRehabGoals (CompanyId, EpisodeId);
END

IF OBJECT_ID('dbo.HospitalRehabSessions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRehabSessions (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        EpisodeId             int            NOT NULL,
        PatientId             int            NOT NULL,
        SessionDate           date           NOT NULL,
        Attendance            nvarchar(9)    NOT NULL,
        Minutes               int            NULL,
        Interventions         nvarchar(120)  NULL,
        PainBefore            int            NULL,
        PainAfter             int            NULL,
        Score                 decimal(5,1)   NULL,
        Note                  nvarchar(500)  NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalRehabSessions PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRehabSessions_Episode FOREIGN KEY (EpisodeId) REFERENCES dbo.HospitalRehabEpisodes(ID),
        CONSTRAINT FK_HospitalRehabSessions_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalRehabSessions_Attendance CHECK (Attendance IN ('Attended','NoShow','Cancelled')),
        CONSTRAINT CK_HospitalRehabSessions_Pain CHECK ((PainBefore IS NULL OR PainBefore BETWEEN 0 AND 10) AND (PainAfter IS NULL OR PainAfter BETWEEN 0 AND 10))
    );
    CREATE INDEX IX_HospitalRehabSessions_CompanyId_EpisodeId_SessionDate ON dbo.HospitalRehabSessions (CompanyId, EpisodeId, SessionDate);
END

COMMIT TRANSACTION;
