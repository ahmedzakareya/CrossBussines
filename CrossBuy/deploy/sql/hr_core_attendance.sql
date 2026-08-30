/* ============================================================================================================
   HR core — attendance records, attendance policy and official holidays.

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

IF OBJECT_ID('dbo.AttendanceRecords','U') IS NULL
CREATE TABLE dbo.AttendanceRecords (
    ID                 int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID          int NOT NULL,
    EmployeeID         int NOT NULL,
    WorkDate           date NOT NULL,
    CheckIn            datetime2(7) NULL,
    CheckOut           datetime2(7) NULL,
    Source             nvarchar(20) NOT NULL DEFAULT('Web'),
    LateMinutes        int NOT NULL DEFAULT((0)),
    EarlyLeaveMinutes  int NOT NULL DEFAULT((0)),
    OvertimeMinutes    int NOT NULL DEFAULT((0)),
    WorkedHours        decimal(9,2) NOT NULL DEFAULT((0)),
    Status             nvarchar(20) NOT NULL DEFAULT('Present'),
    Notes              nvarchar(300) NULL,
    CreatedBy          nvarchar(450) NULL,
    CreatedAt          datetime2(7) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AttendanceRecords_Company_Employee_Date' AND object_id=OBJECT_ID('dbo.AttendanceRecords'))
CREATE INDEX IX_AttendanceRecords_Company_Employee_Date ON dbo.AttendanceRecords (CompanyID, EmployeeID, WorkDate);
GO

IF OBJECT_ID('dbo.AttendancePolicies','U') IS NULL
CREATE TABLE dbo.AttendancePolicies (
    ID                                int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    LeavePolicyTypeID                 int NOT NULL,
    AllowedGraceMinutes               int NOT NULL,
    WarningThresholdCount             int NOT NULL,
    DeductionRatePerOccurrence        decimal(19,4) NOT NULL,
    UnexcusedAbsenceThreshold         int NOT NULL,
    WorkStartTime                     time NULL,
    WorkEndTime                       time NULL,
    WorkHoursPerDay                   decimal(4,2) NULL,
    BreakStartTime                    time NULL,
    BreakEndTime                      time NULL,
    BreakDurationMinutes              int NULL,
    WorkDaysPerWeek                   int NULL,
    CreatedBy                         int NULL,
    CreatedAt                         datetime2(7) NULL,
    updatedBy                         int NULL,
    UpdatedAt                         datetime2(7) NULL,
    WorkOnSunday                      bit NOT NULL DEFAULT((0)),
    WorkOnMonday                      bit NOT NULL DEFAULT((0)),
    WorkOnTuesday                     bit NOT NULL DEFAULT((0)),
    WorkOnWednesday                   bit NOT NULL DEFAULT((0)),
    WorkOnThursday                    bit NOT NULL DEFAULT((0)),
    WorkOnFriday                      bit NOT NULL DEFAULT((0)),
    WorkOnSaturday                    bit NOT NULL DEFAULT((0)),
    AllowPermissions                  bit NOT NULL DEFAULT((0)),
    MaxPermissionRequestsPerDay       int NULL,
    MaxPermissionRequestsPerWeek      int NULL,
    MaxPermissionRequestsPerMonth     int NULL,
    MaxPermissionMinutesPerRequest    int NULL,
    MaxPermissionMinutesPerDay        int NULL,
    MaxPermissionMinutesPerWeek       int NULL,
    MaxPermissionMinutesPerMonth      int NULL,
    RequirePermissionApproval         bit NOT NULL DEFAULT((1)),
    RejectPermissionIfExceeded        bit NOT NULL DEFAULT((1)),
    DeductPermissionIfExceeded        bit NOT NULL DEFAULT((0)),
    AllowLateArrivalPermission        bit NOT NULL DEFAULT((1)),
    AllowEarlyLeavePermission         bit NOT NULL DEFAULT((1)),
    AllowDuringWorkPermission         bit NOT NULL DEFAULT((1)),
    LinkPermissionWithFingerprint     bit NOT NULL DEFAULT((1)),
    PermissionDeductionRatePerMinute  decimal(19,4) NULL
);
GO

IF OBJECT_ID('dbo.OfficialHolidays','U') IS NULL
CREATE TABLE dbo.OfficialHolidays (
    ID           int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID    int NOT NULL,
    NameAr       nvarchar(120) NOT NULL,
    NameEn       nvarchar(120) NULL,
    HolidayDate  date NOT NULL,
    IsRecurring  bit NOT NULL DEFAULT((0)),
    Notes        nvarchar(300) NULL,
    CreatedAt    datetime2(7) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OfficialHolidays_Company_Date' AND object_id=OBJECT_ID('dbo.OfficialHolidays'))
CREATE INDEX IX_OfficialHolidays_Company_Date ON dbo.OfficialHolidays (CompanyID, HolidayDate);
GO
