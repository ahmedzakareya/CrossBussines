-- ==========================================================================================
-- Hospital H237 - time-critical emergency pathways (sepsis, stroke, STEMI, trauma) with their timed steps, and the
-- ambulance pre-arrival that opens the emergency visit with its pathway already running.
--
--   HospitalErPathways       (new)
--   HospitalErPathwaySteps   (new)
--   HospitalErPreArrivals    (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalErVisits','U') IS NULL
    THROW 52037, 'Run the hospital emergency slice first.', 1;

IF OBJECT_ID('dbo.HospitalErPathways','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalErPathways (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ErVisitId int NOT NULL,
        PatientId int NULL,
        Kind nvarchar(10) NOT NULL,
        ActivatedAt datetime2 NOT NULL,
        ActivatedByEmployeeId int NULL,
        ClosedAt datetime2 NULL,
        Note nvarchar(300) NULL,
        CONSTRAINT PK_HospitalErPathways PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalErPathways_Visit FOREIGN KEY (ErVisitId) REFERENCES dbo.HospitalErVisits (ID),
        CONSTRAINT FK_HospitalErPathways_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalErPathways_Kind CHECK (Kind IN ('Sepsis','Stroke','Stemi','Trauma'))
    );
    CREATE UNIQUE INDEX IX_HospitalErPathways_CompanyId_ErVisitId_Kind ON dbo.HospitalErPathways (CompanyId, ErVisitId, Kind);
    CREATE INDEX IX_HospitalErPathways_CompanyId_PatientId ON dbo.HospitalErPathways (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalErPathwaySteps','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalErPathwaySteps (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PathwayId int NOT NULL,
        Code nvarchar(20) NOT NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        NotIndicated bit NOT NULL CONSTRAINT DF_HospitalErPathwaySteps_NotIndicated DEFAULT (0),
        Note nvarchar(300) NULL,
        CONSTRAINT PK_HospitalErPathwaySteps PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalErPathwaySteps_Pathway FOREIGN KEY (PathwayId) REFERENCES dbo.HospitalErPathways (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalErPathwaySteps_CompanyId_PathwayId_Code ON dbo.HospitalErPathwaySteps (CompanyId, PathwayId, Code);
END

IF OBJECT_ID('dbo.HospitalErPreArrivals','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalErPreArrivals (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        ExpectedAt datetime2 NOT NULL,
        Unit nvarchar(60) NULL,
        Complaint nvarchar(300) NOT NULL,
        AgeText nvarchar(20) NULL,
        Sex nvarchar(1) NULL,
        EsiEstimate int NULL,
        Pathway nvarchar(10) NULL,
        Status nvarchar(10) NOT NULL,
        ErVisitId int NULL,
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalErPreArrivals PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalErPreArrivals_Status CHECK (Status IN ('Expected','Arrived','Cancelled')),
        CONSTRAINT CK_HospitalErPreArrivals_Esi CHECK (EsiEstimate IS NULL OR EsiEstimate BETWEEN 1 AND 5)
    );
    CREATE INDEX IX_HospitalErPreArrivals_CompanyId_Status_ExpectedAt ON dbo.HospitalErPreArrivals (CompanyId, Status, ExpectedAt);
END

COMMIT TRANSACTION;
