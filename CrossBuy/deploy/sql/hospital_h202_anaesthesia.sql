-- ==========================================================================================
-- Hospital H202 - the anaesthesia record of an operation.
--
--   HospitalAnaesthesias        (new) one per operation: the pre-anaesthetic assessment and the recovery room
--   HospitalAnaesthesiaEntries  (new) what happened in between: milestones, drugs, fluids, readings, Aldrete scores
--
-- New tables only. Idempotent. Once applied, an operation does not start without its assessment.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalSurgeries','U') IS NULL
    THROW 52002, 'Run the theatre slice first.', 1;

IF OBJECT_ID('dbo.HospitalAnaesthesias','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalAnaesthesias (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        SurgeryId int NOT NULL,
        PatientId int NOT NULL,
        Asa int NOT NULL,
        Emergency bit NOT NULL,
        WeightKg decimal(5,1) NULL,
        Mallampati int NULL,
        MouthOpeningCm decimal(4,1) NULL,
        ThyromentalCm decimal(4,1) NULL,
        DifficultAirway bit NOT NULL,
        LastSolidsAt datetime2 NULL,
        LastMilkAt datetime2 NULL,
        LastClearAt datetime2 NULL,
        FastingOverride nvarchar(300) NULL,
        Technique nvarchar(20) NOT NULL,
        PlanText nvarchar(1000) NULL,
        AssessedAt datetime2 NOT NULL,
        AssessedByEmployeeId int NULL,
        PacuInAt datetime2 NULL,
        PacuOutAt datetime2 NULL,
        PacuOutByEmployeeId int NULL,
        CONSTRAINT PK_HospitalAnaesthesias PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalAnaesthesias_Surgery FOREIGN KEY (SurgeryId) REFERENCES dbo.HospitalSurgeries (ID),
        CONSTRAINT CK_HospitalAnaesthesias_Asa CHECK (Asa BETWEEN 1 AND 6),
        CONSTRAINT CK_HospitalAnaesthesias_Technique CHECK (Technique IN ('General', 'Spinal', 'Epidural', 'Regional', 'Sedation', 'Local'))
    );
    CREATE UNIQUE INDEX IX_HospitalAnaesthesias_CompanyId_SurgeryId ON dbo.HospitalAnaesthesias (CompanyId, SurgeryId);
    CREATE INDEX IX_HospitalAnaesthesias_CompanyId_PatientId ON dbo.HospitalAnaesthesias (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalAnaesthesiaEntries','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalAnaesthesiaEntries (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AnaesthesiaId int NOT NULL,
        At datetime2 NOT NULL,
        Kind nvarchar(10) NOT NULL,
        Code nvarchar(100) NULL,
        Dose decimal(9,3) NULL,
        Unit nvarchar(20) NULL,
        Route nvarchar(20) NULL,
        HeartRate int NULL,
        SystolicBp int NULL,
        DiastolicBp int NULL,
        SpO2 int NULL,
        EtCo2 int NULL,
        Score int NULL,
        Note nvarchar(300) NULL,
        RecordedAt datetime2 NOT NULL,
        RecordedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalAnaesthesiaEntries PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalAnaesthesiaEntries_Record FOREIGN KEY (AnaesthesiaId) REFERENCES dbo.HospitalAnaesthesias (ID),
        CONSTRAINT CK_HospitalAnaesthesiaEntries_Kind CHECK (Kind IN ('Event', 'Drug', 'Vitals', 'Fluid', 'Aldrete'))
    );
    CREATE INDEX IX_HospitalAnaesthesiaEntries_CompanyId_AnaesthesiaId_At ON dbo.HospitalAnaesthesiaEntries (CompanyId, AnaesthesiaId, At);
END

COMMIT TRANSACTION;
