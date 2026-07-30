-- TM-3: Timesheet entries. Additive + idempotent. Operational only — NO GL/stock.
IF OBJECT_ID('TimesheetEntries','U') IS NULL
CREATE TABLE TimesheetEntries (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId INT NOT NULL,
    TaskId INT NOT NULL,
    EmployeeId INT NOT NULL,
    WorkDate DATETIME2 NOT NULL,
    Hours DECIMAL(9,2) NOT NULL DEFAULT 0,
    Description NVARCHAR(500) NULL,
    Source NVARCHAR(10) NOT NULL DEFAULT 'Manual',
    StartedAt DATETIME2 NULL,
    EndedAt DATETIME2 NULL,
    IsBillable BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Timesheet_Task' AND object_id=OBJECT_ID('TimesheetEntries'))
    CREATE INDEX IX_Timesheet_Task ON TimesheetEntries (CompanyId, TaskId);
GO
-- fast lookup of a running timer (one open Timer row per employee)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Timesheet_Running' AND object_id=OBJECT_ID('TimesheetEntries'))
    CREATE INDEX IX_Timesheet_Running ON TimesheetEntries (CompanyId, EmployeeId, EndedAt);
GO
