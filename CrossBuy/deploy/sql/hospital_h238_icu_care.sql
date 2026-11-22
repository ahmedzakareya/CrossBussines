-- ==========================================================================================
-- Hospital H238 - intensive-care depth: GCS and APACHE II scores, continuous infusions run to a protocol with every
-- titration, and devices (ventilator, central line, urinary catheter) with their daily care-bundle check.
--
--   HospitalIcuScores        (new)
--   HospitalIcuInfusions     (new)
--   HospitalIcuTitrations    (new)
--   HospitalIcuDevices       (new)
--   HospitalIcuBundleChecks  (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
    THROW 52038, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalIcuScores','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalIcuScores (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AdmissionId int NOT NULL,
        PatientId int NOT NULL,
        Kind nvarchar(10) NOT NULL,
        At datetime2 NOT NULL,
        Total int NOT NULL,
        Detail nvarchar(400) NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalIcuScores PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalIcuScores_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions (ID),
        CONSTRAINT FK_HospitalIcuScores_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalIcuScores_Kind CHECK (Kind IN ('GCS','APACHE2')),
        CONSTRAINT CK_HospitalIcuScores_Total CHECK ((Kind = 'GCS' AND Total BETWEEN 3 AND 15) OR (Kind = 'APACHE2' AND Total BETWEEN 0 AND 71))
    );
    CREATE INDEX IX_HospitalIcuScores_CompanyId_AdmissionId_At ON dbo.HospitalIcuScores (CompanyId, AdmissionId, At);
    CREATE INDEX IX_HospitalIcuScores_CompanyId_PatientId ON dbo.HospitalIcuScores (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalIcuInfusions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalIcuInfusions (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AdmissionId int NOT NULL,
        PatientId int NOT NULL,
        Drug nvarchar(100) NOT NULL,
        DoseUnit nvarchar(20) NOT NULL,
        ConcPerMl decimal(12,4) NULL,
        WeightKg decimal(5,1) NULL,
        MinDose decimal(12,4) NOT NULL,
        MaxDose decimal(12,4) NOT NULL,
        MaxStep decimal(12,4) NOT NULL,
        Target nvarchar(100) NOT NULL,
        Dose decimal(12,4) NOT NULL,
        StartedAt datetime2 NOT NULL,
        StoppedAt datetime2 NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalIcuInfusions PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalIcuInfusions_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions (ID),
        CONSTRAINT FK_HospitalIcuInfusions_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalIcuInfusions_Range CHECK (MinDose >= 0 AND MaxDose > MinDose AND MaxStep > 0 AND Dose BETWEEN MinDose AND MaxDose)
    );
    CREATE INDEX IX_HospitalIcuInfusions_CompanyId_AdmissionId ON dbo.HospitalIcuInfusions (CompanyId, AdmissionId);
    CREATE INDEX IX_HospitalIcuInfusions_CompanyId_PatientId ON dbo.HospitalIcuInfusions (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalIcuTitrations','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalIcuTitrations (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        InfusionId int NOT NULL,
        At datetime2 NOT NULL,
        FromDose decimal(12,4) NOT NULL,
        ToDose decimal(12,4) NOT NULL,
        Reason nvarchar(200) NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalIcuTitrations PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalIcuTitrations_Infusion FOREIGN KEY (InfusionId) REFERENCES dbo.HospitalIcuInfusions (ID)
    );
    CREATE INDEX IX_HospitalIcuTitrations_CompanyId_InfusionId_At ON dbo.HospitalIcuTitrations (CompanyId, InfusionId, At);
END

IF OBJECT_ID('dbo.HospitalIcuDevices','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalIcuDevices (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AdmissionId int NOT NULL,
        PatientId int NOT NULL,
        Kind nvarchar(15) NOT NULL,
        Site nvarchar(60) NULL,
        InsertedAt datetime2 NOT NULL,
        RemovedAt datetime2 NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalIcuDevices PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalIcuDevices_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions (ID),
        CONSTRAINT FK_HospitalIcuDevices_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalIcuDevices_Kind CHECK (Kind IN ('Ventilator','CentralLine','Catheter'))
    );
    CREATE INDEX IX_HospitalIcuDevices_CompanyId_AdmissionId ON dbo.HospitalIcuDevices (CompanyId, AdmissionId);
    CREATE INDEX IX_HospitalIcuDevices_CompanyId_PatientId ON dbo.HospitalIcuDevices (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalIcuBundleChecks','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalIcuBundleChecks (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        DeviceId int NOT NULL,
        Day date NOT NULL,
        Items nvarchar(200) NOT NULL,
        Complete bit NOT NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalIcuBundleChecks PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalIcuBundleChecks_Device FOREIGN KEY (DeviceId) REFERENCES dbo.HospitalIcuDevices (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalIcuBundleChecks_CompanyId_DeviceId_Day ON dbo.HospitalIcuBundleChecks (CompanyId, DeviceId, Day);
END

COMMIT TRANSACTION;
