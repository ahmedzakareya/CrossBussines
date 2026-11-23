-- ==========================================================================================
-- Hospital H247 - outbound HL7 v2 feeds (ADT, ORU) entered by the administrator, off until enabled, and every
-- message queued once per event with its acknowledgement.
--
--   HospitalOutboundFeeds      (new)
--   HospitalOutboundMessages   (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52047, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalOutboundFeeds','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalOutboundFeeds (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Code nvarchar(20) NOT NULL,
        Name nvarchar(100) NOT NULL,
        Kind nvarchar(5) NOT NULL,
        Host nvarchar(200) NULL,
        Port int NULL,
        ReceivingApp nvarchar(60) NULL,
        ReceivingFacility nvarchar(60) NULL,
        Enabled bit NOT NULL,
        StartFrom datetime2 NOT NULL,
        UpdatedAt datetime2 NOT NULL,
        CONSTRAINT PK_HospitalOutboundFeeds PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalOutboundFeeds_Kind CHECK (Kind IN ('ADT','ORU'))
    );
    CREATE UNIQUE INDEX IX_HospitalOutboundFeeds_CompanyId_Code ON dbo.HospitalOutboundFeeds (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalOutboundMessages','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalOutboundMessages (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        FeedId int NOT NULL,
        EventKey nvarchar(40) NOT NULL,
        MessageType nvarchar(15) NOT NULL,
        PatientId int NOT NULL,
        ControlId nvarchar(30) NOT NULL,
        Raw nvarchar(max) NOT NULL,
        Status nvarchar(8) NOT NULL,
        Attempts int NOT NULL,
        AckCode nvarchar(4) NULL,
        LastError nvarchar(300) NULL,
        EventAt datetime2 NOT NULL,
        CreatedAt datetime2 NOT NULL,
        SentAt datetime2 NULL,
        CONSTRAINT PK_HospitalOutboundMessages PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalOutboundMessages_Feed FOREIGN KEY (FeedId) REFERENCES dbo.HospitalOutboundFeeds (ID),
        CONSTRAINT FK_HospitalOutboundMessages_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalOutboundMessages_Status CHECK (Status IN ('Queued','Sent','Error'))
    );
    CREATE UNIQUE INDEX IX_HospitalOutboundMessages_CompanyId_FeedId_EventKey ON dbo.HospitalOutboundMessages (CompanyId, FeedId, EventKey);
    CREATE INDEX IX_HospitalOutboundMessages_CompanyId_Status ON dbo.HospitalOutboundMessages (CompanyId, Status);
    CREATE INDEX IX_HospitalOutboundMessages_CompanyId_PatientId ON dbo.HospitalOutboundMessages (CompanyId, PatientId);
END

COMMIT TRANSACTION;
