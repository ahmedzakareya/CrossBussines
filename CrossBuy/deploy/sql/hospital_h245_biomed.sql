-- ==========================================================================================
-- Hospital H245 - biomedical engineering over the fixed assets: each medical device's register entry (room,
-- category, risk class, serial, status) and its events (out of service, back in service, safety test, recall).
-- Maintenance schedules and records stay the asset module's (MaintenanceSchedules / MaintenanceRecords).
--
--   HospitalBiomedDevices   (new)
--   HospitalBiomedEvents    (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.FixedAssets','U') IS NULL
    THROW 52045, 'The fixed-asset tables are missing.', 1;

IF OBJECT_ID('dbo.HospitalBiomedDevices','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBiomedDevices (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AssetId int NOT NULL,
        RoomId int NULL,
        Category nvarchar(15) NOT NULL,
        RiskClass nvarchar(8) NOT NULL,
        SerialNo nvarchar(60) NULL,
        Manufacturer nvarchar(100) NULL,
        ModelName nvarchar(100) NULL,
        Status nvarchar(10) NOT NULL,
        DownSince datetime2 NULL,
        UpdatedAt datetime2 NOT NULL,
        CONSTRAINT PK_HospitalBiomedDevices PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalBiomedDevices_Asset FOREIGN KEY (AssetId) REFERENCES dbo.FixedAssets (ID),
        CONSTRAINT CK_HospitalBiomedDevices_Risk CHECK (RiskClass IN ('High','Medium','Low')),
        CONSTRAINT CK_HospitalBiomedDevices_Status CHECK (Status IN ('InService','Down','Retired'))
    );
    CREATE UNIQUE INDEX IX_HospitalBiomedDevices_CompanyId_AssetId ON dbo.HospitalBiomedDevices (CompanyId, AssetId);
END

IF OBJECT_ID('dbo.HospitalBiomedEvents','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBiomedEvents (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        DeviceId int NOT NULL,
        Kind nvarchar(12) NOT NULL,
        At datetime2 NOT NULL,
        Passed bit NULL,
        Detail nvarchar(300) NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalBiomedEvents PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalBiomedEvents_Device FOREIGN KEY (DeviceId) REFERENCES dbo.HospitalBiomedDevices (ID),
        CONSTRAINT CK_HospitalBiomedEvents_Kind CHECK (Kind IN ('Down','Up','SafetyTest','Recall'))
    );
    CREATE INDEX IX_HospitalBiomedEvents_CompanyId_DeviceId_At ON dbo.HospitalBiomedEvents (CompanyId, DeviceId, At);
END

COMMIT TRANSACTION;
