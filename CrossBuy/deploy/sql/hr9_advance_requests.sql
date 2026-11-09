-- ==========================================================================================
-- HR9 (2026-11-09): a salary advance requested from self-service.
--   EmployeeRequests.AdvanceAmount / AdvanceInstallment  the amount asked for and the monthly instalment offered
--   EmployeeLoans.SourceRequestId                          the approved request a loan was paid out against (once)
-- The request goes through the ordinary employee-request approval chain; once approved, payroll pays it out from
-- the loans screen, which creates the loan (and its payment entry) linked back to the request. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF COL_LENGTH(N'dbo.EmployeeRequests', N'AdvanceAmount') IS NULL
    ALTER TABLE dbo.EmployeeRequests ADD AdvanceAmount decimal(18,3) NULL, AdvanceInstallment decimal(18,3) NULL;
IF COL_LENGTH(N'dbo.EmployeeLoans', N'SourceRequestId') IS NULL
    ALTER TABLE dbo.EmployeeLoans ADD SourceRequestId int NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_EmployeeLoans_SourceRequest' AND object_id = OBJECT_ID(N'dbo.EmployeeLoans'))
    CREATE UNIQUE INDEX UX_EmployeeLoans_SourceRequest ON dbo.EmployeeLoans (SourceRequestId) WHERE SourceRequestId IS NOT NULL;

-- HR9 (2026-11-09): a self-service profile change; the proposed values as JSON, applied on final approval.
IF COL_LENGTH(N'dbo.EmployeeRequests', N'ChangesJson') IS NULL
    ALTER TABLE dbo.EmployeeRequests ADD ChangesJson nvarchar(max) NULL;
