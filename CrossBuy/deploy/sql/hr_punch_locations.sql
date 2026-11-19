-- ==========================================================================================
-- HR (2026-11-19): punching attendance from the phone, inside the office — "بصمة الآن · داخل نطاق المكتب" in the owner's
-- mobile prototype.
--
-- Each branch may carry its office location and the radius a phone punch must fall within. The SERVER measures the
-- distance from the phone's reported position to the branch and refuses a punch outside the radius; a branch with no
-- location set refuses every phone punch (fail closed — an unconfigured office is not "anywhere").
--
-- Idempotent: each column and check is added only if missing; nothing is filled.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF COL_LENGTH('dbo.Branches', 'Latitude') IS NULL          ALTER TABLE dbo.Branches ADD Latitude decimal(9,6) NULL;
IF COL_LENGTH('dbo.Branches', 'Longitude') IS NULL         ALTER TABLE dbo.Branches ADD Longitude decimal(9,6) NULL;
IF COL_LENGTH('dbo.Branches', 'PunchRadiusMeters') IS NULL ALTER TABLE dbo.Branches ADD PunchRadiusMeters int NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Branches_PunchLocation')
    ALTER TABLE dbo.Branches WITH CHECK ADD CONSTRAINT CK_Branches_PunchLocation CHECK (
        (Latitude IS NULL OR Latitude BETWEEN -90 AND 90)
        AND (Longitude IS NULL OR Longitude BETWEEN -180 AND 180)
        AND (PunchRadiusMeters IS NULL OR PunchRadiusMeters BETWEEN 10 AND 5000));
GO
