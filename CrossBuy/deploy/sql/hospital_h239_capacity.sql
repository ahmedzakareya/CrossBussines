-- ==========================================================================================
-- Hospital H239 - the capacity centre's discharge plans: per admission, the expected day, how sure, and the barriers.
--
--   HospitalDischargePlans   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
    THROW 52039, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalDischargePlans','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDischargePlans (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AdmissionId int NOT NULL,
        PatientId int NOT NULL,
        ExpectedDate date NOT NULL,
        Confidence nvarchar(10) NOT NULL,
        Barriers nvarchar(200) NULL,
        Note nvarchar(300) NULL,
        UpdatedAt datetime2 NOT NULL,
        UpdatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalDischargePlans PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDischargePlans_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions (ID),
        CONSTRAINT FK_HospitalDischargePlans_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalDischargePlans_Confidence CHECK (Confidence IN ('Likely','Possible'))
    );
    CREATE UNIQUE INDEX IX_HospitalDischargePlans_CompanyId_AdmissionId ON dbo.HospitalDischargePlans (CompanyId, AdmissionId);
    CREATE INDEX IX_HospitalDischargePlans_CompanyId_PatientId ON dbo.HospitalDischargePlans (CompanyId, PatientId);
END

COMMIT TRANSACTION;
