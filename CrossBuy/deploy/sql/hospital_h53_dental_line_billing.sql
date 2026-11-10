-- ==========================================================================================
-- Hospital H53 — a completed dental plan line is charged to the patient's bill at the fee the patient accepted (on a
-- payer's price list the list decides, as for any order), in the same transaction as the completion; the line keeps
-- the bill line it was charged as, so it is never charged twice.
--   HospitalDentalPlanLines  + BillLineId
--
-- Additive only (one nullable column). Idempotent (COL_LENGTH guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDentalPlanLines','U') IS NULL
    THROW 51990, 'Run hospital_h15_dental.sql first.', 1;

IF COL_LENGTH('dbo.HospitalDentalPlanLines', 'BillLineId') IS NULL
    ALTER TABLE dbo.HospitalDentalPlanLines ADD BillLineId int NULL;

COMMIT TRANSACTION;
