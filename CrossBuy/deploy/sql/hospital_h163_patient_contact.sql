-- ==========================================================================================
-- Hospital H163 - how to reach the patient and who speaks for them: contact preferences (channel, language, best time,
-- assistance needs) and representatives (parent, guardian, attorney, next of kin).
--
-- Two new tables; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run the hospital patient slices first.', 1;

IF OBJECT_ID('dbo.HospitalContactPreferences','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalContactPreferences (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PatientId int NOT NULL,
        Channel nvarchar(10) NOT NULL,
        Language nvarchar(5) NOT NULL,
        BestTime nvarchar(10) NOT NULL,
        Needs nvarchar(100) NULL,
        InterpreterLanguage nvarchar(40) NULL,
        NeedsNote nvarchar(200) NULL,
        UpdatedAt datetime2 NOT NULL,
        UpdatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalContactPreferences PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalContactPreferences_Channel CHECK (Channel IN ('Sms','Call')),
        CONSTRAINT CK_HospitalContactPreferences_BestTime CHECK (BestTime IN ('Any','Morning','Afternoon','Evening'))
    );
    CREATE UNIQUE INDEX IX_HospitalContactPreferences_CompanyId_PatientId ON dbo.HospitalContactPreferences (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalPatientRepresentatives','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPatientRepresentatives (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PatientId int NOT NULL,
        Authority nvarchar(10) NOT NULL,
        Name nvarchar(120) NOT NULL,
        Relation nvarchar(60) NULL,
        Phone nvarchar(30) NULL,
        NationalId nvarchar(30) NULL,
        ValidTo date NULL,
        IsActive bit NOT NULL CONSTRAINT DF_HospitalPatientRepresentatives_Active DEFAULT (1),
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NULL,
        StoppedAt datetime2 NULL,
        StoppedByEmployeeId int NULL,
        StopReason nvarchar(200) NULL,
        CONSTRAINT PK_HospitalPatientRepresentatives PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalPatientRepresentatives_Authority CHECK (Authority IN ('Parent','Guardian','Attorney','NextOfKin'))
    );
    CREATE INDEX IX_HospitalPatientRepresentatives_CompanyId_PatientId ON dbo.HospitalPatientRepresentatives (CompanyId, PatientId);
END

COMMIT TRANSACTION;
