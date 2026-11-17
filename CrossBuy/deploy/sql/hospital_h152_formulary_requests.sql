-- ==========================================================================================
-- Hospital H152 - formulary governance: a request to give one patient a restricted or non-formulary medicine, decided by
-- the pharmacy and therapeutics committee (approve, or refuse naming a formulary alternative).
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDrugs','U') IS NULL
    THROW 51993, 'Run the drug master slice (H133) first.', 1;

IF OBJECT_ID('dbo.HospitalFormularyRequests','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalFormularyRequests (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        RequestNo nvarchar(20) NOT NULL,
        PatientId int NOT NULL,
        DispenseId int NOT NULL,
        LineId int NOT NULL,
        ItemId int NOT NULL,
        FormularyStatus nvarchar(20) NOT NULL,
        Justification nvarchar(500) NOT NULL,
        Status nvarchar(10) NOT NULL,
        DecisionNote nvarchar(500) NULL,
        AlternativeItemId int NULL,
        RequestedAt datetime2 NOT NULL,
        RequestedByEmployeeId int NULL,
        DecidedAt datetime2 NULL,
        DecidedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalFormularyRequests PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalFormularyRequests_Status CHECK (Status IN ('Pending','Approved','Refused'))
    );
    CREATE UNIQUE INDEX IX_HospitalFormularyRequests_CompanyId_RequestNo ON dbo.HospitalFormularyRequests (CompanyId, RequestNo);
    CREATE INDEX IX_HospitalFormularyRequests_CompanyId_DispenseId ON dbo.HospitalFormularyRequests (CompanyId, DispenseId);
    CREATE INDEX IX_HospitalFormularyRequests_CompanyId_Status ON dbo.HospitalFormularyRequests (CompanyId, Status);
END

COMMIT TRANSACTION;
