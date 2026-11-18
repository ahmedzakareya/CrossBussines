-- ==========================================================================================
-- Hospital H176 - internal calls: voice and video between staff on the hospital's own network.
-- The record of each call (never its audio or picture) and one settings row per company.
--
-- Two new tables; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalCalls','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCalls (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CallerEmployeeId int NOT NULL,
        CalleeEmployeeId int NOT NULL,
        IsVideo bit NOT NULL CONSTRAINT DF_HospitalCalls_IsVideo DEFAULT (0),
        PatientId int NULL,
        Reason nvarchar(200) NULL,
        Status nvarchar(12) NOT NULL,
        MissedWhy nvarchar(12) NULL,
        StartedAt datetime2 NOT NULL,
        AnsweredAt datetime2 NULL,
        EndedAt datetime2 NULL,
        DurationSeconds int NULL,
        Reply nvarchar(120) NULL,
        Note nvarchar(1000) NULL,
        NoteByEmployeeId int NULL,
        SeenAt datetime2 NULL,
        CONSTRAINT PK_HospitalCalls PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalCalls_People CHECK (CallerEmployeeId <> CalleeEmployeeId),
        CONSTRAINT CK_HospitalCalls_Status CHECK (Status IN ('Ringing','Answered','Ended','Missed','Declined','Cancelled'))
    );
    CREATE INDEX IX_HospitalCalls_CompanyId_CallerEmployeeId_StartedAt ON dbo.HospitalCalls (CompanyId, CallerEmployeeId, StartedAt);
    CREATE INDEX IX_HospitalCalls_CompanyId_CalleeEmployeeId_StartedAt ON dbo.HospitalCalls (CompanyId, CalleeEmployeeId, StartedAt);
    CREATE INDEX IX_HospitalCalls_CompanyId_PatientId ON dbo.HospitalCalls (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalCallSettings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCallSettings (
        CompanyId int NOT NULL,
        VideoEnabled bit NOT NULL CONSTRAINT DF_HospitalCallSettings_Video DEFAULT (1),
        ScreenShareEnabled bit NOT NULL CONSTRAINT DF_HospitalCallSettings_Share DEFAULT (1),
        RingSeconds int NOT NULL CONSTRAINT DF_HospitalCallSettings_Ring DEFAULT (30),
        MaxVideoHeight int NOT NULL CONSTRAINT DF_HospitalCallSettings_Height DEFAULT (720),
        TurnUrls nvarchar(300) NULL,
        TurnSecret nvarchar(200) NULL,
        Policy nvarchar(600) NULL,
        UpdatedAt datetime2 NULL,
        UpdatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalCallSettings PRIMARY KEY (CompanyId),
        CONSTRAINT CK_HospitalCallSettings_Ring CHECK (RingSeconds BETWEEN 10 AND 120),
        CONSTRAINT CK_HospitalCallSettings_Height CHECK (MaxVideoHeight IN (480, 720, 1080))
    );
END

COMMIT TRANSACTION;
