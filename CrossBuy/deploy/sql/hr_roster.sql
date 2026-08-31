/* ============================================================================================================
   HR Product Batch 2 — roster / shift management.

   CANONICAL AUTHORED SLICE (D-38), under CrossBuy/deploy/sql — the authored root apply-sql-slices.ps1 treats as
   canonical. Named hr_roster.sql so it sits with the other HR slices this tab authored (CrossBuy/deploy/sql/hr*.sql)
   and matches TAB-2's ownership pattern without needing a workaround name.

   IDEMPOTENT AND ADDITIVE. Every table, index and constraint is created only when absent. Nothing here alters or
   drops an existing object, so the file is safe to re-run.

   NOT EXECUTED BY THIS BATCH. No DDL was applied to CrossBuyDev or anywhere else.

   FOUR TABLES, AND WHAT THEY DELIBERATELY DO NOT DUPLICATE:

     · dbo.PosShifts is a CASH-DRAWER session on a terminal, not a work shift. Nothing here touches it, and the
       table below is called WorkShifts so the two never read as the same concept.

     · dbo.AttendancePolicies already holds WorkStartTime/WorkEndTime/BreakDurationMinutes and a weekly pattern.
       It stays the employee's DEFAULT contractual schedule. It holds exactly one start time, so it cannot express
       "Morning on Monday, Night on Thursday" — which is what a roster is. An assignment below is a per-date
       override of that default; its absence means the policy still applies.

     · dbo.AttendanceRecord stays the sole authority for what ACTUALLY happened. There is NO foreign key to it and
       no column added to it. Planned-vs-actual joins on (CompanyID, EmployeeID, WorkDate), which both sides
       already carry — which is exactly why Attendance needed no change.

     · dbo.LeaveRequests stays the sole authority for leave. Roster READS approved rows (Status = 1) for conflict
       detection and writes none.

   NO IsConflicted COLUMN. A conflict is a function of OTHER rows — another assignment, an approved leave request —
   and goes stale the moment one of them moves. It is detected on demand, never stored.

   Run with sqlcmd -I (QUOTED_IDENTIFIER ON) so the filtered indexes below build.
   ============================================================================================================ */

/* ------------------------------------------------------------------------------------------------------------
   SHIFT DEFINITIONS.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID('dbo.WorkShifts','U') IS NULL
CREATE TABLE dbo.WorkShifts (
    ID            int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID     int NOT NULL,
    Code          nvarchar(50) NOT NULL,
    NameAr        nvarchar(200) NOT NULL,
    NameEn        nvarchar(200) NOT NULL,
    /*  LOCAL WALL-CLOCK, not an instant. "The night shift starts at 22:00" is true at the site every day; storing
        a datetime would tie the definition to the day it was written. An end at or before the start means the
        shift runs past midnight — derived from these two columns, never stored as a third.  */
    StartTime     time(0) NOT NULL,
    EndTime       time(0) NOT NULL,
    BreakMinutes  int NOT NULL CONSTRAINT DF_WorkShift_Break DEFAULT(0),
    IsActive      bit NOT NULL CONSTRAINT DF_WorkShift_Active DEFAULT(1),
    CreatedBy     int NULL,
    CreatedAt     datetime2(7) NULL,
    UpdatedBy     int NULL,
    UpdatedAt     datetime2(7) NULL
);
GO

/*  The code is the stable identity used for reporting across periods, so it must be unique per company. Enforced
    here rather than in C#: "check then insert" in application code is a race two concurrent requests both win.  */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_WorkShift_Company_Code' AND object_id=OBJECT_ID('dbo.WorkShifts'))
CREATE UNIQUE INDEX UX_WorkShift_Company_Code ON dbo.WorkShifts (CompanyID, Code);
GO

