-- ==========================================================================================
-- Hospital H168 - implant stability by protocol: every stability measurement with its date and moment, the clinic's
-- approved loading protocol, and on the implant how it was loaded, what was fitted, and any documented exception.
--
--   HospitalImplantMeasures   (new)
--   HospitalImplantProtocols  (new, one per company)
--   HospitalImplants          + LoadingKind, Restoration, ExceptionReason, ExceptionByEmployeeId (nullable)
--
-- Nothing existing is changed or removed. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalImplants','U') IS NULL
    THROW 51987, 'Run hospital_h17_dental_cases.sql first.', 1;

IF OBJECT_ID('dbo.HospitalImplantMeasures','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalImplantMeasures (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ImplantId int NOT NULL,
        PatientId int NOT NULL,
        MeasuredOn date NOT NULL,
        Moment nvarchar(15) NOT NULL,
        Isq int NULL,
        TorqueNcm int NULL,
        Note nvarchar(300) NULL,
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalImplantMeasures PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalImplantMeasures_Implant FOREIGN KEY (ImplantId) REFERENCES dbo.HospitalImplants (ID),
        CONSTRAINT CK_HospitalImplantMeasures_Moment CHECK (Moment IN ('Placement','BeforeLoading','FollowUp')),
        CONSTRAINT CK_HospitalImplantMeasures_Isq CHECK (Isq IS NULL OR Isq BETWEEN 1 AND 100),
        CONSTRAINT CK_HospitalImplantMeasures_Torque CHECK (TorqueNcm IS NULL OR TorqueNcm BETWEEN 1 AND 150)
    );
    CREATE INDEX IX_HospitalImplantMeasures_CompanyId_ImplantId ON dbo.HospitalImplantMeasures (CompanyId, ImplantId);
    CREATE INDEX IX_HospitalImplantMeasures_CompanyId_PatientId ON dbo.HospitalImplantMeasures (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalImplantProtocols','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalImplantProtocols (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        MinIsqImmediate int NULL,
        MinIsqEarly int NULL,
        MinIsqConventional int NULL,
        MinTorqueImmediate int NULL,
        Reference nvarchar(300) NOT NULL,
        ApprovedByEmployeeId int NOT NULL,
        ApprovedAt datetime2 NOT NULL,
        CONSTRAINT PK_HospitalImplantProtocols PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalImplantProtocols_CompanyId ON dbo.HospitalImplantProtocols (CompanyId);
END

IF COL_LENGTH('dbo.HospitalImplants', 'LoadingKind') IS NULL
    ALTER TABLE dbo.HospitalImplants ADD LoadingKind nvarchar(15) NULL;
IF COL_LENGTH('dbo.HospitalImplants', 'Restoration') IS NULL
    ALTER TABLE dbo.HospitalImplants ADD Restoration nvarchar(15) NULL;
IF COL_LENGTH('dbo.HospitalImplants', 'ExceptionReason') IS NULL
    ALTER TABLE dbo.HospitalImplants ADD ExceptionReason nvarchar(300) NULL;
IF COL_LENGTH('dbo.HospitalImplants', 'ExceptionByEmployeeId') IS NULL
    ALTER TABLE dbo.HospitalImplants ADD ExceptionByEmployeeId int NULL;

COMMIT TRANSACTION;
