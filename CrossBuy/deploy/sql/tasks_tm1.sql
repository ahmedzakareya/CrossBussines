-- TM-1: Task Management core table. Additive + idempotent. Operational only — NO GL/stock. Nullable where optional.
IF OBJECT_ID('TaskItems','U') IS NULL
CREATE TABLE TaskItems (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId INT NOT NULL,
    Title NVARCHAR(200) NOT NULL,
    Description NVARCHAR(MAX) NULL,
    AssigneeEmployeeId INT NOT NULL,
    CreatedByEmployeeId INT NOT NULL,
    Priority NVARCHAR(20) NOT NULL DEFAULT 'Normal',
    DueDate DATETIME2 NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'New',
    EstimatedHours DECIMAL(9,2) NULL,
    ActualHours DECIMAL(9,2) NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CompletedAt DATETIME2 NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_TaskItems_Assignee' AND object_id=OBJECT_ID('TaskItems'))
    CREATE INDEX IX_TaskItems_Assignee ON TaskItems (CompanyId, AssigneeEmployeeId, Status);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_TaskItems_Due' AND object_id=OBJECT_ID('TaskItems'))
    CREATE INDEX IX_TaskItems_Due ON TaskItems (CompanyId, DueDate);
GO
