-- ==========================================================================================
-- Hospital H58 — births: registered on the mother's file, a live birth opening the newborn's own file linked here to
-- the mother. Numbered, final once registered (a wrong one is voided with a reason). Printed through Report Studio.
--   HospitalBirths  one row per baby (twins are two rows, BirthOrder 1 and 2)
--
-- Additive only (one new table). Idempotent (OBJECT_ID guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql and hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalBirths','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBirths (
        ID                      int IDENTITY(1,1) NOT NULL,
        CompanyId               int            NOT NULL,
        BirthNo                 nvarchar(20)   NOT NULL,
        MotherPatientId         int            NOT NULL,
        MotherAdmissionId       int            NULL,
        NewbornPatientId        int            NULL,
        BornAt                  datetime2      NOT NULL,
        Sex                     nvarchar(1)    NOT NULL,
        Outcome                 nvarchar(12)   NOT NULL,
        DeliveryMode            nvarchar(12)   NOT NULL,
        GestationWeeks          int            NULL,
        BirthOrder              int            NOT NULL,
        WeightGrams             int            NOT NULL,
        LengthCm                decimal(5,1)   NULL,
        HeadCm                  decimal(5,1)   NULL,
        Apgar1                  int            NULL,
        Apgar5                  int            NULL,
        AttendantPractitionerId int            NULL,
        Note                    nvarchar(500)  NULL,
        RegisteredAt            datetime2      NOT NULL,
        RegisteredByEmployeeId  int            NOT NULL,
        VoidedAt                datetime2      NULL,
        VoidedByEmployeeId      int            NULL,
        VoidReason              nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalBirths PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalBirths_Mother FOREIGN KEY (MotherPatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalBirths_Newborn FOREIGN KEY (NewbornPatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalBirths_Sex CHECK (Sex IN ('M','F')),
        CONSTRAINT CK_HospitalBirths_Outcome CHECK (Outcome IN ('Live','Stillbirth')),
        CONSTRAINT CK_HospitalBirths_Mode CHECK (DeliveryMode IN ('Normal','Assisted','Caesarean')),
        CONSTRAINT CK_HospitalBirths_Measures CHECK (WeightGrams BETWEEN 200 AND 7000 AND BirthOrder BETWEEN 1 AND 6
            AND (GestationWeeks IS NULL OR GestationWeeks BETWEEN 20 AND 45)
            AND (Apgar1 IS NULL OR Apgar1 BETWEEN 0 AND 10) AND (Apgar5 IS NULL OR Apgar5 BETWEEN 0 AND 10)),
        -- a live birth has the newborn's file; a stillbirth has none
        CONSTRAINT CK_HospitalBirths_Newborn CHECK ((Outcome = 'Live' AND NewbornPatientId IS NOT NULL) OR (Outcome = 'Stillbirth' AND NewbornPatientId IS NULL)),
        CONSTRAINT CK_HospitalBirths_Void CHECK ((VoidedAt IS NULL AND VoidReason IS NULL) OR (VoidedAt IS NOT NULL AND VoidReason IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalBirths_CompanyId_BirthNo ON dbo.HospitalBirths (CompanyId, BirthNo);
    CREATE INDEX IX_HospitalBirths_CompanyId_MotherPatientId ON dbo.HospitalBirths (CompanyId, MotherPatientId);
END

COMMIT TRANSACTION;
