-- ==========================================================================================
-- Hospital H148 - credit notes on a closed bill: what the patient no longer owes after closing (medicine brought back),
-- as a document of its own with its lines; the closed bill and its posted revenue never change.
--
-- Two new tables; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDispenseReturns','U') IS NULL
    THROW 51991, 'Run hospital_h146_dispense_returns.sql first.', 1;

IF OBJECT_ID('dbo.HospitalCreditNotes','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCreditNotes (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        NoteNo nvarchar(20) NOT NULL,
        BillId int NOT NULL,
        PatientId int NOT NULL,
        SourceType nvarchar(40) NOT NULL,
        SourceId int NOT NULL,
        Net decimal(18,4) NOT NULL,
        Vat decimal(18,4) NOT NULL,
        Total decimal(18,4) NOT NULL,
        Reason nvarchar(300) NOT NULL,
        JournalEntryId int NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalCreditNotes PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalCreditNotes_Total CHECK (Total > 0 AND Net >= 0 AND Vat >= 0)
    );
    CREATE UNIQUE INDEX IX_HospitalCreditNotes_CompanyId_NoteNo ON dbo.HospitalCreditNotes (CompanyId, NoteNo);
    CREATE INDEX IX_HospitalCreditNotes_CompanyId_BillId ON dbo.HospitalCreditNotes (CompanyId, BillId);
END

IF OBJECT_ID('dbo.HospitalCreditNoteLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCreditNoteLines (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        CreditNoteId int NOT NULL,
        BillLineId int NOT NULL,
        Quantity decimal(18,4) NOT NULL,
        UnitPrice decimal(18,4) NOT NULL,
        Net decimal(18,4) NOT NULL,
        Vat decimal(18,4) NOT NULL,
        RevenueAccountId int NOT NULL,
        CONSTRAINT PK_HospitalCreditNoteLines PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalCreditNoteLines_Quantity CHECK (Quantity > 0)
    );
    CREATE INDEX IX_HospitalCreditNoteLines_CompanyId_CreditNoteId ON dbo.HospitalCreditNoteLines (CompanyId, CreditNoteId);
    CREATE INDEX IX_HospitalCreditNoteLines_CompanyId_BillLineId ON dbo.HospitalCreditNoteLines (CompanyId, BillLineId);
END

COMMIT TRANSACTION;
