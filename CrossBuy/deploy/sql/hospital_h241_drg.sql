-- ==========================================================================================
-- Hospital H241 - DRG grouping: the facility's group table (entered by the administrator from the payer's or the
-- ministry's grouper) and the coded discharged stays with their frozen weight.
--
--   HospitalDrgs             (new)
--   HospitalDrgAssignments   (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
    THROW 52041, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalDrgs','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDrgs (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Code nvarchar(20) NOT NULL,
        Name nvarchar(200) NOT NULL,
        NameEn nvarchar(200) NULL,
        DxPrefixes nvarchar(500) NOT NULL,
        Surgical bit NULL,
        MinAge int NULL,
        MaxAge int NULL,
        Weight decimal(8,4) NOT NULL,
        MeanLos decimal(5,1) NULL,
        IsActive bit NOT NULL,
        UpdatedAt datetime2 NOT NULL,
        CONSTRAINT PK_HospitalDrgs PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalDrgs_Weight CHECK (Weight > 0)
    );
    CREATE UNIQUE INDEX IX_HospitalDrgs_CompanyId_Code ON dbo.HospitalDrgs (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalDrgAssignments','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDrgAssignments (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AdmissionId int NOT NULL,
        PatientId int NOT NULL,
        PrincipalDx nvarchar(10) NOT NULL,
        Surgical bit NOT NULL,
        DrgId int NULL,
        Weight decimal(8,4) NOT NULL,
        LosDays int NOT NULL,
        Manual bit NOT NULL,
        AssignedAt datetime2 NOT NULL,
        AssignedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalDrgAssignments PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDrgAssignments_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions (ID),
        CONSTRAINT FK_HospitalDrgAssignments_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT FK_HospitalDrgAssignments_Drg FOREIGN KEY (DrgId) REFERENCES dbo.HospitalDrgs (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDrgAssignments_CompanyId_AdmissionId ON dbo.HospitalDrgAssignments (CompanyId, AdmissionId);
    CREATE INDEX IX_HospitalDrgAssignments_CompanyId_PatientId ON dbo.HospitalDrgAssignments (CompanyId, PatientId);
END

COMMIT TRANSACTION;
