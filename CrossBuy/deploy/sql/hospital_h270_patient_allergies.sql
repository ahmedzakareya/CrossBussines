-- ==========================================================================================
-- Hospital H270 - the patient's allergies as a coded list (one row per allergy: a catalogue code or free text,
-- the reaction and its severity; "NKA" alone = no known allergy). HospitalPatients.Allergies stays the printed
-- text, written from the active rows on every save. A removed allergy keeps its row (RemovedAt).
--
--   HospitalPatientAllergies   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52270, 'Run the hospital patients slice first.', 1;

IF OBJECT_ID('dbo.HospitalPatientAllergies','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPatientAllergies (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PatientId int NOT NULL,
        AllergenCode nvarchar(20) NULL,
        Text nvarchar(100) NULL,
        Reaction nvarchar(20) NULL,
        Severity nvarchar(10) NULL,
        RecordedAt datetime2 NOT NULL,
        RecordedByEmployeeId int NULL,
        RemovedAt datetime2 NULL,
        RemovedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalPatientAllergies PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalPatientAllergies_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalPatientAllergies_What CHECK (AllergenCode IS NOT NULL OR Text IS NOT NULL),
        CONSTRAINT CK_HospitalPatientAllergies_Severity CHECK (Severity IS NULL OR Severity IN ('Mild','Moderate','Severe'))
    );
    CREATE INDEX IX_HospitalPatientAllergies_CompanyId_PatientId ON dbo.HospitalPatientAllergies (CompanyId, PatientId);
END

COMMIT TRANSACTION;
