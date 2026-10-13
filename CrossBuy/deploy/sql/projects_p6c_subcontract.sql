-- Projects & Contracting — P6-ج (subcontractor / equipment progress billing).
-- Adds a subcontractor-retention liability account + the subcontract & subcontract-billing tables.
-- Money posted ONLY via PayableService (purchase invoice + settlement payment) → ap_sub intact; Dr 510104 tagged ProjectId.
-- No new accounting writer. Idempotent. Company 1.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- 1) Subcontractor retention held = current LIABILITY (2105), under 21 — mirror of 1104 (customer retention) on the payable side
IF NOT EXISTS (SELECT 1 FROM Accounts WHERE CompanyID=1 AND Code='2105')
    INSERT INTO Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive, RequireCostCenter, RequireProject, CashFlowCategory, CreatedAt)
    SELECT 1, '2105', N'محتجزات مقاولي الباطن', N'Subcontractor retention payable',
           (SELECT ID FROM AccountTypes WHERE Code='LIAB'),
           (SELECT ID FROM Accounts WHERE CompanyID=1 AND Code='21'),
           1, 1, 0, 0, 'Operating', SYSUTCDATETIME();
GO

-- 2) Subcontract header (عقد باطن) — one per (project, vendor); many per project
IF OBJECT_ID('dbo.Subcontracts','U') IS NULL
BEGIN
    CREATE TABLE dbo.Subcontracts(
        ID               INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyID        INT           NOT NULL,
        ProjectId        INT           NOT NULL,
        VendorId         INT           NOT NULL,
        Description      NVARCHAR(400) NULL,
        ContractValue    DECIMAL(19,4) NULL,
        RetentionPercent DECIMAL(9,4)  NULL,
        Status           NVARCHAR(20)  NOT NULL CONSTRAINT DF_Subcontracts_Status DEFAULT('Active'),
        CreatedAt        DATETIME2     NULL,
        CreatedBy        INT           NULL
    );
    CREATE INDEX IX_Subcontracts_Project ON dbo.Subcontracts(CompanyID, ProjectId);
    PRINT 'CREATED Subcontracts';
END
ELSE PRINT 'Subcontracts EXISTS';
GO

-- 3) Subcontract progress billing (مستخلص باطن) — cumulative per subcontract
IF OBJECT_ID('dbo.SubcontractBillings','U') IS NULL
BEGIN
    CREATE TABLE dbo.SubcontractBillings(
        ID                INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyID         INT           NOT NULL,
        SubcontractId     INT           NOT NULL,
        ProjectId         INT           NOT NULL,
        VendorId          INT           NOT NULL,
        BillingNo         INT           NOT NULL,       -- sequence per subcontract
        BillingDate       DATETIME2     NOT NULL,
        Status            NVARCHAR(20)  NOT NULL CONSTRAINT DF_SubcontractBillings_Status DEFAULT('Draft'),  -- Draft/Approved/Posted
        CumulativeWork    DECIMAL(19,4) NOT NULL CONSTRAINT DF_SubcontractBillings_Cum DEFAULT(0),   -- cumulative work value to date
        GrossWork         DECIMAL(19,4) NOT NULL CONSTRAINT DF_SubcontractBillings_Gross DEFAULT(0), -- period W = cumulative − previously billed
        TaxRate           DECIMAL(9,4)  NOT NULL CONSTRAINT DF_SubcontractBillings_TaxR DEFAULT(0),
        TaxAmount         DECIMAL(19,4) NOT NULL CONSTRAINT DF_SubcontractBillings_Tax DEFAULT(0),   -- T
        RetentionPercent  DECIMAL(9,4)  NOT NULL CONSTRAINT DF_SubcontractBillings_RetP DEFAULT(0),
        RetentionAmount   DECIMAL(19,4) NOT NULL CONSTRAINT DF_SubcontractBillings_Ret DEFAULT(0),   -- R
        NetPayable        DECIMAL(19,4) NOT NULL CONSTRAINT DF_SubcontractBillings_Net DEFAULT(0),   -- W+T−R
        PurchaseInvoiceId INT           NULL,
        RetentionPaymentId INT          NULL,
        Note              NVARCHAR(1000) NULL,
        CreatedAt         DATETIME2     NULL,
        CreatedBy         INT           NULL,
        PostedAt          DATETIME2     NULL,
        PostedBy          INT           NULL
    );
    CREATE INDEX IX_SubcontractBillings_Sub ON dbo.SubcontractBillings(CompanyID, SubcontractId, BillingNo);
    PRINT 'CREATED SubcontractBillings';
END
ELSE PRINT 'SubcontractBillings EXISTS';
GO

SELECT Code, Name, IsPostable FROM Accounts WHERE CompanyID=1 AND Code='2105';
GO
