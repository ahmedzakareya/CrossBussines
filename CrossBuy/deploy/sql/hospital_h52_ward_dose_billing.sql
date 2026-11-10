-- ==========================================================================================
-- Hospital H52 — a controlled drug GIVEN to a patient on the ward (the H39 register) is charged to the patient's bill:
-- the stay's bill while admitted, otherwise the patient's open bill, priced as the pharmacy prices the item. The entry
-- keeps the line it was charged as.
--   HospitalControlledEntries  + BillLineId
--
-- Additive only (one nullable column). Idempotent (COL_LENGTH guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalControlledEntries','U') IS NULL
    THROW 51990, 'Run hospital_h39_controlled_drugs.sql first.', 1;

IF COL_LENGTH('dbo.HospitalControlledEntries', 'BillLineId') IS NULL
    ALTER TABLE dbo.HospitalControlledEntries ADD BillLineId int NULL;

COMMIT TRANSACTION;
