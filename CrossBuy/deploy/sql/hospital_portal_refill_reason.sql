-- ==========================================================================================
-- Hospital - the refill REASON: a patient asking for a medicine again picks why from a list (running out, lost,
-- travelling, dose changed, other), stored as a CODE so every reader sees it in their own language. The free note
-- stays, optional, for anything the list does not say; it is the patient's own words and is shown as written.
--   · HospitalPortalRefills.Reason  NVARCHAR(20) NULL, one of the codes below (NULL = an older request, no reason);
--   · an existing request whose note is exactly the Arabic for "running out" becomes Reason = 'RunningOut' with no note.
--
-- One nullable column, one check, one guarded data change. Idempotent. Apply BEFORE the code (the model maps the column).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPortalRefills','U') IS NULL
    THROW 51991, 'Run hospital_h27_portal_records.sql first.', 1;

IF COL_LENGTH('dbo.HospitalPortalRefills', 'Reason') IS NULL
    ALTER TABLE dbo.HospitalPortalRefills ADD Reason NVARCHAR(20) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalPortalRefills_Reason')
    EXEC('ALTER TABLE dbo.HospitalPortalRefills ADD CONSTRAINT CK_HospitalPortalRefills_Reason
          CHECK (Reason IS NULL OR Reason IN (''RunningOut'', ''Lost'', ''Travelling'', ''DoseChanged'', ''Other''))');

EXEC('UPDATE dbo.HospitalPortalRefills SET Reason = ''RunningOut'', Note = NULL
      WHERE Reason IS NULL AND LTRIM(RTRIM(Note)) IN (N''الدواء قارب على النفاد'', N''الدواء قارب على النفاذ'', N''Running out'')');

COMMIT;
PRINT 'hospital_portal_refill_reason: done';
