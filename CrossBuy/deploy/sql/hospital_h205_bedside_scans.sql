-- ==========================================================================================
-- Hospital H205 - bedside barcode verification: every scan at the bedside, the ones that stopped a step included.
--
--   HospitalBedsideScans   (new) wristband + pack (or specimen, blood unit, baby band), against which patient and
--                          step, and what came of it (Matched / Override / Refused)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52005, 'Run the patient registry slice first.', 1;

IF OBJECT_ID('dbo.HospitalBedsideScans','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBedsideScans (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Kind nvarchar(12) NOT NULL,
        PatientId int NOT NULL,
        AdmissionId int NULL,
        RefId int NULL,
        DoneId int NULL,
        Wristband nvarchar(60) NULL,
        Item nvarchar(120) NULL,
        Gtin nvarchar(14) NULL,
        BatchNo nvarchar(20) NULL,
        Expiry datetime2 NULL,
        Outcome nvarchar(10) NOT NULL,
        Detail nvarchar(300) NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalBedsideScans PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalBedsideScans_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalBedsideScans_Kind CHECK (Kind IN ('Medication', 'Specimen', 'Blood', 'Baby')),
        CONSTRAINT CK_HospitalBedsideScans_Outcome CHECK (Outcome IN ('Matched', 'Override', 'Refused'))
    );
    CREATE INDEX IX_HospitalBedsideScans_CompanyId_Kind_At ON dbo.HospitalBedsideScans (CompanyId, Kind, At);
    CREATE INDEX IX_HospitalBedsideScans_CompanyId_PatientId ON dbo.HospitalBedsideScans (CompanyId, PatientId);
END

COMMIT TRANSACTION;
