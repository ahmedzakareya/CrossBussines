-- ==========================================================================================
-- Hospital H242 - FHIR eligibility and pre-authorisation: each payer's gateway (entered by the administrator when
-- the payer gives it; off until then) and every message as it went and as it came.
--
--   HospitalPayerGateways   (new)
--   HospitalFhirMessages    (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPayers','U') IS NULL
    THROW 52042, 'Run the hospital insurance slice first.', 1;

IF OBJECT_ID('dbo.HospitalPayerGateways','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPayerGateways (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PayerId int NOT NULL,
        BaseUrl nvarchar(300) NOT NULL,
        KeyHeader nvarchar(60) NULL,
        ApiKey nvarchar(500) NULL,
        Enabled bit NOT NULL,
        UpdatedAt datetime2 NOT NULL,
        UpdatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalPayerGateways PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalPayerGateways_Payer FOREIGN KEY (PayerId) REFERENCES dbo.HospitalPayers (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalPayerGateways_CompanyId_PayerId ON dbo.HospitalPayerGateways (CompanyId, PayerId);
END

IF OBJECT_ID('dbo.HospitalFhirMessages','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalFhirMessages (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Kind nvarchar(12) NOT NULL,
        PatientId int NOT NULL,
        PayerId int NOT NULL,
        PreauthId int NULL,
        Request nvarchar(max) NOT NULL,
        Response nvarchar(max) NULL,
        Outcome nvarchar(12) NULL,
        Detail nvarchar(500) NULL,
        Sent bit NOT NULL,
        At datetime2 NOT NULL,
        AnsweredAt datetime2 NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalFhirMessages PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalFhirMessages_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT FK_HospitalFhirMessages_Payer FOREIGN KEY (PayerId) REFERENCES dbo.HospitalPayers (ID),
        CONSTRAINT CK_HospitalFhirMessages_Kind CHECK (Kind IN ('Eligibility','Preauth')),
        CONSTRAINT CK_HospitalFhirMessages_Outcome CHECK (Outcome IS NULL OR Outcome IN ('InForce','NotInForce','Approved','Rejected','Pending','Error'))
    );
    CREATE INDEX IX_HospitalFhirMessages_CompanyId_PatientId_At ON dbo.HospitalFhirMessages (CompanyId, PatientId, At);
    CREATE INDEX IX_HospitalFhirMessages_CompanyId_PreauthId ON dbo.HospitalFhirMessages (CompanyId, PreauthId);
END

COMMIT TRANSACTION;
