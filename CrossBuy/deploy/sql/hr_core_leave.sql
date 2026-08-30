/* ============================================================================================================
   HR core — leave requests, approval steps, types, policies, carry-over, encashment and provision.

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

/*  LeaveRequests CARRIES NO CompanyID, and that is deliberate rather than missed.

    Its tenancy is mediated through the requesting employee: ApprovalInboxService documents the same boundary
    ("leave has no CompanyID column and bounds by the requester's employee row"), and every read in the module
    joins Employee to reach the company. Adding a column here would create a second, independently-writable
    answer to "whose company is this request in", and the two would eventually disagree. The mediated boundary
    is reproduced, not replaced.  */

IF OBJECT_ID('dbo.LeaveRequests','U') IS NULL
CREATE TABLE dbo.LeaveRequests (
    ID                         int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    EmployeeID                 int NOT NULL,
    LeaveTypeID                int NOT NULL,
    StartDate                  datetime2(7) NOT NULL,
    EndDate                    datetime2(7) NOT NULL,
    Days                       int NOT NULL,
    Reason                     nvarchar(max) NULL,
    Status                     int NOT NULL DEFAULT((0)),
    ApproverEmployeeID         int NULL,
    DecisionAt                 datetime2(7) NULL,
    DecisionNote               nvarchar(max) NULL,
    CreatedBy                  int NULL,
    CreatedAt                  datetime2(7) NULL,
    updatedBy                  int NULL,
    UpdatedAt                  datetime2(7) NULL,
    CurrentLevel               int NOT NULL DEFAULT((0)),
    CurrentApproverEmployeeID  int NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_LeaveRequests_Employee_Status' AND object_id=OBJECT_ID('dbo.LeaveRequests'))
CREATE INDEX IX_LeaveRequests_Employee_Status ON dbo.LeaveRequests (EmployeeID, Status);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_LeaveRequests_CurrentApprover' AND object_id=OBJECT_ID('dbo.LeaveRequests'))
CREATE INDEX IX_LeaveRequests_CurrentApprover ON dbo.LeaveRequests (CurrentApproverEmployeeID, Status);
GO

IF OBJECT_ID('dbo.LeaveApprovalSteps','U') IS NULL
CREATE TABLE dbo.LeaveApprovalSteps (
    ID                  int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    LeaveRequestID      int NOT NULL,
    Level               int NOT NULL,
    ApproverEmployeeID  int NOT NULL,
    Status              int NOT NULL DEFAULT((0)),
    DecisionAt          datetime2(7) NULL,
    DecisionNote        nvarchar(max) NULL,
    CreatedBy           int NULL,
    CreatedAt           datetime2(7) NULL,
    updatedBy           int NULL,
    UpdatedAt           datetime2(7) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_LeaveApprovalSteps_Request' AND object_id=OBJECT_ID('dbo.LeaveApprovalSteps'))
CREATE INDEX IX_LeaveApprovalSteps_Request ON dbo.LeaveApprovalSteps (LeaveRequestID);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_LeaveApprovalSteps_Approver' AND object_id=OBJECT_ID('dbo.LeaveApprovalSteps'))
CREATE INDEX IX_LeaveApprovalSteps_Approver ON dbo.LeaveApprovalSteps (ApproverEmployeeID);
GO

IF OBJECT_ID('dbo.LeaveTypes','U') IS NULL
CREATE TABLE dbo.LeaveTypes (
    ID            int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    NameAr        nvarchar(max) NOT NULL,
    NameEn        nvarchar(max) NOT NULL,
    CreatedBy     int NULL,
    CreatedAt     datetime2(7) NULL,
    updatedBy     int NULL,
    UpdatedAt     datetime2(7) NULL,
    Notes         nvarchar(max) NULL,
    IsEncashable  bit NOT NULL DEFAULT((0))
);
GO

IF OBJECT_ID('dbo.LeavePolicies','U') IS NULL
CREATE TABLE dbo.LeavePolicies (
    ID                          int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    LeavePolicyTypeID           int NOT NULL,
    EntitlementDaysPerYear      int NOT NULL,
    CarryOverLimit              int NOT NULL,
    NoticePeriodDays            int NOT NULL,
    RequiresMedicalCertificate  bit NULL,
    CreatedBy                   int NULL,
    CreatedAt                   datetime2(7) NULL,
    updatedBy                   int NULL,
    UpdatedAt                   datetime2(7) NULL,
    LeaveTypeID                 int NOT NULL DEFAULT((0))
);
GO

IF OBJECT_ID('dbo.LeaveCarryOvers','U') IS NULL
CREATE TABLE dbo.LeaveCarryOvers (
    ID           int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID    int NOT NULL,
    EmployeeID   int NOT NULL,
    LeaveTypeID  int NOT NULL,
    Year         int NOT NULL,
    Days         int NOT NULL,
    CreatedAt    datetime2(7) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_LeaveCarryOvers_Company_Year' AND object_id=OBJECT_ID('dbo.LeaveCarryOvers'))
CREATE INDEX IX_LeaveCarryOvers_Company_Year ON dbo.LeaveCarryOvers (CompanyID, Year);
GO

IF OBJECT_ID('dbo.LeaveEncashments','U') IS NULL
CREATE TABLE dbo.LeaveEncashments (
    ID              int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID       int NOT NULL,
    EmployeeID      int NOT NULL,
    EmployeeName    nvarchar(200) NULL,
    LeaveTypeID     int NOT NULL,
    Year            int NOT NULL,
    Days            int NOT NULL,
    DailyRate       decimal(19,4) NOT NULL,
    Amount          decimal(19,4) NOT NULL,
    EncashDate      datetime2(7) NOT NULL,
    JournalEntryId  int NULL,
    CreatedAt       datetime2(7) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_LeaveEncashments_Company_Employee' AND object_id=OBJECT_ID('dbo.LeaveEncashments'))
CREATE INDEX IX_LeaveEncashments_Company_Employee ON dbo.LeaveEncashments (CompanyID, EmployeeID);
GO

IF OBJECT_ID('dbo.LeaveProvisionRuns','U') IS NULL
CREATE TABLE dbo.LeaveProvisionRuns (
    ID              int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID       int NOT NULL,
    AsOfDate        datetime2(7) NOT NULL,
    TotalDays       decimal(19,4) NOT NULL,
    TotalAmount     decimal(19,4) NOT NULL,
    Adjustment      decimal(19,4) NOT NULL,
    JournalEntryId  int NULL,
    CreatedAt       datetime2(7) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_LeaveProvisionRuns_Company_AsOf' AND object_id=OBJECT_ID('dbo.LeaveProvisionRuns'))
CREATE INDEX IX_LeaveProvisionRuns_Company_AsOf ON dbo.LeaveProvisionRuns (CompanyID, AsOfDate);
GO
