-- ==========================================================================================
-- Hospital H262 - disease registries. Membership and care gaps are computed from the record; only the outreach
-- about a gap (who reached the patient, when, what was agreed) is stored.
--
--   HospitalRegistryOutreach   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52062, 'Run the hospital patients slice first.', 1;

IF OBJECT_ID('dbo.HospitalRegistryOutreach','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRegistryOutreach (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PatientId int NOT NULL,
        Registry nvarchar(15) NOT NULL,
        Note nvarchar(500) NOT NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalRegistryOutreach PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRegistryOutreach_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalRegistryOutreach_Registry CHECK (Registry IN ('Diabetes','Hypertension','Ckd'))
    );
    CREATE INDEX IX_HospitalRegistryOutreach_CompanyId_PatientId_Registry_At ON dbo.HospitalRegistryOutreach (CompanyId, PatientId, Registry, At);
END

COMMIT TRANSACTION;
