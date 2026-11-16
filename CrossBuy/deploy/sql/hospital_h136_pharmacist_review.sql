-- ==========================================================================================
-- Hospital H136 - the pharmacist's review: priority with a target time, refusal with a reason, and the interventions log.
--   HospitalDispenses.Priority / RejectedAt / RejectedByEmployeeId / RejectReason   (nullable)
--   CK_HospitalDispenses_Status                                                    + 'Rejected'
--   HospitalPharmacyInterventions                                                  one row per clinical intervention
--
-- Nothing existing changes meaning: every existing prescription keeps its status. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDispenses','U') IS NULL
    THROW 51990, 'Run hospital_h10_pharmacy.sql first.', 1;

IF COL_LENGTH('dbo.HospitalDispenses', 'Priority') IS NULL ALTER TABLE dbo.HospitalDispenses ADD Priority nvarchar(10) NULL;
IF COL_LENGTH('dbo.HospitalDispenses', 'RejectedAt') IS NULL ALTER TABLE dbo.HospitalDispenses ADD RejectedAt datetime2 NULL;
IF COL_LENGTH('dbo.HospitalDispenses', 'RejectedByEmployeeId') IS NULL ALTER TABLE dbo.HospitalDispenses ADD RejectedByEmployeeId int NULL;
IF COL_LENGTH('dbo.HospitalDispenses', 'RejectReason') IS NULL ALTER TABLE dbo.HospitalDispenses ADD RejectReason nvarchar(400) NULL;

-- a refused prescription is a fourth state
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalDispenses_Status' AND parent_object_id = OBJECT_ID('dbo.HospitalDispenses') AND definition NOT LIKE '%Rejected%')
    ALTER TABLE dbo.HospitalDispenses DROP CONSTRAINT CK_HospitalDispenses_Status;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalDispenses_Status' AND parent_object_id = OBJECT_ID('dbo.HospitalDispenses'))
    ALTER TABLE dbo.HospitalDispenses ADD CONSTRAINT CK_HospitalDispenses_Status CHECK (Status IN ('New','Verified','Dispensed','Rejected'));

IF OBJECT_ID('dbo.HospitalPharmacyInterventions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPharmacyInterventions (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        DispenseId int NOT NULL,
        PatientId int NOT NULL,
        LineId int NULL,
        Drug nvarchar(200) NULL,
        Category nvarchar(20) NOT NULL,
        Severity nvarchar(10) NOT NULL,
        Problem nvarchar(400) NOT NULL,
        Recommendation nvarchar(400) NOT NULL,
        Outcome nvarchar(10) NOT NULL,
        Minutes int NULL,
        RecordedAt datetime2 NOT NULL,
        RecordedByEmployeeId int NULL,
        OutcomeAt datetime2 NULL,
        OutcomeByEmployeeId int NULL,
        CONSTRAINT PK_HospitalPharmacyInterventions PRIMARY KEY (ID)
    );
    CREATE INDEX IX_HospitalPharmacyInterventions_CompanyId_DispenseId ON dbo.HospitalPharmacyInterventions (CompanyId, DispenseId);
    CREATE INDEX IX_HospitalPharmacyInterventions_CompanyId_RecordedAt ON dbo.HospitalPharmacyInterventions (CompanyId, RecordedAt);
END

COMMIT TRANSACTION;
