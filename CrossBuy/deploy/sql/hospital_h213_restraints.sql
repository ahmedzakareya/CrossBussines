-- ==========================================================================================
-- Hospital H213 - restraint and seclusion: the doctor's time-limited order (reason, alternatives tried), the nurses'
-- checks at its interval, renewals and the end.
--
--   HospitalRestraints        (new) one per episode
--   HospitalRestraintChecks   (new) each check: circulation, skin, fluids, toilet, behaviour, still needed
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
    THROW 52013, 'Run the inpatient slices first.', 1;

IF OBJECT_ID('dbo.HospitalRestraints','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRestraints (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PatientId int NOT NULL,
        AdmissionId int NOT NULL,
        Kind nvarchar(10) NOT NULL,
        Device nvarchar(100) NULL,
        Reason nvarchar(500) NOT NULL,
        Alternatives nvarchar(500) NOT NULL,
        FamilyInformed bit NOT NULL,
        CheckEveryMinutes int NOT NULL,
        StartedAt datetime2 NOT NULL,
        ExpiresAt datetime2 NOT NULL,
        Renewals int NOT NULL,
        OrderedByEmployeeId int NULL,
        EndedAt datetime2 NULL,
        EndReason nvarchar(300) NULL,
        EndedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalRestraints PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRestraints_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT FK_HospitalRestraints_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions (ID),
        CONSTRAINT CK_HospitalRestraints_Kind CHECK (Kind IN ('Physical', 'Chemical', 'Seclusion')),
        CONSTRAINT CK_HospitalRestraints_Interval CHECK (CheckEveryMinutes BETWEEN 15 AND 240),
        CONSTRAINT CK_HospitalRestraints_End CHECK (EndedAt IS NULL OR EndReason IS NOT NULL)
    );
    CREATE INDEX IX_HospitalRestraints_CompanyId_EndedAt ON dbo.HospitalRestraints (CompanyId, EndedAt);
    CREATE INDEX IX_HospitalRestraints_CompanyId_PatientId ON dbo.HospitalRestraints (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalRestraintChecks','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRestraintChecks (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        RestraintId int NOT NULL,
        PatientId int NOT NULL,
        At datetime2 NOT NULL,
        CirculationOk bit NOT NULL,
        SkinOk bit NOT NULL,
        FluidsOffered bit NOT NULL,
        ToiletOffered bit NOT NULL,
        StillNeeded bit NOT NULL,
        Behaviour nvarchar(300) NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalRestraintChecks PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRestraintChecks_Restraint FOREIGN KEY (RestraintId) REFERENCES dbo.HospitalRestraints (ID),
        CONSTRAINT FK_HospitalRestraintChecks_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID)
    );
    CREATE INDEX IX_HospitalRestraintChecks_CompanyId_RestraintId_At ON dbo.HospitalRestraintChecks (CompanyId, RestraintId, At);
END

COMMIT TRANSACTION;
