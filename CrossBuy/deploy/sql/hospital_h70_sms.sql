-- ==========================================================================================
-- Hospital H70 — SMS to patients, before a provider is chosen: the outbox every message is written to (queued until a
-- provider is connected), the editable template per kind, and the patients who asked for no messages.
--   HospitalSmsMessages   one message and its delivery state (one per event: DedupKey unique per company)
--   HospitalSmsTemplates  the words of each kind of message, Arabic and English
--   HospitalSmsOptOuts    a patient who asked for no messages
--
-- Additive only (three new tables). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalSmsMessages','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSmsMessages (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        MessageNo              nvarchar(20)   NOT NULL,
        PatientId              int            NULL,
        Phone                  nvarchar(30)   NULL,
        Kind                   nvarchar(10)   NOT NULL,
        Language               nvarchar(2)    NOT NULL,
        Body                   nvarchar(640)  NOT NULL,
        Status                 nvarchar(10)   NOT NULL,
        SourceType             nvarchar(20)   NULL,
        SourceId               int            NULL,
        DedupKey               nvarchar(60)   NOT NULL,
        QueuedAt               datetime2      NOT NULL,
        QueuedByEmployeeId     int            NULL,
        Attempts               int            NOT NULL,
        SentAt                 datetime2      NULL,
        ProviderRef            nvarchar(100)  NULL,
        Error                  nvarchar(300)  NULL,
        CancelledAt            datetime2      NULL,
        CancelledByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalSmsMessages PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalSmsMessages_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalSmsMessages_Kind CHECK (Kind IN ('Booked','Reminder','NoShow','LabReady','Custom')),
        CONSTRAINT CK_HospitalSmsMessages_Status CHECK (Status IN ('Queued','Sent','Failed','Skipped','Cancelled')),
        CONSTRAINT CK_HospitalSmsMessages_Language CHECK (Language IN ('ar','en')),
        -- a message that can go out has a phone; a skipped one says why; a sent one says when
        CONSTRAINT CK_HospitalSmsMessages_Path CHECK (
            (Status IN ('Queued','Failed') AND Phone IS NOT NULL)
            OR (Status = 'Sent' AND Phone IS NOT NULL AND SentAt IS NOT NULL)
            OR (Status = 'Skipped' AND Error IS NOT NULL)
            OR (Status = 'Cancelled' AND CancelledAt IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalSmsMessages_CompanyId_MessageNo ON dbo.HospitalSmsMessages (CompanyId, MessageNo);
    CREATE UNIQUE INDEX IX_HospitalSmsMessages_CompanyId_DedupKey ON dbo.HospitalSmsMessages (CompanyId, DedupKey);
    CREATE INDEX IX_HospitalSmsMessages_CompanyId_Status ON dbo.HospitalSmsMessages (CompanyId, Status);
    CREATE INDEX IX_HospitalSmsMessages_CompanyId_PatientId ON dbo.HospitalSmsMessages (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalSmsTemplates','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSmsTemplates (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        Kind                 nvarchar(10)   NOT NULL,
        BodyAr               nvarchar(480)  NOT NULL,
        BodyEn               nvarchar(480)  NOT NULL,
        IsActive             bit            NOT NULL,
        UpdatedAt            datetime2      NOT NULL,
        UpdatedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalSmsTemplates PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalSmsTemplates_Kind CHECK (Kind IN ('Booked','Reminder','NoShow','LabReady'))
    );
    CREATE UNIQUE INDEX IX_HospitalSmsTemplates_CompanyId_Kind ON dbo.HospitalSmsTemplates (CompanyId, Kind);
END

IF OBJECT_ID('dbo.HospitalSmsOptOuts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSmsOptOuts (
        ID            int IDENTITY(1,1) NOT NULL,
        CompanyId     int            NOT NULL,
        PatientId     int            NOT NULL,
        Reason        nvarchar(200)  NULL,
        At            datetime2      NOT NULL,
        ByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalSmsOptOuts PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalSmsOptOuts_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalSmsOptOuts_CompanyId_PatientId ON dbo.HospitalSmsOptOuts (CompanyId, PatientId);
END

COMMIT TRANSACTION;
