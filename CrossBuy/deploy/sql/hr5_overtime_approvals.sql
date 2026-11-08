-- ==========================================================================================
-- HR5 (2026-11-08): overtime is approved before it is paid, within the yearly cap.
--   OvertimeApprovals  one decision per employee per month: the minutes approved by day type (working / rest / holiday).
--   When the company's rule pack says overtime.requiresApproval, payroll pays only what is approved here (no row = nothing
--   yet); otherwise payroll keeps paying the attendance overtime as before. The yearly cap (overtime.yearlyCapHours) is
--   applied at approval: a month cannot be approved past what is left of the year.
-- Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF OBJECT_ID(N'dbo.OvertimeApprovals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OvertimeApprovals (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OvertimeApprovals PRIMARY KEY,
        CompanyID             int           NOT NULL,
        EmployeeID            int           NOT NULL CONSTRAINT FK_OvertimeApprovals_Employee REFERENCES dbo.Employee(ID),
        Year                  int           NOT NULL,
        Month                 int           NOT NULL CONSTRAINT CK_OvertimeApprovals_Month CHECK (Month BETWEEN 1 AND 12),
        Status                nvarchar(20)  NOT NULL CONSTRAINT CK_OvertimeApprovals_Status CHECK (Status IN (N'Approved', N'Rejected')),
        WorkingMinutes        int           NOT NULL CONSTRAINT DF_OvertimeApprovals_W DEFAULT 0,
        RestDayMinutes        int           NOT NULL CONSTRAINT DF_OvertimeApprovals_R DEFAULT 0,
        HolidayMinutes        int           NOT NULL CONSTRAINT DF_OvertimeApprovals_H DEFAULT 0,
        RequestedMinutes      int           NOT NULL CONSTRAINT DF_OvertimeApprovals_Q DEFAULT 0,
        Note                  nvarchar(400) NULL,
        DecidedByEmployeeId   int           NULL,
        DecidedAt             datetime2     NOT NULL CONSTRAINT DF_OvertimeApprovals_At DEFAULT SYSUTCDATETIME()
    );
    CREATE UNIQUE INDEX UX_OvertimeApprovals_Emp_Period ON dbo.OvertimeApprovals (CompanyID, EmployeeID, Year, Month);
END
-- the switch lives in every rule pack (off by default, so nothing changes until a company turns it on)
UPDATE dbo.HrRulePacks SET RulesJson = JSON_MODIFY(RulesJson, '$.overtime.requiresApproval', CAST(0 AS bit))
 WHERE JSON_VALUE(RulesJson, '$.overtime.requiresApproval') IS NULL;
