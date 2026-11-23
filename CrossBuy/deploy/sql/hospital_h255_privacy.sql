-- ==========================================================================================
-- Hospital H255 - behavioural privacy monitoring: alerts read from the record-access log (same family name, no
-- care link, unusual volume, a restricted file opened after hours) and the privacy officer's review.
--
--   HospitalPrivacyAlerts   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52055, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalPrivacyAlerts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPrivacyAlerts (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        EmployeeId int NOT NULL,
        PatientId int NULL,
        Kind nvarchar(12) NOT NULL,
        Day date NOT NULL,
        Count int NOT NULL,
        Status nvarchar(10) NOT NULL,
        Note nvarchar(500) NULL,
        CreatedAt datetime2 NOT NULL,
        ReviewedAt datetime2 NULL,
        ReviewedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalPrivacyAlerts PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalPrivacyAlerts_Kind CHECK (Kind IN ('SameFamily','NoCareLink','Volume','AfterHours')),
        CONSTRAINT CK_HospitalPrivacyAlerts_Status CHECK (Status IN ('Open','Cleared','Confirmed'))
    );
    CREATE UNIQUE INDEX IX_HospitalPrivacyAlerts_CompanyId_EmployeeId_PatientId_Kind_Day ON dbo.HospitalPrivacyAlerts (CompanyId, EmployeeId, PatientId, Kind, Day);
    CREATE INDEX IX_HospitalPrivacyAlerts_CompanyId_Status ON dbo.HospitalPrivacyAlerts (CompanyId, Status);
END

COMMIT TRANSACTION;
