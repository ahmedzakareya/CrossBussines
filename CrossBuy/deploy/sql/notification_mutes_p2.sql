-- Communication Hub P2: per-employee notification category mute. Idempotent, additive. Safe to re-run.
IF OBJECT_ID('NotificationMutes','U') IS NULL
BEGIN
    CREATE TABLE NotificationMutes (
        ID         INT IDENTITY(1,1) PRIMARY KEY,
        EmployeeId INT          NOT NULL,
        Category   NVARCHAR(40) NOT NULL,
        CreatedAt  DATETIME2    NOT NULL
    );
    CREATE UNIQUE INDEX UX_NotifMute_Emp_Cat ON NotificationMutes (EmployeeId, Category);
END
GO
