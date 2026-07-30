-- TM-7: auto-task rules + dedupe log. Additive + idempotent. Operational only — NO GL/stock.
IF OBJECT_ID('TaskAutoRules','U') IS NULL
CREATE TABLE TaskAutoRules (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId INT NOT NULL,
    RuleType NVARCHAR(40) NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    DefaultAssigneeEmployeeId INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_TaskAutoRules_Type' AND object_id=OBJECT_ID('TaskAutoRules'))
    CREATE UNIQUE INDEX UX_TaskAutoRules_Type ON TaskAutoRules (CompanyId, RuleType);
GO
IF OBJECT_ID('TaskAutoLogs','U') IS NULL
CREATE TABLE TaskAutoLogs (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId INT NOT NULL,
    RuleKey NVARCHAR(80) NOT NULL,
    TaskId INT NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_TaskAutoLogs_Key' AND object_id=OBJECT_ID('TaskAutoLogs'))
    CREATE UNIQUE INDEX UX_TaskAutoLogs_Key ON TaskAutoLogs (CompanyId, RuleKey);
GO
