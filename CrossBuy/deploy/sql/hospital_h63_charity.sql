-- ==========================================================================================
-- Hospital H63 — charity cases: a charity fund waives part of what a patient owes on a closed bill, requested by the
-- billing desk, approved (or refused) by the hospital administrator — never the same person. Approving posts the
-- waiver to the fund's expense account against the patients' receivable.
--   HospitalCharityFunds   the funds (hospital charity, zakat, an outside donor…) and their expense accounts
--   HospitalCharityGrants  one request: the bill, the fund, the amount, the reason, and its single decision
--
-- Additive only (two new tables). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalBills','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql and hospital_h6_billing.sql first.', 1;

IF OBJECT_ID('dbo.HospitalCharityFunds','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCharityFunds (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        Code                 nvarchar(20)   NOT NULL,
        Name                 nvarchar(150)  NOT NULL,
        NameEn               nvarchar(150)  NOT NULL,
        AccountId            int            NULL,
        IsActive             bit            NOT NULL,
        UpdatedAt            datetime2      NOT NULL,
        UpdatedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalCharityFunds PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalCharityFunds_CompanyId_Code ON dbo.HospitalCharityFunds (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalCharityGrants','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCharityGrants (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        GrantNo                nvarchar(20)   NOT NULL,
        BillId                 int            NOT NULL,
        PatientId              int            NOT NULL,
        FundId                 int            NOT NULL,
        Amount                 decimal(18,4)  NOT NULL,
        Reason                 nvarchar(500)  NOT NULL,
        Status                 nvarchar(10)   NOT NULL,
        RequestedAt            datetime2      NOT NULL,
        RequestedByEmployeeId  int            NOT NULL,
        DecidedAt              datetime2      NULL,
        DecidedByEmployeeId    int            NULL,
        DecisionNote           nvarchar(300)  NULL,
        JournalEntryId         int            NULL,
        CONSTRAINT PK_HospitalCharityGrants PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalCharityGrants_Bill FOREIGN KEY (BillId) REFERENCES dbo.HospitalBills(ID),
        CONSTRAINT FK_HospitalCharityGrants_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalCharityGrants_Fund FOREIGN KEY (FundId) REFERENCES dbo.HospitalCharityFunds(ID),
        CONSTRAINT CK_HospitalCharityGrants_Amount CHECK (Amount > 0),
        CONSTRAINT CK_HospitalCharityGrants_Status CHECK (Status IN ('Requested','Approved','Refused')),
        -- a decision is made once, by someone other than the requester; an approval carries its journal
        CONSTRAINT CK_HospitalCharityGrants_Decision CHECK ((Status = 'Requested' AND DecidedAt IS NULL AND DecidedByEmployeeId IS NULL)
            OR (Status <> 'Requested' AND DecidedAt IS NOT NULL AND DecidedByEmployeeId IS NOT NULL AND DecidedByEmployeeId <> RequestedByEmployeeId)),
        CONSTRAINT CK_HospitalCharityGrants_Journal CHECK (Status <> 'Approved' OR JournalEntryId IS NOT NULL)
    );
    CREATE UNIQUE INDEX IX_HospitalCharityGrants_CompanyId_GrantNo ON dbo.HospitalCharityGrants (CompanyId, GrantNo);
    CREATE INDEX IX_HospitalCharityGrants_CompanyId_BillId ON dbo.HospitalCharityGrants (CompanyId, BillId);
    CREATE INDEX IX_HospitalCharityGrants_CompanyId_PatientId ON dbo.HospitalCharityGrants (CompanyId, PatientId);
END

COMMIT TRANSACTION;
