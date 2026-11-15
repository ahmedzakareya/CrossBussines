-- ==========================================================================================
-- Hospital H131 — a dose the pharmacy store issued but nobody gave returns to the store it left. Cancelling such a
-- delivery replays its outgoing stock movements back in (the same batch, quantity and cost; the cost back off cost of
-- goods) in the cancel's own transaction, and the delivery remembers when:
--   HospitalMedDeliveries.ReturnedAt   null unless the dose went back into the store
--
-- One nullable column; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalMedDeliveries','U') IS NULL
    THROW 51990, 'Run hospital_h21_med_routing.sql first.', 1;

IF COL_LENGTH('dbo.HospitalMedDeliveries', 'ReturnedAt') IS NULL ALTER TABLE dbo.HospitalMedDeliveries ADD ReturnedAt datetime2 NULL;

COMMIT TRANSACTION;
