-- ==========================================================================================
-- Hospital H177 - addenda and corrections on locked clinical records (a signed visit or drawing, a closed labour).
-- Append-only: a row is never updated or deleted by the system.
--
--   HospitalClinicalAddenda   (new)
--
-- New table only; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51983, 'Run the hospital patient slices first.', 1;

IF OBJECT_ID('dbo.HospitalClinicalAddenda','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClinicalAddenda (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PatientId int NOT NULL,
        Subject nvarchar(20) NOT NULL,
        SubjectId int NOT NULL,
        Kind nvarchar(12) NOT NULL,
        Section nvarchar(20) NULL,
        Text nvarchar(2000) NOT NULL,
        Reason nvarchar(300) NULL,
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NOT NULL,
        CONSTRAINT PK_HospitalClinicalAddenda PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalClinicalAddenda_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalClinicalAddenda_Subject CHECK (Subject IN ('Encounter','Canvas','Labour')),
        CONSTRAINT CK_HospitalClinicalAddenda_Kind CHECK (Kind IN ('Addendum','Correction')),
        CONSTRAINT CK_HospitalClinicalAddenda_Reason CHECK (Kind = 'Addendum' OR Reason IS NOT NULL)
    );
    CREATE INDEX IX_HospitalClinicalAddenda_CompanyId_Subject_SubjectId ON dbo.HospitalClinicalAddenda (CompanyId, Subject, SubjectId);
    CREATE INDEX IX_HospitalClinicalAddenda_CompanyId_PatientId ON dbo.HospitalClinicalAddenda (CompanyId, PatientId);
END

COMMIT TRANSACTION;
