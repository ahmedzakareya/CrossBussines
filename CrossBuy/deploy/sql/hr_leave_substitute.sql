-- ==========================================================================================
-- HR (2026-11-18): the colleague who covers during a leave — "البديل أثناء الغياب" on the owner's mobile reference.
--
-- A leave request names, optionally, one active colleague of the same company who covers the work while the
-- requester is away. The application checks the company, the colleague being active, and that it is not the
-- requester. Nullable: every existing request simply has none. A foreign key to Employee guards the paths that do
-- not go through the application (a data fix, a seed, a direct insert), the same as EmployeeID beside it.
--
-- Idempotent: the column and the key are added only if missing; nothing is filled.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF COL_LENGTH('dbo.LeaveRequests', 'SubstituteEmployeeID') IS NULL
    ALTER TABLE dbo.LeaveRequests ADD SubstituteEmployeeID int NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_LeaveRequests_Employee_SubstituteEmployeeID')
    ALTER TABLE dbo.LeaveRequests WITH CHECK
        ADD CONSTRAINT FK_LeaveRequests_Employee_SubstituteEmployeeID FOREIGN KEY (SubstituteEmployeeID) REFERENCES dbo.Employee (ID);
GO
