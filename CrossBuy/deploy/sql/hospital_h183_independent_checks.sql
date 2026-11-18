-- HOSPITAL H183 — independent safety checks on the therapeutic units.
--   * Dialysis: the prescription is checked by a second clinician before a session can start (VerifiedAt/By on the
--     plan; any change to the prescription clears it); the nurse confirms the machine safety check at the start
--     (MachineChecked on the session). Plans written before this slice are taken as checked (they were in use).
--   * Immunization: the screening answer before the dose and, for a precaution given anyway, why.
-- Idempotent; additive. Apply with sqlcmd -I.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF COL_LENGTH('dbo.HospitalDialysisPlans', 'VerifiedAt') IS NULL
BEGIN
    ALTER TABLE dbo.HospitalDialysisPlans ADD VerifiedAt datetime2 NULL;
    ALTER TABLE dbo.HospitalDialysisPlans ADD VerifiedByEmployeeId int NULL;
    EXEC('UPDATE dbo.HospitalDialysisPlans SET VerifiedAt = CreatedAt');
END;
GO

IF COL_LENGTH('dbo.HospitalDialysisSessions', 'MachineChecked') IS NULL
    ALTER TABLE dbo.HospitalDialysisSessions ADD MachineChecked bit NULL;
GO

IF COL_LENGTH('dbo.HospitalImmunizations', 'Screening') IS NULL
    ALTER TABLE dbo.HospitalImmunizations ADD Screening nvarchar(16) NULL;
GO

IF COL_LENGTH('dbo.HospitalImmunizations', 'ScreenOverride') IS NULL
    ALTER TABLE dbo.HospitalImmunizations ADD ScreenOverride nvarchar(300) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalImmunizations_Screening')
    ALTER TABLE dbo.HospitalImmunizations ADD CONSTRAINT CK_HospitalImmunizations_Screening
        CHECK (Screening IS NULL OR Screening IN ('None', 'Fever', 'PriorSevere', 'Immunosuppressed', 'Pregnant'));
GO
