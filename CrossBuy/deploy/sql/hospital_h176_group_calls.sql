-- ==========================================================================================
-- Hospital H176 (group calls) - a call with up to four people (each device connected to each other one), the
-- participants of every group call, and the consent of each participant to a group recording.
--
-- Additive only. Idempotent. Needs hospital_h176_calls.sql and hospital_h176_call_recording.sql.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.HospitalCalls', 'IsGroup') IS NULL
    ALTER TABLE dbo.HospitalCalls ADD IsGroup bit NOT NULL CONSTRAINT DF_HospitalCalls_IsGroup DEFAULT (0);

IF COL_LENGTH('dbo.HospitalCallSettings', 'GroupEnabled') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD GroupEnabled bit NOT NULL CONSTRAINT DF_HospitalCallSettings_GroupOn DEFAULT (1);
IF COL_LENGTH('dbo.HospitalCallSettings', 'GroupMax') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD GroupMax int NOT NULL CONSTRAINT DF_HospitalCallSettings_GroupMax DEFAULT (4);

IF OBJECT_ID('dbo.HospitalCallParticipants','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCallParticipants (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CallId int NOT NULL,
        EmployeeId int NOT NULL,
        State nvarchar(10) NOT NULL,
        InvitedAt datetime2 NOT NULL,
        JoinedAt datetime2 NULL,
        LeftAt datetime2 NULL,
        RemovedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalCallParticipants PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalCallParticipants_Call FOREIGN KEY (CallId) REFERENCES dbo.HospitalCalls (ID),
        CONSTRAINT CK_HospitalCallParticipants_State CHECK (State IN ('Host','Invited','Joined','Declined','Missed','Busy','Left','Removed'))
    );
    CREATE UNIQUE INDEX IX_HospitalCallParticipants_CallId_EmployeeId ON dbo.HospitalCallParticipants (CallId, EmployeeId);
    CREATE INDEX IX_HospitalCallParticipants_CompanyId_EmployeeId ON dbo.HospitalCallParticipants (CompanyId, EmployeeId);
END

IF OBJECT_ID('dbo.HospitalCallRecordingConsents','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCallRecordingConsents (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        RecordingId int NOT NULL,
        EmployeeId int NOT NULL,
        At datetime2 NOT NULL,
        Via nvarchar(8) NOT NULL,
        CONSTRAINT PK_HospitalCallRecordingConsents PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalCallRecordingConsents_Recording FOREIGN KEY (RecordingId) REFERENCES dbo.HospitalCallRecordings (ID),
        CONSTRAINT CK_HospitalCallRecordingConsents_Via CHECK (Via IN ('agreed','joined'))
    );
    CREATE UNIQUE INDEX IX_HospitalCallRecordingConsents_RecordingId_EmployeeId ON dbo.HospitalCallRecordingConsents (RecordingId, EmployeeId);
END

COMMIT TRANSACTION;
