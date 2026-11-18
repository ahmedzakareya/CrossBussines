-- ==========================================================================================
-- Hospital H181 - the independent double-check of chemotherapy (from the review: "independent validation of the
-- therapeutic units"). A cycle is first PREPARED (its doses computed and recorded); a second clinician, never the one who
-- prepared it, checks it independently, and only then is it given (charged, its drugs requested from the pharmacy).
--
--   HospitalChemoCycles   status 'Prepared' allowed; + CheckedAt, CheckedByEmployeeId
--
-- Cycles given before H181 keep their status and have no checker. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalChemoCycles','U') IS NULL
    THROW 51979, 'Run hospital_h88_oncology.sql first.', 1;

IF COL_LENGTH('dbo.HospitalChemoCycles', 'CheckedAt') IS NULL ALTER TABLE dbo.HospitalChemoCycles ADD CheckedAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalChemoCycles', 'CheckedByEmployeeId') IS NULL ALTER TABLE dbo.HospitalChemoCycles ADD CheckedByEmployeeId int NULL;

-- the status column was nvarchar(5) ('Given', 'Held'): widened for 'Prepared'. Its check and the filtered unique index on
-- given cycles read the column, so both are dropped and built again around the change.
IF COL_LENGTH('dbo.HospitalChemoCycles', 'Status') < 20
BEGIN
    IF OBJECT_ID('dbo.CK_HospitalChemoCycles_Status','C') IS NOT NULL ALTER TABLE dbo.HospitalChemoCycles DROP CONSTRAINT CK_HospitalChemoCycles_Status;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_HospitalChemoCycles_Given' AND object_id = OBJECT_ID('dbo.HospitalChemoCycles')) DROP INDEX UX_HospitalChemoCycles_Given ON dbo.HospitalChemoCycles;
    ALTER TABLE dbo.HospitalChemoCycles ALTER COLUMN Status nvarchar(10) NOT NULL;
END
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_HospitalChemoCycles_Given' AND object_id = OBJECT_ID('dbo.HospitalChemoCycles'))
    EXEC (N'CREATE UNIQUE INDEX UX_HospitalChemoCycles_Given ON dbo.HospitalChemoCycles (CourseId, CycleNo) WHERE Status = ''Given''');

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalChemoCycles_Status' AND definition LIKE '%Prepared%')
BEGIN
    IF OBJECT_ID('dbo.CK_HospitalChemoCycles_Status','C') IS NOT NULL ALTER TABLE dbo.HospitalChemoCycles DROP CONSTRAINT CK_HospitalChemoCycles_Status;
    ALTER TABLE dbo.HospitalChemoCycles ADD CONSTRAINT CK_HospitalChemoCycles_Status CHECK (Status IN ('Given','Held','Prepared'));
END

-- "a held cycle has its date and reason" was written as Status = 'Given' OR (...): a prepared cycle is neither, so the
-- check now says what it means.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalChemoCycles_Held' AND definition NOT LIKE '%<>%')
BEGIN
    ALTER TABLE dbo.HospitalChemoCycles DROP CONSTRAINT CK_HospitalChemoCycles_Held;
    ALTER TABLE dbo.HospitalChemoCycles ADD CONSTRAINT CK_HospitalChemoCycles_Held CHECK (Status <> 'Held' OR (HeldUntil IS NOT NULL AND Reason IS NOT NULL));
END

COMMIT TRANSACTION;
