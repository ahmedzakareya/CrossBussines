-- ==========================================================================================
-- Hospital H119 — every treatment session is charged: a dialysis session (HD-SESSION), a chemotherapy cycle given
-- (CHEMO-ADMIN) and a physiotherapy session attended (PT-SESSION) keep the bill line they were charged on — once.
--   HospitalDialysisSessions.BillLineId
--   HospitalChemoCycles.BillLineId
--   HospitalRehabSessions.BillLineId
--
-- Adds three nullable columns and their references; changes no data. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalBillLines','U') IS NULL OR OBJECT_ID('dbo.HospitalDialysisSessions','U') IS NULL
   OR OBJECT_ID('dbo.HospitalChemoCycles','U') IS NULL OR OBJECT_ID('dbo.HospitalRehabSessions','U') IS NULL
    THROW 51990, 'Run hospital_h6_billing.sql, hospital_h87_dialysis.sql, hospital_h88_oncology.sql and hospital_h89_rehab.sql first.', 1;

IF COL_LENGTH('dbo.HospitalDialysisSessions', 'BillLineId') IS NULL
    ALTER TABLE dbo.HospitalDialysisSessions ADD BillLineId int NULL;

IF COL_LENGTH('dbo.HospitalChemoCycles', 'BillLineId') IS NULL
    ALTER TABLE dbo.HospitalChemoCycles ADD BillLineId int NULL;

IF COL_LENGTH('dbo.HospitalRehabSessions', 'BillLineId') IS NULL
    ALTER TABLE dbo.HospitalRehabSessions ADD BillLineId int NULL;

COMMIT TRANSACTION;
GO
-- the references name the new columns, so they compile in a batch of their own
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_HospitalDialysisSessions_BillLine')
    ALTER TABLE dbo.HospitalDialysisSessions WITH CHECK ADD CONSTRAINT FK_HospitalDialysisSessions_BillLine FOREIGN KEY (BillLineId) REFERENCES dbo.HospitalBillLines(ID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_HospitalChemoCycles_BillLine')
    ALTER TABLE dbo.HospitalChemoCycles WITH CHECK ADD CONSTRAINT FK_HospitalChemoCycles_BillLine FOREIGN KEY (BillLineId) REFERENCES dbo.HospitalBillLines(ID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_HospitalRehabSessions_BillLine')
    ALTER TABLE dbo.HospitalRehabSessions WITH CHECK ADD CONSTRAINT FK_HospitalRehabSessions_BillLine FOREIGN KEY (BillLineId) REFERENCES dbo.HospitalBillLines(ID);

COMMIT TRANSACTION;
