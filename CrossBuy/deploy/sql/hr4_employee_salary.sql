-- ==========================================================================================
-- HR4 (2026-11-08): a salary per employee, effective-dated (EmployeeSalaries).
-- Before this, pay hung off the LEAVE POLICY (SalaryPolicies.LeavePolicyTypeID): everyone on one policy earned the same.
-- A raise is a new row from its date; the pay in force on a date is the latest row on or before it. Employees with no row
-- keep falling back to their policy's salary, so applying this table changes no figure until HR sets a salary.
-- Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF OBJECT_ID(N'dbo.EmployeeSalaries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeSalaries (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeSalaries PRIMARY KEY,
        CompanyID             int            NOT NULL,
        EmployeeID            int            NOT NULL CONSTRAINT FK_EmployeeSalaries_Employee REFERENCES dbo.Employee(ID),
        EffectiveFrom         date           NOT NULL,
        BasicSalary           decimal(18,3)  NOT NULL CONSTRAINT CK_EmployeeSalaries_Basic CHECK (BasicSalary >= 0),
        HousingAllowance      decimal(18,3)  NOT NULL CONSTRAINT DF_EmployeeSalaries_Housing DEFAULT 0 CONSTRAINT CK_EmployeeSalaries_Housing CHECK (HousingAllowance >= 0),
        TransportAllowance    decimal(18,3)  NOT NULL CONSTRAINT DF_EmployeeSalaries_Transport DEFAULT 0 CONSTRAINT CK_EmployeeSalaries_Transport CHECK (TransportAllowance >= 0),
        OtherAllowances       decimal(18,3)  NOT NULL CONSTRAINT DF_EmployeeSalaries_Other DEFAULT 0 CONSTRAINT CK_EmployeeSalaries_Other CHECK (OtherAllowances >= 0),
        Note                  nvarchar(400)  NULL,
        CreatedAt             datetime2      NOT NULL CONSTRAINT DF_EmployeeSalaries_CreatedAt DEFAULT SYSUTCDATETIME(),
        CreatedByEmployeeId   int            NULL
    );
    -- one pay per employee per effective date: a second change on the same day replaces the first
    CREATE UNIQUE INDEX UX_EmployeeSalaries_Employee_From ON dbo.EmployeeSalaries (EmployeeID, EffectiveFrom);
    CREATE INDEX IX_EmployeeSalaries_Company ON dbo.EmployeeSalaries (CompanyID, EmployeeID);
END
