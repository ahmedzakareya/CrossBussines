-- ==========================================================================================
-- Hospital H175 - the WHO Labour Care Guide (2020) replaces the partograph. The labour gets the guide's first section
-- (parity, onset, membranes ruptured, risk factors, when active labour was diagnosed); each observation the guide's
-- other sections (supportive care, the baby, the woman, medication, shared decision-making); the liquor gains thick
-- meconium (M+++), the guide's alert.
--
--   HospitalLabours              + Parity, Onset, MembranesRupturedAt, RiskFactors, ActiveLabourAt
--   HospitalLabourObservations   + Companion, PainRelief, OralFluid, Posture, Deceleration, FetalPosition, Caput,
--                                  Moulding, UrineProtein, UrineAcetone, Oxytocin, Medicine, IvFluids, Assessment, CarePlan
--                                  liquor check widened with 'T'
--
-- Nullable columns only; the rows recorded under the partograph keep their readings. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalLabours','U') IS NULL OR OBJECT_ID('dbo.HospitalLabourObservations','U') IS NULL
    THROW 51984, 'Run hospital_h86_labour.sql first.', 1;

IF COL_LENGTH('dbo.HospitalLabours', 'Parity') IS NULL ALTER TABLE dbo.HospitalLabours ADD Parity int NULL;
IF COL_LENGTH('dbo.HospitalLabours', 'Onset') IS NULL ALTER TABLE dbo.HospitalLabours ADD Onset nvarchar(12) NULL CONSTRAINT CK_HospitalLabours_Onset CHECK (Onset IS NULL OR Onset IN ('Spontaneous','Induced'));
IF COL_LENGTH('dbo.HospitalLabours', 'MembranesRupturedAt') IS NULL ALTER TABLE dbo.HospitalLabours ADD MembranesRupturedAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalLabours', 'RiskFactors') IS NULL ALTER TABLE dbo.HospitalLabours ADD RiskFactors nvarchar(300) NULL;
IF COL_LENGTH('dbo.HospitalLabours', 'ActiveLabourAt') IS NULL ALTER TABLE dbo.HospitalLabours ADD ActiveLabourAt datetime2 NULL;

IF COL_LENGTH('dbo.HospitalLabourObservations', 'Companion') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD Companion bit NULL;
IF COL_LENGTH('dbo.HospitalLabourObservations', 'PainRelief') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD PainRelief bit NULL;
IF COL_LENGTH('dbo.HospitalLabourObservations', 'OralFluid') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD OralFluid bit NULL;
IF COL_LENGTH('dbo.HospitalLabourObservations', 'Posture') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD Posture nvarchar(2) NULL CONSTRAINT CK_HospitalLabourObservations_Posture CHECK (Posture IS NULL OR Posture IN ('SP','MO'));
IF COL_LENGTH('dbo.HospitalLabourObservations', 'Deceleration') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD Deceleration nvarchar(1) NULL CONSTRAINT CK_HospitalLabourObservations_Decel CHECK (Deceleration IS NULL OR Deceleration IN ('N','E','L','V'));
IF COL_LENGTH('dbo.HospitalLabourObservations', 'FetalPosition') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD FetalPosition nvarchar(2) NULL CONSTRAINT CK_HospitalLabourObservations_Position CHECK (FetalPosition IS NULL OR FetalPosition IN ('OA','OP','OT'));
IF COL_LENGTH('dbo.HospitalLabourObservations', 'Caput') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD Caput int NULL CONSTRAINT CK_HospitalLabourObservations_Caput CHECK (Caput IS NULL OR Caput BETWEEN 0 AND 3);
IF COL_LENGTH('dbo.HospitalLabourObservations', 'Moulding') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD Moulding int NULL CONSTRAINT CK_HospitalLabourObservations_Moulding CHECK (Moulding IS NULL OR Moulding BETWEEN 0 AND 3);
IF COL_LENGTH('dbo.HospitalLabourObservations', 'UrineProtein') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD UrineProtein int NULL CONSTRAINT CK_HospitalLabourObservations_Protein CHECK (UrineProtein IS NULL OR UrineProtein BETWEEN 0 AND 4);
IF COL_LENGTH('dbo.HospitalLabourObservations', 'UrineAcetone') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD UrineAcetone int NULL CONSTRAINT CK_HospitalLabourObservations_Acetone CHECK (UrineAcetone IS NULL OR UrineAcetone BETWEEN 0 AND 4);
IF COL_LENGTH('dbo.HospitalLabourObservations', 'Oxytocin') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD Oxytocin nvarchar(40) NULL;
IF COL_LENGTH('dbo.HospitalLabourObservations', 'Medicine') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD Medicine nvarchar(200) NULL;
IF COL_LENGTH('dbo.HospitalLabourObservations', 'IvFluids') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD IvFluids bit NULL;
IF COL_LENGTH('dbo.HospitalLabourObservations', 'Assessment') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD Assessment nvarchar(300) NULL;
IF COL_LENGTH('dbo.HospitalLabourObservations', 'CarePlan') IS NULL ALTER TABLE dbo.HospitalLabourObservations ADD CarePlan nvarchar(300) NULL;

-- the liquor's check gains thick meconium
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalLabourObservations_Liquor' AND definition LIKE '%''T''%')
BEGIN
    IF OBJECT_ID('dbo.CK_HospitalLabourObservations_Liquor','C') IS NOT NULL ALTER TABLE dbo.HospitalLabourObservations DROP CONSTRAINT CK_HospitalLabourObservations_Liquor;
    ALTER TABLE dbo.HospitalLabourObservations ADD CONSTRAINT CK_HospitalLabourObservations_Liquor CHECK (Liquor IS NULL OR Liquor IN ('I','C','M','T','B'));
END

COMMIT TRANSACTION;
