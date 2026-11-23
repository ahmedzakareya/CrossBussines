-- ==========================================================================================
-- Hospital H244 - the operating theatre's preference cards (a surgeon's way of doing a procedure) and block time
-- (a theatre reserved on a weekday for a surgeon or a service line).
--
--   HospitalPreferenceCards   (new)
--   HospitalTheatreBlocks     (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalSurgeries','U') IS NULL
    THROW 52044, 'Run the hospital theatre slice first.', 1;

IF OBJECT_ID('dbo.HospitalPreferenceCards','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPreferenceCards (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        SurgeonId int NOT NULL,
        ServiceId int NOT NULL,
        Position nvarchar(200) NULL,
        SkinPrep nvarchar(200) NULL,
        Gloves nvarchar(100) NULL,
        Instruments nvarchar(1000) NULL,
        Equipment nvarchar(500) NULL,
        Sutures nvarchar(500) NULL,
        Notes nvarchar(1000) NULL,
        UpdatedAt datetime2 NOT NULL,
        UpdatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalPreferenceCards PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalPreferenceCards_CompanyId_SurgeonId_ServiceId ON dbo.HospitalPreferenceCards (CompanyId, SurgeonId, ServiceId);
END

IF OBJECT_ID('dbo.HospitalTheatreBlocks','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalTheatreBlocks (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        RoomId int NOT NULL,
        Weekday int NOT NULL,
        StartMinute int NOT NULL,
        EndMinute int NOT NULL,
        SurgeonId int NULL,
        Label nvarchar(100) NULL,
        IsActive bit NOT NULL,
        UpdatedAt datetime2 NOT NULL,
        CONSTRAINT PK_HospitalTheatreBlocks PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalTheatreBlocks_Window CHECK (Weekday BETWEEN 0 AND 6 AND StartMinute >= 0 AND EndMinute <= 1440 AND EndMinute > StartMinute)
    );
    CREATE INDEX IX_HospitalTheatreBlocks_CompanyId_RoomId_Weekday ON dbo.HospitalTheatreBlocks (CompanyId, RoomId, Weekday);
END

COMMIT TRANSACTION;
