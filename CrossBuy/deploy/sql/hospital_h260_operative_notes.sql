-- ==========================================================================================
-- Hospital H260 - the structured operative note of a theatre case (one per case; signed by its surgeon).
--
--   HospitalOperativeNotes   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalSurgeries','U') IS NULL
    THROW 52060, 'Run the hospital theatre slice first.', 1;

IF OBJECT_ID('dbo.HospitalOperativeNotes','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalOperativeNotes (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        SurgeryId int NOT NULL,
        PatientId int NOT NULL,
        PreOpDiagnosis nvarchar(300) NULL,
        PostOpDiagnosis nvarchar(300) NULL,
        ProcedurePerformed nvarchar(300) NULL,
        Indication nvarchar(1000) NULL,
        Findings nvarchar(2000) NULL,
        Technique nvarchar(4000) NULL,
        Specimens nvarchar(500) NULL,
        BloodLossMl int NULL,
        Drains nvarchar(300) NULL,
        Complications nvarchar(1000) NULL,
        PostOpPlan nvarchar(2000) NULL,
        UpdatedAt datetime2 NOT NULL,
        UpdatedByEmployeeId int NULL,
        SignedAt datetime2 NULL,
        SignedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalOperativeNotes PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalOperativeNotes_Surgery FOREIGN KEY (SurgeryId) REFERENCES dbo.HospitalSurgeries (ID),
        CONSTRAINT FK_HospitalOperativeNotes_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalOperativeNotes_BloodLoss CHECK (BloodLossMl IS NULL OR BloodLossMl BETWEEN 0 AND 20000)
    );
    CREATE UNIQUE INDEX IX_HospitalOperativeNotes_CompanyId_SurgeryId ON dbo.HospitalOperativeNotes (CompanyId, SurgeryId);
    CREATE INDEX IX_HospitalOperativeNotes_CompanyId_PatientId ON dbo.HospitalOperativeNotes (CompanyId, PatientId);
END

COMMIT TRANSACTION;
