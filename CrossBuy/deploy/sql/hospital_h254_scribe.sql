-- ==========================================================================================
-- Hospital H254 - ambient documentation: the trail of each recorded visit (who, which visit, when, the transcript).
-- The recording itself is never stored.
--
--   HospitalScribeLogs   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalEncounters','U') IS NULL
    THROW 52054, 'Run the hospital consultation slice first.', 1;

IF OBJECT_ID('dbo.HospitalScribeLogs','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalScribeLogs (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        EncounterId int NOT NULL,
        PatientId int NOT NULL,
        Transcript nvarchar(max) NOT NULL,
        Language nvarchar(10) NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalScribeLogs PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalScribeLogs_Encounter FOREIGN KEY (EncounterId) REFERENCES dbo.HospitalEncounters (ID),
        CONSTRAINT FK_HospitalScribeLogs_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID)
    );
    CREATE INDEX IX_HospitalScribeLogs_CompanyId_EncounterId ON dbo.HospitalScribeLogs (CompanyId, EncounterId);
    CREATE INDEX IX_HospitalScribeLogs_CompanyId_PatientId ON dbo.HospitalScribeLogs (CompanyId, PatientId);
END

COMMIT TRANSACTION;
