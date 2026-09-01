-- =============================================================================================
-- Central Document Platform — slice documents_004_expiry.
--
-- AUTHORED, NOT EXECUTED. No DDL ran for this batch: no production write, no CrossBuyDev change.
--
-- FOUR COLUMNS, and each one had to prove the existing model could not already say it.
--
--   PlatformDocumentVersions.DocumentNumber / IssueDate / ExpiryDate
--     A renewal replaces a passport with a DIFFERENT passport. The document row can hold only one
--     number and one pair of dates, and it must hold the CURRENT ones - otherwise the validity rule
--     would have to walk the version chain to answer "is this employee covered today", and that rule
--     is deliberately a single-row read.
--
--     So before this slice, renewing OVERWROTE the old passport's number and dates and nothing
--     remembered them. The blob survived; the identity of the document it was a scan of did not.
--     That is the "pretend the renewal never happened" failure in reverse - it pretended the OLD
--     document never happened. Each version now records the instrument it carries.
--
--     NULLABLE, because a version written before this slice genuinely does not know which instrument
--     it was, and back-filling it from the document row would be a guess: the document row holds the
--     CURRENT values, so copying them onto historical versions would stamp today's passport number
--     onto a scan of the one it replaced. A null that admits ignorance is worth more than a
--     confident wrong answer, so NOTHING IS BACK-FILLED.
--
--   PlatformDocumentTypes.ExpiryWarningDays
--     How much notice this KIND of document deserves. The type model could not express it and I
--     looked for a way to avoid the column: RequiresExpiryDate answers whether a date is DEMANDED,
--     which is a different question, and MetadataSchema describes the keys a DOCUMENT may carry and
--     is validated against document metadata on write - putting type policy there would make one
--     field mean two unrelated things. Nothing else on the type holds a number.
--
--     A passport wants months of notice because renewing one takes months; a gate pass wants a week.
--     That is a business decision per type, so it is configuration - which is also what keeps the
--     words Passport and GatePass out of the platform's code.
--
--     NULL means the platform default (30 days), NOT "never warn". A type nobody has configured must
--     still give notice, because the failure it prevents is a document expiring in silence.
--
-- WHAT THIS SLICE DELIBERATELY DOES NOT ADD:
--
--   * NO reminder table, NO document-task table. Expiry follow-up is written to the platform's own
--     TaskItems / TaskAutoLogs, keyed by (document, expiry date) - see DocumentExpiryProjection.
--   * NO new status value and NO CHECK change. Expiring-soon and expired are DERIVED from the date by
--     the canonical evaluator. Stamping a status from a worker would create a second source of truth
--     that is stale between ticks, and the first disagreement would be a document the checklist calls
--     valid and the dashboard calls expired.
--   * NO new index. The projection filters (CompanyID, Status, ExpiryDate) over a bounded date
--     window, and IX on (CompanyID, ExpiryDate) already exists from the base slice; Status then
--     filters a handful of rows. A second overlapping index would cost every write to save nothing
--     measurable on a query that runs once a day.
--
-- ADDITIVE + IDEMPOTENT: four nullable columns behind COL_LENGTH guards. No existing row is read,
-- rewritten or migrated. No legacy document store is touched.
--
-- RUN IT WITH sqlcmd -I (QUOTED_IDENTIFIER ON), for consistency with the other slices in this tree.
-- Requires documents_003_lifecycle to have been applied first.
-- =============================================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID('dbo.PlatformDocumentVersions') IS NULL OR OBJECT_ID('dbo.PlatformDocumentTypes') IS NULL
BEGIN
    RAISERROR ('documents_004 REFUSED: the Central Document Platform tables do not exist. Apply the base slice and documents_003_lifecycle first; this slice alters, it does not create.', 16, 1);
END
GO

-- ---- 1. the version records the instrument it carries -------------------------------------------
IF COL_LENGTH('dbo.PlatformDocumentVersions', 'DocumentNumber') IS NULL
BEGIN
    ALTER TABLE dbo.PlatformDocumentVersions ADD DocumentNumber nvarchar(200) NULL;
    PRINT 'PlatformDocumentVersions.DocumentNumber added.';
END
ELSE PRINT 'PlatformDocumentVersions.DocumentNumber already present.';
GO

IF COL_LENGTH('dbo.PlatformDocumentVersions', 'IssueDate') IS NULL
BEGIN
    ALTER TABLE dbo.PlatformDocumentVersions ADD IssueDate datetime2 NULL;
    PRINT 'PlatformDocumentVersions.IssueDate added.';
END
ELSE PRINT 'PlatformDocumentVersions.IssueDate already present.';
GO

IF COL_LENGTH('dbo.PlatformDocumentVersions', 'ExpiryDate') IS NULL
BEGIN
    ALTER TABLE dbo.PlatformDocumentVersions ADD ExpiryDate datetime2 NULL;
    PRINT 'PlatformDocumentVersions.ExpiryDate added.';
END
ELSE PRINT 'PlatformDocumentVersions.ExpiryDate already present.';
GO

-- ---- 2. the type carries its lead-time policy ---------------------------------------------------
IF COL_LENGTH('dbo.PlatformDocumentTypes', 'ExpiryWarningDays') IS NULL
BEGIN
    ALTER TABLE dbo.PlatformDocumentTypes ADD ExpiryWarningDays int NULL;
    PRINT 'PlatformDocumentTypes.ExpiryWarningDays added.';
END
ELSE PRINT 'PlatformDocumentTypes.ExpiryWarningDays already present.';
GO

PRINT 'documents_004_expiry complete. Structure only: no document altered, no version back-filled, no type seeded, no task created, no legacy row touched.';
GO
