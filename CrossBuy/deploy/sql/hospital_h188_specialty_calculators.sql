-- ==========================================================================================
-- Hospital H188 - four specialty calculators in the template engine.
--
--   HospitalConcepts.Calculator   the check widened with QtcBazett, QtcFridericia, TiRads, Pasi, TScoreClass
--
-- Nothing else changes. Idempotent (the check is rebuilt only when it lacks the new names).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalConcepts','U') IS NULL
    THROW 51988, 'Run the clinical content slice (hospital_h93_clinical_content.sql) first.', 1;

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalConcepts_Calculator' AND definition NOT LIKE '%TScoreClass%')
    ALTER TABLE dbo.HospitalConcepts DROP CONSTRAINT CK_HospitalConcepts_Calculator;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalConcepts_Calculator')
    ALTER TABLE dbo.HospitalConcepts WITH CHECK ADD CONSTRAINT CK_HospitalConcepts_Calculator
        CHECK (Calculator IS NULL OR Calculator IN ('Bmi','EgfrCkdEpi2021','Kdigo','Sum','IgeClass','QtcBazett','QtcFridericia','TiRads','Pasi','TScoreClass'));

COMMIT TRANSACTION;
