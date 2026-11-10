-- ==========================================================================================
-- Hospital H42 — central sterile supply (the prototype's #/cssd, "Instrument-set tracking"). An instrument set goes
-- round: awaiting wash → washed and packed → in the autoclave (a cycle, passed or failed) → sterile until its shelf
-- life ends → in use on a theatre case → awaiting wash again. Every step is a set event, so the set used on a case
-- names the cycle that sterilised it.
--   HospitalSterilisationCycles  one row per autoclave run
--   HospitalInstrumentSets       one row per set, with where it is now
--   HospitalSetEvents            one row per step a set takes
--
-- New tables only; nothing existing changes. Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalSurgeries','U') IS NULL
    THROW 51990, 'Run hospital_h29_theatre.sql first.', 1;

IF OBJECT_ID('dbo.HospitalSterilisationCycles','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSterilisationCycles (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        CycleNo              nvarchar(20)   NOT NULL,
        Autoclave            nvarchar(60)   NOT NULL,
        Result               nvarchar(10)   NOT NULL,
        Note                 nvarchar(300)  NULL,
        StartedAt            datetime2      NOT NULL,
        StartedByEmployeeId  int            NULL,
        EndedAt              datetime2      NULL,
        EndedByEmployeeId    int            NULL,
        CONSTRAINT PK_HospitalSterilisationCycles PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalSterilisationCycles_Result CHECK (Result IN (N'Running', N'Passed', N'Failed'))
    );
    CREATE UNIQUE INDEX IX_HospitalSterilisationCycles_CompanyId_CycleNo ON dbo.HospitalSterilisationCycles (CompanyId, CycleNo);
END

IF OBJECT_ID('dbo.HospitalInstrumentSets','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalInstrumentSets (
        ID             int IDENTITY(1,1) NOT NULL,
        CompanyId      int            NOT NULL,
        Code           nvarchar(30)   NOT NULL,
        Name           nvarchar(150)  NOT NULL,
        NameEn         nvarchar(150)  NULL,
        ShelfLifeDays  int            NOT NULL,
        Status         nvarchar(20)   NOT NULL,
        SterileUntil   date           NULL,
        CycleId        int            NULL,
        SurgeryId      int            NULL,
        IsActive       bit            NOT NULL,
        CreatedAt      datetime2      NOT NULL,
        CONSTRAINT PK_HospitalInstrumentSets PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalInstrumentSets_Cycle FOREIGN KEY (CycleId) REFERENCES dbo.HospitalSterilisationCycles(ID),
        CONSTRAINT FK_HospitalInstrumentSets_Surgery FOREIGN KEY (SurgeryId) REFERENCES dbo.HospitalSurgeries(ID),
        CONSTRAINT CK_HospitalInstrumentSets_Status CHECK (Status IN (N'AwaitingWash', N'Packed', N'InAutoclave', N'Sterile', N'InUse')),
        CONSTRAINT CK_HospitalInstrumentSets_ShelfLife CHECK (ShelfLifeDays BETWEEN 1 AND 365),
        -- a sterile set says until when; one in use says on which case
        CONSTRAINT CK_HospitalInstrumentSets_Sterile CHECK (Status <> N'Sterile' OR SterileUntil IS NOT NULL),
        CONSTRAINT CK_HospitalInstrumentSets_InUse CHECK (Status <> N'InUse' OR SurgeryId IS NOT NULL)
    );
    CREATE UNIQUE INDEX IX_HospitalInstrumentSets_CompanyId_Code ON dbo.HospitalInstrumentSets (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalSetEvents','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSetEvents (
        ID            int IDENTITY(1,1) NOT NULL,
        CompanyId     int            NOT NULL,
        SetId         int            NOT NULL,
        Kind          nvarchar(20)   NOT NULL,
        CycleId       int            NULL,
        SurgeryId     int            NULL,
        At            datetime2      NOT NULL,
        ByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalSetEvents PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalSetEvents_Set FOREIGN KEY (SetId) REFERENCES dbo.HospitalInstrumentSets(ID),
        CONSTRAINT FK_HospitalSetEvents_Cycle FOREIGN KEY (CycleId) REFERENCES dbo.HospitalSterilisationCycles(ID),
        CONSTRAINT FK_HospitalSetEvents_Surgery FOREIGN KEY (SurgeryId) REFERENCES dbo.HospitalSurgeries(ID),
        CONSTRAINT CK_HospitalSetEvents_Kind CHECK (Kind IN (N'Washed', N'Loaded', N'Sterilised', N'CycleFailed', N'Issued', N'Returned', N'Reprocess'))
    );
    CREATE INDEX IX_HospitalSetEvents_SetId_At ON dbo.HospitalSetEvents (SetId, At);
END

COMMIT TRANSACTION;
