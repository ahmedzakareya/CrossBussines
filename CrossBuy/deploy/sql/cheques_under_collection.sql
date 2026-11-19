-- ==========================================================================================
-- Cheques under collection (2026-11-19) — "شيك" in the owner's mobile prototype, and the web register behind it.
--
-- A cheque taken from a customer is not cash until the bank clears it. So a cheque receipt debits a holding account,
-- 1106 «شيكات تحت التحصيل», instead of the cash or bank account; clearing it moves the amount to the bank (Dr bank /
-- Cr 1106); a bounced cheque reverses the receipt's entry so the debt returns to the customer.
--
-- 1. Account 1106 for every company whose chart has the current-assets parent 11 and no 1106 yet. It sits beside
--    1101 Cash & Banks, NOT under it, so the cash figure never counts an uncleared cheque.
-- 2. ReceiptCheques: the cheque's own facts beside its receipt voucher (one cheque per receipt) and its life —
--    InHand → Cleared, or Bounced. Kept, never deleted.
--
-- Idempotent: the account is inserted only where missing; the table, keys and index only if missing.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;
-- CreatedBy is left NULL: the column is nvarchar but the application reads it as an int (Account.CreatedBy int?),
-- so any text there makes every journal post that touches the account throw InvalidCastException.
INSERT INTO dbo.Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive, RequireCostCenter, RequireProject, CreatedAt)
SELECT p.CompanyID, N'1106', N'شيكات تحت التحصيل', N'Cheques under collection', p.AccountTypeId, p.ID, 1, 1, 0, 0, SYSUTCDATETIME()
FROM dbo.Accounts p
WHERE p.Code = N'11'
  AND NOT EXISTS (SELECT 1 FROM dbo.Accounts a WHERE a.CompanyID = p.CompanyID AND a.Code = N'1106');
-- a 1106 inserted by the first version of this slice carried text in CreatedBy; clear it (re-runnable: matches 0 after)
UPDATE dbo.Accounts SET CreatedBy = NULL WHERE Code = N'1106' AND CreatedBy = N'cheques_under_collection.sql';
COMMIT TRANSACTION;
GO

IF OBJECT_ID('dbo.ReceiptCheques', 'U') IS NULL
CREATE TABLE dbo.ReceiptCheques (
    ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReceiptCheques PRIMARY KEY,
    CompanyID           int NOT NULL,
    ReceiptID           int NOT NULL,
    ChequeNo            nvarchar(50) NOT NULL,
    DueDate             date NOT NULL,
    Bank                nvarchar(150) NULL,
    Status              nvarchar(20) NOT NULL CONSTRAINT DF_ReceiptCheques_Status DEFAULT (N'InHand'),
    ClearedAt           datetime2 NULL,
    ClearedToAccountID  int NULL,
    ClearingEntryID     int NULL,
    BouncedAt           datetime2 NULL,
    BounceReason        nvarchar(300) NULL,
    CreatedBy           nvarchar(256) NULL,
    CreatedAt           datetime2 NOT NULL CONSTRAINT DF_ReceiptCheques_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_ReceiptCheques_Receipt FOREIGN KEY (ReceiptID) REFERENCES dbo.Receipts (ID),
    CONSTRAINT FK_ReceiptCheques_ClearedTo FOREIGN KEY (ClearedToAccountID) REFERENCES dbo.Accounts (ID),
    CONSTRAINT FK_ReceiptCheques_ClearingEntry FOREIGN KEY (ClearingEntryID) REFERENCES dbo.JournalEntries (ID),
    CONSTRAINT CK_ReceiptCheques_Status CHECK (Status IN (N'InHand', N'Cleared', N'Bounced'))
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ReceiptCheques_Receipt' AND object_id = OBJECT_ID('dbo.ReceiptCheques'))
    CREATE UNIQUE INDEX UX_ReceiptCheques_Receipt ON dbo.ReceiptCheques (ReceiptID);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ReceiptCheques_CompanyStatusDue' AND object_id = OBJECT_ID('dbo.ReceiptCheques'))
    CREATE INDEX IX_ReceiptCheques_CompanyStatusDue ON dbo.ReceiptCheques (CompanyID, Status, DueDate);
GO
