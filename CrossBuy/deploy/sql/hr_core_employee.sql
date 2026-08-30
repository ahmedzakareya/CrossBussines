/* ============================================================================================================
   HR core — the employee record, contracts, documents and final settlement.

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

/*  LEGACY DEBT REPRODUCED DELIBERATELY, and named so nobody has to rediscover it.

    Employee declares FirstName, LastName, FullName, Address, PhoneNumber, Email, ProfileImage, Gender and
    MaritalStatus as nvarchar(max) NOT NULL. That is what an EF `string` with no [MaxLength] produces, and it is
    what the live database has. It is poor schema — an unbounded column cannot be indexed and NOT NULL on a
    free-text field forces empty strings instead of nulls — but narrowing it is a data migration with a
    truncation risk, and this batch executes no DDL. Reproduced for fidelity; recorded as HR SCHEMA DEBT.

    UserId is nvarchar(450) because it carries the ASP.NET Identity key and is UNIQUE in the model — 450 is the
    indexable maximum, which is why that number and not 4000.  */

IF OBJECT_ID('dbo.Employee','U') IS NULL
CREATE TABLE dbo.Employee (
    ID                         int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    FirstName                  nvarchar(max) NOT NULL,
    LastName                   nvarchar(max) NOT NULL,
    FullName                   nvarchar(max) NOT NULL,
    Address                    nvarchar(max) NOT NULL,
    PhoneNumber                nvarchar(max) NOT NULL,
    Email                      nvarchar(max) NOT NULL,
    CountryID                  int NULL,
    JobTitleID                 int NOT NULL,
    BranchID                   int NULL,
    EmpCompanyID               int NOT NULL,
    ProfileImage               nvarchar(max) NOT NULL,
    DateOfBirth                datetime2(7) NOT NULL,
    Gender                     nvarchar(max) NOT NULL,
    MaritalStatus              nvarchar(max) NOT NULL,
    DateOfJoining              datetime2(7) NOT NULL,
    IsActive                   bit NOT NULL,
    UserId                     nvarchar(450) NOT NULL,
    CreatedBy                  int NULL,
    CreatedAt                  datetime2(7) NULL,
    updatedBy                  int NULL,
    UpdatedAt                  datetime2(7) NULL,
    SalaryPoliciesID           int NULL,
    DepartmentID               int NULL,
    AdministrativeStructureID  int NULL,
    FullNameEn                 nvarchar(200) NULL,
    EmploymentType             nvarchar(50) NULL,
    ManufHourlyRate            decimal(19,4) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Employee_Company_Active' AND object_id=OBJECT_ID('dbo.Employee'))
CREATE INDEX IX_Employee_Company_Active ON dbo.Employee (EmpCompanyID, IsActive) INCLUDE (FullName, FullNameEn, JobTitleID);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Employee_Company_Branch' AND object_id=OBJECT_ID('dbo.Employee'))
CREATE INDEX IX_Employee_Company_Branch ON dbo.Employee (EmpCompanyID, BranchID);
GO

IF OBJECT_ID('dbo.EmploymentContracts','U') IS NULL
CREATE TABLE dbo.EmploymentContracts (
    ID            int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID     int NOT NULL,
    EmployeeID    int NOT NULL,
    ContractType  nvarchar(40) NOT NULL,
    StartDate     datetime2(7) NOT NULL,
    EndDate       datetime2(7) NULL,
    Status        nvarchar(40) NOT NULL,
    FilePath      nvarchar(400) NULL,
    Notes         nvarchar(1000) NULL,
    CreatedAt     datetime2(7) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_EmploymentContracts_Company_Employee' AND object_id=OBJECT_ID('dbo.EmploymentContracts'))
CREATE INDEX IX_EmploymentContracts_Company_Employee ON dbo.EmploymentContracts (CompanyID, EmployeeID);
GO

IF OBJECT_ID('dbo.EmployeeDocuments','U') IS NULL
CREATE TABLE dbo.EmployeeDocuments (
    ID          int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID   int NOT NULL,
    EmployeeID  int NOT NULL,
    DocType     nvarchar(40) NOT NULL,
    DocNumber   nvarchar(100) NULL,
    FilePath    nvarchar(400) NULL,
    IssueDate   datetime2(7) NULL,
    ExpiryDate  datetime2(7) NULL,
    Notes       nvarchar(1000) NULL,
    CreatedAt   datetime2(7) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_EmployeeDocuments_Company_Employee' AND object_id=OBJECT_ID('dbo.EmployeeDocuments'))
CREATE INDEX IX_EmployeeDocuments_Company_Employee ON dbo.EmployeeDocuments (CompanyID, EmployeeID);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_EmployeeDocuments_Expiry' AND object_id=OBJECT_ID('dbo.EmployeeDocuments'))
CREATE INDEX IX_EmployeeDocuments_Expiry ON dbo.EmployeeDocuments (CompanyID, ExpiryDate)
    WHERE ExpiryDate IS NOT NULL;
GO

IF OBJECT_ID('dbo.FinalSettlements','U') IS NULL
CREATE TABLE dbo.FinalSettlements (
    ID               int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID        int NOT NULL,
    EmployeeID       int NOT NULL,
    EmployeeName     nvarchar(200) NULL,
    TerminationDate  datetime2(7) NOT NULL,
    Reason           nvarchar(500) NULL,
    ServiceYears     decimal(19,4) NOT NULL,
    LeaveDays        int NOT NULL,
    LeaveValue       decimal(19,4) NOT NULL,
    Gratuity         decimal(19,4) NOT NULL,
    OtherEarnings    decimal(19,4) NOT NULL,
    Deductions       decimal(19,4) NOT NULL,
    NetSettlement    decimal(19,4) NOT NULL,
    JournalEntryId   int NULL,
    CreatedAt        datetime2(7) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_FinalSettlements_Company_Employee' AND object_id=OBJECT_ID('dbo.FinalSettlements'))
CREATE INDEX IX_FinalSettlements_Company_Employee ON dbo.FinalSettlements (CompanyID, EmployeeID);
GO
