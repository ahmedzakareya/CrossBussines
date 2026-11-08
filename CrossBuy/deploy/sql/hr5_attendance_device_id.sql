-- HR5 (2026-11-08): the ID an employee has on the fingerprint / attendance device, so a device export maps to people.
-- Unique per company when set. Idempotent.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF COL_LENGTH(N'dbo.Employee', N'AttendanceDeviceId') IS NULL
    ALTER TABLE dbo.Employee ADD AttendanceDeviceId nvarchar(50) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Employee_Company_AttendanceDeviceId' AND object_id = OBJECT_ID(N'dbo.Employee'))
    CREATE UNIQUE INDEX UX_Employee_Company_AttendanceDeviceId ON dbo.Employee (EmpCompanyID, AttendanceDeviceId) WHERE AttendanceDeviceId IS NOT NULL;
