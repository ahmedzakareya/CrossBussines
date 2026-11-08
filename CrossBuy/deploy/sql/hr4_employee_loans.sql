-- ==========================================================================================
-- HR4 (2026-11-08): employee loans and salary advances, repaid by payroll deduction under the legal cap.
--
--   EmployeeLoans           one loan / advance: principal, monthly instalment, first deduction month, status
--   EmployeeLoanDeductions  one row per loan per payroll month actually deducted (written when the payroll POSTS)
--   Payslips.LoanDeduction  what the payslip took for loans, so its net explains itself
--   Accounting (company 1): account 110207 "Employee loans & advances" under 1102 Accounts Receivable, and the
--                           posting rule Payroll/EmployeeLoans → that account (debited when a loan is paid out,
--                           credited by every payroll deduction).
-- The cap is the country rule pack's LoanCapPct (Kuwait Art. 59: 10% of the wage) — a setting, never code.
-- Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.EmployeeLoans', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeLoans (
        ID                     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeLoans PRIMARY KEY,
        CompanyID              int            NOT NULL,
        EmployeeID             int            NOT NULL CONSTRAINT FK_EmployeeLoans_Employee REFERENCES dbo.Employee(ID),
        Kind                   nvarchar(20)   NOT NULL CONSTRAINT CK_EmployeeLoans_Kind CHECK (Kind IN (N'Loan', N'Advance')),
        Principal              decimal(18,3)  NOT NULL CONSTRAINT CK_EmployeeLoans_Principal CHECK (Principal > 0),
        Installment            decimal(18,3)  NOT NULL CONSTRAINT CK_EmployeeLoans_Installment CHECK (Installment > 0),
        FirstYear              int            NOT NULL,
        FirstMonth             int            NOT NULL CONSTRAINT CK_EmployeeLoans_Month CHECK (FirstMonth BETWEEN 1 AND 12),
        Status                 nvarchar(20)   NOT NULL CONSTRAINT DF_EmployeeLoans_Status DEFAULT N'Active'
                                              CONSTRAINT CK_EmployeeLoans_Status CHECK (Status IN (N'Active', N'Settled', N'Cancelled')),
        Reason                 nvarchar(400)  NULL,
        DisbursedOn            date           NOT NULL,
        PaidFromAccountId      int            NULL,
        DisbursementEntryId    int            NULL,
        CreatedAt              datetime2      NOT NULL CONSTRAINT DF_EmployeeLoans_CreatedAt DEFAULT SYSUTCDATETIME(),
        CreatedByEmployeeId    int            NULL
    );
    CREATE INDEX IX_EmployeeLoans_Company_Employee ON dbo.EmployeeLoans (CompanyID, EmployeeID, Status);
END

IF OBJECT_ID(N'dbo.EmployeeLoanDeductions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeLoanDeductions (
        ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeLoanDeductions PRIMARY KEY,
        CompanyID       int            NOT NULL,
        LoanID          int            NOT NULL CONSTRAINT FK_EmployeeLoanDeductions_Loan REFERENCES dbo.EmployeeLoans(ID),
        Year            int            NOT NULL,
        Month           int            NOT NULL,
        Amount          decimal(18,3)  NOT NULL CONSTRAINT CK_EmployeeLoanDeductions_Amount CHECK (Amount > 0),
        JournalEntryId  int            NULL,
        CreatedAt       datetime2      NOT NULL CONSTRAINT DF_EmployeeLoanDeductions_CreatedAt DEFAULT SYSUTCDATETIME()
    );
    -- a payroll month deducts a loan once
    CREATE UNIQUE INDEX UX_EmployeeLoanDeductions_Loan_Period ON dbo.EmployeeLoanDeductions (LoanID, Year, Month);
END

IF COL_LENGTH(N'dbo.Payslips', N'LoanDeduction') IS NULL
    ALTER TABLE dbo.Payslips ADD LoanDeduction decimal(18,3) NOT NULL CONSTRAINT DF_Payslips_LoanDeduction DEFAULT 0;

-- company 1: the receivable account and the posting rule (other companies get them when their chart is set up;
-- until then the loan screen and the payroll refuse with a message naming the missing rule)
IF EXISTS (SELECT 1 FROM dbo.Accounts WHERE CompanyID = 1 AND Code = N'1102')
   AND NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE CompanyID = 1 AND Code = N'110207')
    INSERT INTO dbo.Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive, RequireCostCenter, RequireProject, CreatedAt)
    SELECT 1, N'110207', N'سلف وقروض الموظفين', N'Employee loans & advances', p.AccountTypeId, p.ID, 1, 1, 0, 0, SYSUTCDATETIME()
      FROM dbo.Accounts p WHERE p.CompanyID = 1 AND p.Code = N'1102';

IF NOT EXISTS (SELECT 1 FROM dbo.PostingRules WHERE CompanyID = 1 AND SourceType = N'Payroll' AND ComponentCode = N'EmployeeLoans')
    INSERT INTO dbo.PostingRules (CompanyID, SourceType, ComponentCode, DebitAccountId, CreditAccountId, CostCenterSource, CreatedAt)
    SELECT 1, N'Payroll', N'EmployeeLoans', a.ID, a.ID, NULL, SYSUTCDATETIME()
      FROM dbo.Accounts a WHERE a.CompanyID = 1 AND a.Code = N'110207';

COMMIT TRANSACTION;
