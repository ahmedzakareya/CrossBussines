-- ==========================================================================================
-- Hospital H191 - the QuickDASH calculator in the template engine.
--
--   HospitalConcepts.Calculator   the check widened with QuickDash
--
-- Nothing else changes. Idempotent (the check is rebuilt only when it lacks the new names).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalConcepts','U') IS NULL
    THROW 51991, 'Run the clinical content slice (hospital_h93_clinical_content.sql) first.', 1;

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalConcepts_Calculator' AND definition NOT LIKE '%QuickDash%')
    ALTER TABLE dbo.HospitalConcepts DROP CONSTRAINT CK_HospitalConcepts_Calculator;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalConcepts_Calculator')
    ALTER TABLE dbo.HospitalConcepts WITH CHECK ADD CONSTRAINT CK_HospitalConcepts_Calculator
        CHECK (Calculator IS NULL OR Calculator IN ('Bmi','EgfrCkdEpi2021','Kdigo','Sum','IgeClass','QtcBazett','QtcFridericia','TiRads','Pasi','TScoreClass','QuickDash'));

COMMIT TRANSACTION;
