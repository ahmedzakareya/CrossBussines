-- ==========================================================================================
-- Hospital H34 — the translation of a SIGNED imaging report (owner ruling, 2026-11-05: "إضافة ترجمة").
-- A report signed in Arabic before H33 has no English, and signing locks it. The translation adds the English twins
-- (FindingsEn / ImpressionEn, H33's columns) without touching the signed Arabic, and records who added it and when:
--   HospitalRadOrders  TranslatedAt / TranslatedByEmployeeId
--
-- ADDS NULLABLE COLUMNS ONLY — no existing column, constraint or row changes. Idempotent (one COL_LENGTH guard each).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalRadOrders','U') IS NULL OR COL_LENGTH('dbo.HospitalRadOrders', 'FindingsEn') IS NULL
    THROW 51990, 'Run hospital_h9 and hospital_h33 first.', 1;

IF COL_LENGTH('dbo.HospitalRadOrders', 'TranslatedAt') IS NULL ALTER TABLE dbo.HospitalRadOrders ADD TranslatedAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalRadOrders', 'TranslatedByEmployeeId') IS NULL ALTER TABLE dbo.HospitalRadOrders ADD TranslatedByEmployeeId int NULL;

COMMIT TRANSACTION;
