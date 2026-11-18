-- ==========================================================================================
-- Hospital H185 - the orthodontic record behind the contract.
--
--   HospitalOrthoCases    + the diagnosis (AngleClass, OverjetMm, OverbiteMm, CrowdingUpperMm, CrowdingLowerMm, Iotn)
--                         + the debond day and each arch's retainer (DebondedOn, RetainerUpper, RetainerLower)
--   HospitalOrthoVisits   + what an adjustment fitted and saw (ArchwireUpper, ArchwireLower, Elastics, Hygiene, Breakages)
--
-- Nullable columns; the cases opened before this slice keep no diagnosis. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalOrthoCases','U') IS NULL OR OBJECT_ID('dbo.HospitalOrthoVisits','U') IS NULL
    THROW 51985, 'Run the dental cases slice (hospital_h17_dental_cases.sql) first.', 1;

IF COL_LENGTH('dbo.HospitalOrthoCases', 'AngleClass') IS NULL ALTER TABLE dbo.HospitalOrthoCases ADD AngleClass nvarchar(4) NULL;
IF COL_LENGTH('dbo.HospitalOrthoCases', 'OverjetMm') IS NULL ALTER TABLE dbo.HospitalOrthoCases ADD OverjetMm decimal(4,1) NULL;
IF COL_LENGTH('dbo.HospitalOrthoCases', 'OverbiteMm') IS NULL ALTER TABLE dbo.HospitalOrthoCases ADD OverbiteMm decimal(4,1) NULL;
IF COL_LENGTH('dbo.HospitalOrthoCases', 'CrowdingUpperMm') IS NULL ALTER TABLE dbo.HospitalOrthoCases ADD CrowdingUpperMm decimal(4,1) NULL;
IF COL_LENGTH('dbo.HospitalOrthoCases', 'CrowdingLowerMm') IS NULL ALTER TABLE dbo.HospitalOrthoCases ADD CrowdingLowerMm decimal(4,1) NULL;
IF COL_LENGTH('dbo.HospitalOrthoCases', 'Iotn') IS NULL ALTER TABLE dbo.HospitalOrthoCases ADD Iotn tinyint NULL;
IF COL_LENGTH('dbo.HospitalOrthoCases', 'DebondedOn') IS NULL ALTER TABLE dbo.HospitalOrthoCases ADD DebondedOn date NULL;
IF COL_LENGTH('dbo.HospitalOrthoCases', 'RetainerUpper') IS NULL ALTER TABLE dbo.HospitalOrthoCases ADD RetainerUpper nvarchar(10) NULL;
IF COL_LENGTH('dbo.HospitalOrthoCases', 'RetainerLower') IS NULL ALTER TABLE dbo.HospitalOrthoCases ADD RetainerLower nvarchar(10) NULL;

IF COL_LENGTH('dbo.HospitalOrthoVisits', 'ArchwireUpper') IS NULL ALTER TABLE dbo.HospitalOrthoVisits ADD ArchwireUpper nvarchar(40) NULL;
IF COL_LENGTH('dbo.HospitalOrthoVisits', 'ArchwireLower') IS NULL ALTER TABLE dbo.HospitalOrthoVisits ADD ArchwireLower nvarchar(40) NULL;
IF COL_LENGTH('dbo.HospitalOrthoVisits', 'Elastics') IS NULL ALTER TABLE dbo.HospitalOrthoVisits ADD Elastics nvarchar(60) NULL;
IF COL_LENGTH('dbo.HospitalOrthoVisits', 'Hygiene') IS NULL ALTER TABLE dbo.HospitalOrthoVisits ADD Hygiene nvarchar(6) NULL;
IF COL_LENGTH('dbo.HospitalOrthoVisits', 'Breakages') IS NULL ALTER TABLE dbo.HospitalOrthoVisits ADD Breakages tinyint NULL;

COMMIT TRANSACTION;
GO

SET QUOTED_IDENTIFIER ON;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalOrthoCases_Diagnosis')
    ALTER TABLE dbo.HospitalOrthoCases ADD CONSTRAINT CK_HospitalOrthoCases_Diagnosis CHECK (
        (AngleClass IS NULL OR AngleClass IN ('I', 'II1', 'II2', 'III')) AND (Iotn IS NULL OR Iotn BETWEEN 1 AND 5)
        AND (RetainerUpper IS NULL OR RetainerUpper IN ('Hawley', 'Essix', 'Bonded', 'None'))
        AND (RetainerLower IS NULL OR RetainerLower IN ('Hawley', 'Essix', 'Bonded', 'None')));
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalOrthoVisits_Hygiene')
    ALTER TABLE dbo.HospitalOrthoVisits ADD CONSTRAINT CK_HospitalOrthoVisits_Hygiene CHECK (Hygiene IS NULL OR Hygiene IN ('Good', 'Fair', 'Poor'));
GO
