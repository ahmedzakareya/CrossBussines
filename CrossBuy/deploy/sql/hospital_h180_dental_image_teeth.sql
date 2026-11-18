-- ==========================================================================================
-- Hospital H180 - the teeth a dental image shows, so a tooth's radiographs are found by it. (The chart's history on a
-- past date needs no column: it is rebuilt from when each mark was recorded, completed and voided.)
--
--   HospitalRadOrders   + Teeth (FDI numbers, comma separated)
--
-- One nullable column. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalRadOrders','U') IS NULL
    THROW 51980, 'Run the radiology slice first.', 1;

IF COL_LENGTH('dbo.HospitalRadOrders', 'Teeth') IS NULL ALTER TABLE dbo.HospitalRadOrders ADD Teeth nvarchar(60) NULL;

COMMIT TRANSACTION;
