-- ==========================================================================================
-- Hospital H225 - the Ministry of Health gateway: every sick leave or notifiable case sent to the Ministry's system
-- (or its intermediary) is kept — when, by whom, what came back (the Ministry's reference) or why it failed.
--
--   HospitalMohSubmissions   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52025, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalMohSubmissions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMohSubmissions (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Kind nvarchar(12) NOT NULL,
        RecordId int NOT NULL,
        PatientId int NOT NULL,
        Status nvarchar(10) NOT NULL,
        HttpStatus int NULL,
        Reference nvarchar(60) NULL,
        Error nvarchar(500) NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalMohSubmissions PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalMohSubmissions_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalMohSubmissions_Kind CHECK (Kind IN ('SickLeave', 'Disease')),
        CONSTRAINT CK_HospitalMohSubmissions_Status CHECK (Status IN ('Accepted', 'Failed')),
        CONSTRAINT CK_HospitalMohSubmissions_Accepted CHECK (Status <> 'Accepted' OR Reference IS NOT NULL)
    );
    CREATE INDEX IX_HospitalMohSubmissions_CompanyId_Kind_RecordId ON dbo.HospitalMohSubmissions (CompanyId, Kind, RecordId);
    CREATE INDEX IX_HospitalMohSubmissions_CompanyId_PatientId ON dbo.HospitalMohSubmissions (CompanyId, PatientId);
END

COMMIT TRANSACTION;
