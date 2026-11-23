-- ==========================================================================================
-- Hospital H252 - the neonatal unit's daily record of each admitted newborn: weight, length, head circumference,
-- feeds, parenteral nutrition, phototherapy, bilirubin.
--
--   HospitalNicuDays   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
    THROW 52052, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalNicuDays','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalNicuDays (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AdmissionId int NOT NULL,
        PatientId int NOT NULL,
        Day date NOT NULL,
        WeightGrams int NULL,
        LengthCm decimal(4,1) NULL,
        HeadCm decimal(4,1) NULL,
        FeedsMl int NULL,
        FeedType nvarchar(10) NULL,
        Tpn bit NOT NULL,
        Phototherapy bit NOT NULL,
        Bilirubin int NULL,
        RecordedAt datetime2 NOT NULL,
        RecordedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalNicuDays PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalNicuDays_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions (ID),
        CONSTRAINT FK_HospitalNicuDays_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalNicuDays_Weight CHECK (WeightGrams IS NULL OR WeightGrams BETWEEN 300 AND 7000)
    );
    CREATE UNIQUE INDEX IX_HospitalNicuDays_CompanyId_AdmissionId_Day ON dbo.HospitalNicuDays (CompanyId, AdmissionId, Day);
    CREATE INDEX IX_HospitalNicuDays_CompanyId_PatientId ON dbo.HospitalNicuDays (CompanyId, PatientId);
END

COMMIT TRANSACTION;