/* ------------------------------------------------------------------------------------------------------------
   ROSTER PERIODS — the unit that gets published.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID('dbo.RosterPeriods','U') IS NULL
CREATE TABLE dbo.RosterPeriods (
    ID           int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID    int NOT NULL,
    NameAr       nvarchar(200) NOT NULL,
    NameEn       nvarchar(200) NOT NULL,
    StartDate    datetime2(7) NOT NULL,
    EndDate      datetime2(7) NOT NULL,
    -- Optional narrowing. NULL means the period covers the company rather than one site.
    BranchID     int NULL,
    Status       nvarchar(20) NOT NULL CONSTRAINT DF_RosterPeriod_Status DEFAULT('Draft'),
    PublishedAt  datetime2(7) NULL,
    PublishedBy  int NULL,
    Notes        nvarchar(2000) NULL,
    CreatedBy    int NULL,
    CreatedAt    datetime2(7) NULL,
    UpdatedBy    int NULL,
    UpdatedAt    datetime2(7) NULL,
    CONSTRAINT FK_RosterPeriod_Branch FOREIGN KEY (BranchID) REFERENCES dbo.Branches(ID)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_RosterPeriod_Company_Status' AND object_id=OBJECT_ID('dbo.RosterPeriods'))
CREATE INDEX IX_RosterPeriod_Company_Status ON dbo.RosterPeriods (CompanyID, Status)
    INCLUDE (StartDate, EndDate, BranchID);
GO

/* ------------------------------------------------------------------------------------------------------------
   ASSIGNMENTS.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID('dbo.RosterAssignments','U') IS NULL
CREATE TABLE dbo.RosterAssignments (
    ID            int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    /*  Repeated from the period on purpose: "everyone scheduled in this company next Tuesday" is a real query, and
        a join-only tenant boundary would leave it silently unfiltered the first time somebody omitted the join.  */
    CompanyID     int NOT NULL,
    PeriodID      int NOT NULL,
    EmployeeID    int NOT NULL,
    BranchID      int NULL,
    -- The day the shift BEGINS. An overnight shift belongs to the day it starts, which is how a roster is read
    -- aloud and how PlannedStart below is derived.
    WorkDate      datetime2(7) NOT NULL,
    ShiftID       int NOT NULL,
    /*  MATERIALISED, not derived at read time. The shift definition is provenance; these two are the commitment.
        Editing "Night" from 22:00 to 23:00 next month must not silently rewrite what a published roster already
        promised somebody last week. They are also what makes overlap detection work on real instants, so an
        overnight shift and the next morning's early shift can be compared at all.  */
    PlannedStart  datetime2(7) NOT NULL,
    PlannedEnd    datetime2(7) NOT NULL,
    Status        nvarchar(20) NOT NULL CONSTRAINT DF_RosterAssign_Status DEFAULT('Planned'),
    Note          nvarchar(1000) NULL,
    CreatedBy     int NULL,
    CreatedAt     datetime2(7) NULL,
    UpdatedBy     int NULL,
    UpdatedAt     datetime2(7) NULL,
    CONSTRAINT FK_RosterAssign_Period   FOREIGN KEY (PeriodID)   REFERENCES dbo.RosterPeriods(ID) ON DELETE CASCADE,
    CONSTRAINT FK_RosterAssign_Employee FOREIGN KEY (EmployeeID) REFERENCES dbo.Employee(ID),
    CONSTRAINT FK_RosterAssign_Shift    FOREIGN KEY (ShiftID)    REFERENCES dbo.WorkShifts(ID),
    CONSTRAINT FK_RosterAssign_Branch   FOREIGN KEY (BranchID)   REFERENCES dbo.Branches(ID),
    /*  A window that ends at or before it starts is unusable. The service refuses to create one and detection
        flags any that predate this constraint; the check is what stops a direct INSERT from planting one.  */
    CONSTRAINT CK_RosterAssign_Window CHECK (PlannedEnd > PlannedStart)
);
GO

/*  THE OVERLAP QUERY: this employee's live assignments touching a time window. Filtered to Planned because a
    cancelled row can never conflict, and cancelled rows accumulate.  */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_RosterAssign_Employee_Window' AND object_id=OBJECT_ID('dbo.RosterAssignments'))
CREATE INDEX IX_RosterAssign_Employee_Window ON dbo.RosterAssignments (CompanyID, EmployeeID, PlannedStart)
    INCLUDE (PeriodID, ShiftID, PlannedEnd, WorkDate, BranchID)
    WHERE Status = 'Planned';
GO

/*  THE GRID QUERY: one period, ordered as the screen draws it.  */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_RosterAssign_Period' AND object_id=OBJECT_ID('dbo.RosterAssignments'))
CREATE INDEX IX_RosterAssign_Period ON dbo.RosterAssignments (PeriodID, WorkDate, EmployeeID);
GO

/*  THE PLANNED-VS-ACTUAL JOIN: (CompanyID, EmployeeID, WorkDate), matching the natural key AttendanceRecord
    already carries. This index is the reason that join needs no foreign key.  */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_RosterAssign_Company_Employee_Date' AND object_id=OBJECT_ID('dbo.RosterAssignments'))
CREATE INDEX IX_RosterAssign_Company_Employee_Date ON dbo.RosterAssignments (CompanyID, WorkDate, EmployeeID)
    INCLUDE (PlannedStart, PlannedEnd, Status, ShiftID);
GO

/* ------------------------------------------------------------------------------------------------------------
   REVISIONS — evidence that a published roster changed.

   A table rather than three columns on the assignment. A RevisedAt/RevisedBy/Reason triple records exactly one
   change: the second overwrites the first, and the row then testifies only to the most recent edit. A roster is
   the document people dispute after the fact ("nobody told me the shift moved"), so the second change is
   precisely the one worth keeping.

   Only PUBLISHED assignments generate a row here. Editing a draft is drafting, not history.
   ------------------------------------------------------------------------------------------------------------ */
IF OBJECT_ID('dbo.RosterAssignmentRevisions','U') IS NULL
CREATE TABLE dbo.RosterAssignmentRevisions (
    ID            int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID     int NOT NULL,
    AssignmentID  int NOT NULL,
    ChangedField  nvarchar(100) NOT NULL,
    OldValue      nvarchar(400) NULL,
    NewValue      nvarchar(400) NULL,
    -- Required by the service. A change to a published roster with no stated reason is the silent mutation this
    -- table exists to prevent, so an empty string here would defeat the whole point.
    Reason        nvarchar(1000) NOT NULL,
    ChangedBy     int NULL,
    ChangedAt     datetime2(7) NOT NULL,
    CONSTRAINT FK_RosterRevision_Assignment FOREIGN KEY (AssignmentID)
        REFERENCES dbo.RosterAssignments(ID) ON DELETE CASCADE,
    CONSTRAINT CK_RosterRevision_Reason CHECK (LEN(LTRIM(RTRIM(Reason))) > 0)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_RosterRevision_Assignment' AND object_id=OBJECT_ID('dbo.RosterAssignmentRevisions'))
CREATE INDEX IX_RosterRevision_Assignment ON dbo.RosterAssignmentRevisions (AssignmentID, ChangedAt DESC);
GO
