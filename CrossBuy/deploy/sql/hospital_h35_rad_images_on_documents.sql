-- ==========================================================================================
-- Hospital H35 — an imaging order's pictures on the CENTRAL DOCUMENT PLATFORM (owner ruling, 2026-11-05: every image
-- and attachment goes to the unified attachments). Each picture becomes a document on the HospitalRadOrder record;
-- HospitalRadImages stays as the order's picture list (caption, who attached it, the signed-report lock) and now
-- points at the document:
--   HospitalRadImages  DocumentId (bigint, the platform document)   — added
--   HospitalRadImages  StorageKey                                   — now NULL-able (a picture on the platform has none)
--
-- No row is changed here: the pictures already attached are moved by the application, through the platform.
-- Idempotent (COL_LENGTH guard; the nullability change only when the column is still NOT NULL).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalRadImages','U') IS NULL OR OBJECT_ID('dbo.PlatformDocuments','U') IS NULL
    THROW 51990, 'Run hospital_h31_rad_images.sql and comm_documents_001.sql first.', 1;

IF COL_LENGTH('dbo.HospitalRadImages', 'DocumentId') IS NULL ALTER TABLE dbo.HospitalRadImages ADD DocumentId bigint NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.HospitalRadImages') AND name = 'StorageKey' AND is_nullable = 0)
    ALTER TABLE dbo.HospitalRadImages ALTER COLUMN StorageKey nvarchar(32) NULL;

COMMIT TRANSACTION;
