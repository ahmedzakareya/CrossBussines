-- ==========================================================================================
-- Hospital H33 — the English twin of every free text a PATIENT reads (the owner's rule: a text shown to a user has an
-- Arabic and an English column, the input takes both, the display follows the reader's language). An English reader
-- of the portal saw the doctor's words in Arabic because these were single columns:
--   HospitalRadOrders        Findings / Impression     → FindingsEn / ImpressionEn  (the imaging report)
--   HospitalPrescriptionLines Drug / Instructions      → DrugEn / InstructionsEn    (My medicines)
--   HospitalDentalFindings   Note                      → NoteEn                     (the dental chart)
--   HospitalPortalReleases   Note                      → NoteEn                     (the doctor's word with a result)
--   HospitalPortalRefills    Answer                    → AnswerEn                   (the refill reply)
--
-- ADDS NULLABLE COLUMNS ONLY — no existing column, constraint or row changes; an untranslated row reads its Arabic
-- (DisplayName.Of falls back). Idempotent by construction (one COL_LENGTH guard per column).
--
-- STATUS: PREPARED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalRadOrders','U') IS NULL OR OBJECT_ID('dbo.HospitalPrescriptionLines','U') IS NULL OR OBJECT_ID('dbo.HospitalDentalFindings','U') IS NULL OR OBJECT_ID('dbo.HospitalPortalReleases','U') IS NULL OR OBJECT_ID('dbo.HospitalPortalRefills','U') IS NULL
    THROW 51990, 'Run hospital_h5, h9, h15 and h27 first.', 1;

IF COL_LENGTH('dbo.HospitalRadOrders', 'FindingsEn') IS NULL ALTER TABLE dbo.HospitalRadOrders ADD FindingsEn nvarchar(4000) NULL;
IF COL_LENGTH('dbo.HospitalRadOrders', 'ImpressionEn') IS NULL ALTER TABLE dbo.HospitalRadOrders ADD ImpressionEn nvarchar(1000) NULL;
IF COL_LENGTH('dbo.HospitalPrescriptionLines', 'DrugEn') IS NULL ALTER TABLE dbo.HospitalPrescriptionLines ADD DrugEn nvarchar(200) NULL;
IF COL_LENGTH('dbo.HospitalPrescriptionLines', 'InstructionsEn') IS NULL ALTER TABLE dbo.HospitalPrescriptionLines ADD InstructionsEn nvarchar(400) NULL;
IF COL_LENGTH('dbo.HospitalDentalFindings', 'NoteEn') IS NULL ALTER TABLE dbo.HospitalDentalFindings ADD NoteEn nvarchar(300) NULL;
IF COL_LENGTH('dbo.HospitalPortalReleases', 'NoteEn') IS NULL ALTER TABLE dbo.HospitalPortalReleases ADD NoteEn nvarchar(500) NULL;
IF COL_LENGTH('dbo.HospitalPortalRefills', 'AnswerEn') IS NULL ALTER TABLE dbo.HospitalPortalRefills ADD AnswerEn nvarchar(500) NULL;

COMMIT TRANSACTION;
