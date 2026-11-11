-- ==========================================================================================
-- Hospital H92 — the unidentified patient. A temporary identity on a real file from the first minute, settled later
-- by renaming the file or merging it into the person's own.
--   HospitalUnidentifiedPatients   one temporary identity: description, police case, and how it was settled
--
-- Additive only (one new table). Idempotent (OBJECT_ID guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalUnidentifiedPatients','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalUnidentifiedPatients (
        ID                      int IDENTITY(1,1) NOT NULL,
        CompanyId               int            NOT NULL,
        PatientId               int            NOT NULL,
        Seq                     int            NOT NULL,
        ArrivedOn               date           NOT NULL,
        EstimatedAge            int            NOT NULL,
        Description             nvarchar(500)  NOT NULL,
        BroughtBy               nvarchar(150)  NULL,
        MedicoLegal             bit            NOT NULL,
        PoliceRef               nvarchar(60)   NULL,
        CreatedAt               datetime2      NOT NULL,
        CreatedByEmployeeId     int            NULL,
        IdentifiedAt            datetime2      NULL,
        IdentifiedByEmployeeId  int            NULL,
        Resolution              nvarchar(8)    NULL,
        MergedIntoPatientId     int            NULL,
        IdentifiedHow           nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalUnidentifiedPatients PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalUnidentifiedPatients_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalUnidentifiedPatients_Resolution CHECK (Resolution IS NULL OR Resolution IN ('Renamed','Merged')),
        CONSTRAINT CK_HospitalUnidentifiedPatients_Age CHECK (EstimatedAge BETWEEN 0 AND 120),
        CONSTRAINT CK_HospitalUnidentifiedPatients_Settled CHECK ((IdentifiedAt IS NULL AND Resolution IS NULL) OR (IdentifiedAt IS NOT NULL AND Resolution IS NOT NULL))
    );
    CREATE INDEX IX_HospitalUnidentifiedPatients_CompanyId_PatientId ON dbo.HospitalUnidentifiedPatients (CompanyId, PatientId);
    CREATE UNIQUE INDEX IX_HospitalUnidentifiedPatients_CompanyId_ArrivedOn_Seq ON dbo.HospitalUnidentifiedPatients (CompanyId, ArrivedOn, Seq);
END

COMMIT TRANSACTION;
