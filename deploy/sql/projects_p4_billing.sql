-- Projects & Contracting — P4 (progress billing / المستخلصات).
-- Adds a dedicated contract-revenue account + the billing header/line tables.
-- Money posted ONLY via ReceivableService/JournalEntryService (no new writer). Idempotent. Company 1.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- 1) Contract revenue account (4102), postable, under 4 (الإيرادات) — feeds project profitability (rev 4xxx − cost 5xxx)
IF NOT EXISTS (SELECT 1 FROM Accounts WHERE CompanyID=1 AND Code='4102')
    INSERT INTO Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive, RequireCostCenter, RequireProject, CashFlowCategory, CreatedAt)
    SELECT 1, '4102', N'إيرادات عقود مقاولات', N'Contract revenue',
           (SELECT ID FROM AccountTypes WHERE Code='REV'),
           (SELECT ID FROM Accounts WHERE CompanyID=1 AND Code='4'),
           1, 1, 0, 0, 'Operating', SYSUTCDATETIME();
GO

-- 2) Progress billing header (المستخلص) — cumulative; built from a Confirmed progress measurement
IF OBJECT_ID('dbo.ProgressBillings','U') IS NULL
BEGIN
    CREATE TABLE dbo.ProgressBillings(
        ID                    INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyID             INT           NOT NULL,
        ProjectId             INT           NOT NULL,
        ProgressId            INT           NOT NULL,       -- the Confirmed ProjectProgress it bills
        CustomerId            INT           NULL,           -- from the project (for the invoice/receipts)
        BillingNo             INT           NOT NULL,       -- sequence per project
        BillingDate           DATETIME2     NOT NULL,
        Status                NVARCHAR(20)  NOT NULL CONSTRAINT DF_ProgressBillings_Status DEFAULT('Draft'),  -- Draft/Approved/Posted
        GrossWork             DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProgressBillings_Gross  DEFAULT(0),  -- W (period work value)
        TaxRate               DECIMAL(9,4)  NOT NULL CONSTRAINT DF_ProgressBillings_TaxR   DEFAULT(0),
        TaxAmount             DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProgressBillings_Tax    DEFAULT(0),  -- T
        RetentionPercent      DECIMAL(9,4)  NOT NULL CONSTRAINT DF_ProgressBillings_RetP   DEFAULT(0),
        RetentionAmount       DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProgressBillings_Ret    DEFAULT(0),  -- R
        AdvanceRecoveryAmount DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProgressBillings_Adv    DEFAULT(0),  -- A
        NetDue                DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProgressBillings_Net    DEFAULT(0),  -- W+T-R-A
        SalesInvoiceId        INT           NULL,
        RetentionReceiptId    INT           NULL,
        AdvanceReceiptId      INT           NULL,
        Note                  NVARCHAR(1000) NULL,
        CreatedAt             DATETIME2     NULL,
        CreatedBy             INT           NULL,
        PostedAt              DATETIME2     NULL,
        PostedBy              INT           NULL
    );
    CREATE INDEX IX_ProgressBillings_Project ON dbo.ProgressBillings(CompanyID, ProjectId, BillingNo);
    CREATE INDEX IX_ProgressBillings_Progress ON dbo.ProgressBillings(ProgressId);
    PRINT 'CREATED ProgressBillings';
END
ELSE PRINT 'ProgressBillings EXISTS';
GO

-- 3) Progress billing line — per BOQ item period value (= cumulative executed − previously billed)
IF OBJECT_ID('dbo.ProgressBillingLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.ProgressBillingLines(
        ID                      INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        BillingId               INT           NOT NULL,
        BoqItemId               INT           NULL,          -- null = whole-project (no-BOQ) line
        CumulativeExecutedValue DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProgressBillingLines_Cum  DEFAULT(0),
        PreviouslyBilledValue   DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProgressBillingLines_Prev DEFAULT(0),
        PeriodValue             DECIMAL(19,4) NOT NULL CONSTRAINT DF_ProgressBillingLines_Per  DEFAULT(0),
        CONSTRAINT FK_ProgressBillingLines_Header FOREIGN KEY (BillingId) REFERENCES dbo.ProgressBillings(ID)
    );
    CREATE INDEX IX_ProgressBillingLines_Header ON dbo.ProgressBillingLines(BillingId);
    PRINT 'CREATED ProgressBillingLines';
END
ELSE PRINT 'ProgressBillingLines EXISTS';
GO

SELECT Code, Name, IsPostable FROM Accounts WHERE CompanyID=1 AND Code='4102';
GO
