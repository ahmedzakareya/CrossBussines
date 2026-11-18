-- ==========================================================================================
-- Hospital H176 (recording) - a call recorded with every side's consent inside the call.
-- Four columns on HospitalCallSettings (on/off, voice or both, days kept, who may ask), the recordings
-- (who asked, who agreed, the document on the platform) and every opening of one.
--
-- Additive only; nothing existing changes meaning. Idempotent. Needs hospital_h176_calls.sql.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.HospitalCallSettings', 'RecordingEnabled') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD RecordingEnabled bit NOT NULL CONSTRAINT DF_HospitalCallSettings_RecOn DEFAULT (0);
IF COL_LENGTH('dbo.HospitalCallSettings', 'RecordingMode') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD RecordingMode nvarchar(8) NOT NULL CONSTRAINT DF_HospitalCallSettings_RecMode DEFAULT (N'both');
IF COL_LENGTH('dbo.HospitalCallSettings', 'RecordingDays') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD RecordingDays int NOT NULL CONSTRAINT DF_HospitalCallSettings_RecDays DEFAULT (90);
IF COL_LENGTH('dbo.HospitalCallSettings', 'RecordingGroups') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD RecordingGroups nvarchar(200) NULL;

IF OBJECT_ID('dbo.HospitalCallRecordings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCallRecordings (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CallId int NOT NULL,
        RequestedByEmployeeId int NOT NULL,
        RequestedAt datetime2 NOT NULL,
        AgreedByEmployeeId int NULL,
        AgreedAt datetime2 NULL,
        Status nvarchar(12) NOT NULL,
        Kind nvarchar(8) NOT NULL,
        StartedAt datetime2 NULL,
        EndedAt datetime2 NULL,
        StoppedByEmployeeId int NULL,
        DurationSeconds int NULL,
        SizeBytes bigint NULL,
        Chunks int NOT NULL CONSTRAINT DF_HospitalCallRecordings_Chunks DEFAULT (0),
        TempName nvarchar(40) NULL,
        DocumentId bigint NULL,
        DeleteAfter datetime2 NULL,
        DeletedAt datetime2 NULL,
        CONSTRAINT PK_HospitalCallRecordings PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalCallRecordings_Call FOREIGN KEY (CallId) REFERENCES dbo.HospitalCalls (ID),
        CONSTRAINT CK_HospitalCallRecordings_Status CHECK (Status IN ('Asked','Refused','Recording','Saved','Failed','Deleted')),
        CONSTRAINT CK_HospitalCallRecordings_Kind CHECK (Kind IN ('voice','video'))
    );
    CREATE INDEX IX_HospitalCallRecordings_CompanyId_CallId ON dbo.HospitalCallRecordings (CompanyId, CallId);
    CREATE INDEX IX_HospitalCallRecordings_CompanyId_Status_DeleteAfter ON dbo.HospitalCallRecordings (CompanyId, Status, DeleteAfter);
END

IF OBJECT_ID('dbo.HospitalCallRecordingViews','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCallRecordingViews (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        RecordingId int NOT NULL,
        EmployeeId int NOT NULL,
        At datetime2 NOT NULL,
        CONSTRAINT PK_HospitalCallRecordingViews PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalCallRecordingViews_Recording FOREIGN KEY (RecordingId) REFERENCES dbo.HospitalCallRecordings (ID)
    );
    CREATE INDEX IX_HospitalCallRecordingViews_CompanyId_RecordingId ON dbo.HospitalCallRecordingViews (CompanyId, RecordingId);
END

COMMIT TRANSACTION;
