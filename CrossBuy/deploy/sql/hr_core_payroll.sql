/* ============================================================================================================
   HR core — payslips and the policy configuration tables.

   CANONICAL AUTHORED SLICE (D-38). This file lives under CrossBuy/deploy/sql, the authored root that
   apply-sql-slices.ps1 treats as canonical. The repository-level deploy/sql is the deployment PACKAGE root and
   holds the older HR scripts; those are inputs to this migration, not a second source of truth.

   IDEMPOTENT AND ADDITIVE. Every object is created only when absent. Nothing here alters or drops an existing
   table, column, index or row, so the file is safe to re-run against a database that already has HR data — and
   safe against CrossBuyDev, where all of these tables already exist and every guard therefore no-ops.

   WHY THIS EXISTS. Before D-38, HR had thirteen tables reproducible from deploy scripts and eighteen that were
   not: a fresh CrossBusiness deployment could create the appraisal, training, recruitment and employee-request
   tables and then fail on Employee itself. These files close that gap for the tables HR owns.

   FIDELITY, NOT REDESIGN. Column types, nullability and defaults were read from the live CrossBuyDev schema
   (sys.columns / sys.types, SELECT-only) and reproduced exactly, so a fresh deployment matches what the
   committed application expects. Where the live shape is legacy debt it is reproduced AND recorded rather than
   silently corrected — correcting it is a data migration, which this batch is explicitly not.

   NOTHING HERE TOUCHES A FINANCIAL OR STOCK TABLE. The GL is still written exclusively by JournalEntryService
   and stock exclusively by StockService.

   Run with sqlcmd -I (QUOTED_IDENTIFIER ON).
   ============================================================================================================ */

/*  FOUR OF THESE CARRY NO TENANT COLUMN: SalaryPolicies, Policies, AttendancePolicies (in the attendance
    slice) and LeavePolicies (in the leave slice). SalaryPolicies is the sharpest case — base salary,
    allowances, overtime rate, penalties, social-insurance shares and tax rate, shared by every company on the
    installation.

    THE COLUMN IS NOT ADDED HERE. Adding it is trivial; deciding who owns the existing rows is not, and this
    batch is forbidden from guessing. The analysis and the migration design are in the batch report, and the
    data decision is the owner's. Reproducing the current shape keeps a fresh deployment matching the committed
    application; it does not endorse the shape.  */

IF OBJECT_ID('dbo.Payslips','U') IS NULL
CREATE TABLE dbo.Payslips (
    ID              int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID       int NOT NULL,
    Year            int NOT NULL,
    Month           int NOT NULL,
    EmployeeID      int NOT NULL,
    EmployeeName    nvarchar(200) NOT NULL,
    CostCenterId    int NULL,
    BaseSalary      decimal(19,4) NOT NULL DEFAULT((0)),
    Allowances      decimal(19,4) NOT NULL DEFAULT((0)),
    OvertimePay     decimal(19,4) NOT NULL DEFAULT((0)),
    GrossEarnings   decimal(19,4) NOT NULL DEFAULT((0)),
    LateMinutes     int NOT NULL DEFAULT((0)),
    LatePenalty     decimal(19,4) NOT NULL DEFAULT((0)),
    AbsentDays      int NOT NULL DEFAULT((0)),
    AbsencePenalty  decimal(19,4) NOT NULL DEFAULT((0)),
    SiEmployee      decimal(19,4) NOT NULL DEFAULT((0)),
    SiCompany       decimal(19,4) NOT NULL DEFAULT((0)),
    Tax             decimal(19,4) NOT NULL DEFAULT((0)),
    Net             decimal(19,4) NOT NULL DEFAULT((0)),
    JournalEntryId  int NULL,
    CreatedAt       datetime2(7) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Payslips_Company_Employee_Period' AND object_id=OBJECT_ID('dbo.Payslips'))
CREATE INDEX IX_Payslips_Company_Employee_Period ON dbo.Payslips (CompanyID, EmployeeID);
GO

IF OBJECT_ID('dbo.SalaryPolicies','U') IS NULL
CREATE TABLE dbo.SalaryPolicies (
    ID                            int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    LeavePolicyTypeID             int NOT NULL,
    Description                   nvarchar(max) NULL,
    BaseSalary                    decimal(19,4) NOT NULL,
    HousingAllowance              decimal(19,4) NOT NULL,
    TransportationAllowance       decimal(19,4) NOT NULL,
    OtherAllowances               decimal(19,4) NOT NULL,
    OvertimeRate                  decimal(19,4) NOT NULL,
    LatePenaltyPerMinute          decimal(19,4) NOT NULL,
    AbsencePenaltyPerDay          decimal(19,4) NOT NULL,
    SocialInsuranceEmployeeShare  decimal(19,4) NOT NULL,
    SocialInsuranceCompanyShare   decimal(19,4) NOT NULL,
    TaxRate                       decimal(19,4) NOT NULL,
    IsTaxApplicable               bit NOT NULL,
    PaymentDay                    int NOT NULL,
    PaymentMethod                 nvarchar(max) NOT NULL,
    CreatedBy                     int NULL,
    CreatedAt                     datetime2(7) NULL,
    updatedBy                     int NULL,
    UpdatedAt                     datetime2(7) NULL
);
GO

IF OBJECT_ID('dbo.Policies','U') IS NULL
CREATE TABLE dbo.Policies (
    ID              int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    NameAr          nvarchar(max) NULL,
    NameEn          nvarchar(max) NULL,
    CreatedBy       int NULL,
    CreatedAt       datetime2(7) NULL,
    updatedBy       int NULL,
    UpdatedAt       datetime2(7) NULL,
    Notes           nvarchar(max) NULL,
    ApprovalLevels  int NULL
);
GO

IF OBJECT_ID('dbo.PolicyAssignments','U') IS NULL
CREATE TABLE dbo.PolicyAssignments (
    ID                 int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    LeavePolicyTypeID  int NOT NULL,
    EmployeeID         int NOT NULL,
    CreatedBy          int NULL,
    CreatedAt          datetime2(7) NULL,
    updatedBy          int NULL,
    UpdatedAt          datetime2(7) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PolicyAssignments_Employee' AND object_id=OBJECT_ID('dbo.PolicyAssignments'))
CREATE INDEX IX_PolicyAssignments_Employee ON dbo.PolicyAssignments (EmployeeID);
GO
