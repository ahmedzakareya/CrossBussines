-- ==========================================================================================
-- Hospital H20 — insurance accounting, fail-closed (the owner's rulings D2 and G1 on H18).
--   D2: what a payer owes sits in its OWN receivable — the payer's override, else the company's. Closing an
--       insured bill is refused while neither is mapped (never the patients' account, never a hard-coded number).
--   G1: the payer's answers post, each once: a short acceptance (adjustment), a rejection (write-off), a
--       resubmission accepted (recovery), a remittance (Dr bank / Cr payer receivable). An answer whose account is
--       unmapped is not recorded.
--
-- THREE NEW TABLES. Nothing existing is altered: until this runs, insured billing and posting answers are refused
-- and everything else works as before. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED, NOT APPLIED. Applying it, and mapping the accounts, are separately authorized.
-- Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPayers','U') IS NULL OR OBJECT_ID('dbo.HospitalClaimBatches','U') IS NULL
   OR OBJECT_ID('dbo.HospitalClaimLines','U') IS NULL OR OBJECT_ID('dbo.HospitalBills','U') IS NULL
   OR OBJECT_ID('dbo.HospitalBillCoverages','U') IS NULL
   OR OBJECT_ID('dbo.Accounts','U') IS NULL OR OBJECT_ID('dbo.JournalEntries','U') IS NULL
    THROW 51990, 'Run hospital_h6_billing.sql, hospital_h11_insurance.sql and hospital_h18_bill_coverage.sql first.', 1;

IF OBJECT_ID('dbo.HospitalInsuranceAccounts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalInsuranceAccounts (
        ID                        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalInsuranceAccounts PRIMARY KEY,
        CompanyId                 int NOT NULL,
        PayerReceivableAccountId  int NULL,
        ClaimRejectionAccountId   int NULL,
        ClaimAdjustmentAccountId  int NULL,
        CONSTRAINT FK_HospitalInsuranceAccounts_Receivable FOREIGN KEY (PayerReceivableAccountId) REFERENCES dbo.Accounts(ID),
        CONSTRAINT FK_HospitalInsuranceAccounts_Rejection FOREIGN KEY (ClaimRejectionAccountId) REFERENCES dbo.Accounts(ID),
        CONSTRAINT FK_HospitalInsuranceAccounts_Adjustment FOREIGN KEY (ClaimAdjustmentAccountId) REFERENCES dbo.Accounts(ID)
    );
    -- one mapping per company
    CREATE UNIQUE INDEX IX_HospitalInsuranceAccounts_CompanyId ON dbo.HospitalInsuranceAccounts (CompanyId);
END

IF OBJECT_ID('dbo.HospitalPayerAccounts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPayerAccounts (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPayerAccounts PRIMARY KEY,
        CompanyId            int NOT NULL,
        PayerId              int NOT NULL,
        ReceivableAccountId  int NOT NULL,
        CONSTRAINT FK_HospitalPayerAccounts_Payer FOREIGN KEY (PayerId) REFERENCES dbo.HospitalPayers(ID),
        CONSTRAINT FK_HospitalPayerAccounts_Account FOREIGN KEY (ReceivableAccountId) REFERENCES dbo.Accounts(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalPayerAccounts_CompanyId_PayerId ON dbo.HospitalPayerAccounts (CompanyId, PayerId);
END

IF OBJECT_ID('dbo.HospitalClaimPostings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClaimPostings (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalClaimPostings PRIMARY KEY,
        CompanyId             int            NOT NULL,
        BatchId               int            NOT NULL,
        ClaimLineId           int            NULL,
        Event                 nvarchar(20)   NOT NULL,
        Reference             nvarchar(60)   NULL,
        Amount                decimal(18,3)  NOT NULL,
        BillId                int            NULL,
        OriginJournalEntryId  int            NULL,
        JournalEntryId        int            NOT NULL,
        PostedAt              datetime2      NOT NULL,
        PostedByEmployeeId    int            NULL,
        CONSTRAINT CK_HospitalClaimPostings_Amount CHECK (Amount > 0),
        -- a line's event, or a batch's remittance (with its reference) — never both, never neither
        CONSTRAINT CK_HospitalClaimPostings_Kind CHECK ((ClaimLineId IS NOT NULL AND Reference IS NULL) OR (ClaimLineId IS NULL AND Reference IS NOT NULL)),
        CONSTRAINT FK_HospitalClaimPostings_Batch FOREIGN KEY (BatchId) REFERENCES dbo.HospitalClaimBatches(ID),
        CONSTRAINT FK_HospitalClaimPostings_Line FOREIGN KEY (ClaimLineId) REFERENCES dbo.HospitalClaimLines(ID),
        CONSTRAINT FK_HospitalClaimPostings_Bill FOREIGN KEY (BillId) REFERENCES dbo.HospitalBills(ID),
        CONSTRAINT FK_HospitalClaimPostings_Origin FOREIGN KEY (OriginJournalEntryId) REFERENCES dbo.JournalEntries(ID),
        CONSTRAINT FK_HospitalClaimPostings_Journal FOREIGN KEY (JournalEntryId) REFERENCES dbo.JournalEntries(ID)
    );
    -- each answer posts once: a line's event, a batch's remittance reference
    CREATE UNIQUE INDEX UX_HospitalClaimPostings_LineEvent ON dbo.HospitalClaimPostings (ClaimLineId, Event) WHERE [ClaimLineId] IS NOT NULL;
    CREATE UNIQUE INDEX UX_HospitalClaimPostings_Remittance ON dbo.HospitalClaimPostings (BatchId, Reference) WHERE [Reference] IS NOT NULL;
    CREATE INDEX IX_HospitalClaimPostings_CompanyId_BatchId ON dbo.HospitalClaimPostings (CompanyId, BatchId);
END

COMMIT TRANSACTION;
