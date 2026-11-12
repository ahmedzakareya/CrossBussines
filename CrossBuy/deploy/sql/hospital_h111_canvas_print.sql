-- ==========================================================================================
-- Hospital H111 — a clinical drawing's printed paper (Report Studio). The drawing keeps the picture the doctor saw
-- when it was saved (cleaned SVG), so its paper prints the same drawing:
--   HospitalCanvasSheets.SnapshotSvg   the drawing's picture (NULL until the drawing is next saved)
--
-- Additive only (one nullable column). Idempotent (COL_LENGTH guard).
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

IF OBJECT_ID('dbo.HospitalCanvasSheets','U') IS NULL
    THROW 51990, 'Run hospital_h99_canvas.sql first.', 1;

IF COL_LENGTH('dbo.HospitalCanvasSheets', 'SnapshotSvg') IS NULL
    ALTER TABLE dbo.HospitalCanvasSheets ADD SnapshotSvg nvarchar(max) NULL;

COMMIT TRANSACTION;
