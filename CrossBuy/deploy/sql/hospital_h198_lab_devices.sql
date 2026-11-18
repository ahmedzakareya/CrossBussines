-- ==========================================================================================
-- Hospital H198 - results from the laboratory's analysers (HL7 v2 ORU^R01).
--
--   HospitalDeviceMessages   (new) every message received, as it came, with what became of it
--   HospitalDeviceCodeMaps   (new) a device's own code linked to an analyte of the catalogue
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalLabOrders','U') IS NULL OR OBJECT_ID('dbo.HospitalLabAnalytes','U') IS NULL
    THROW 51998, 'Run the laboratory slice (hospital_h8_lab.sql) first.', 1;

IF OBJECT_ID('dbo.HospitalDeviceMessages','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDeviceMessages (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Device nvarchar(60) NOT NULL,
        MessageId nvarchar(60) NOT NULL,
        AccessionNo nvarchar(30) NULL,
        LabOrderId int NULL,
        Status nvarchar(12) NOT NULL,
        Applied int NOT NULL,
        Detail nvarchar(500) NULL,
        Raw nvarchar(max) NOT NULL,
        ReceivedAt datetime2 NOT NULL,
        ReceivedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalDeviceMessages PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDeviceMessages_Order FOREIGN KEY (LabOrderId) REFERENCES dbo.HospitalLabOrders (ID),
        CONSTRAINT CK_HospitalDeviceMessages_Status CHECK (Status IN ('Accepted', 'Partial', 'Refused', 'Duplicate'))
    );
    CREATE INDEX IX_HospitalDeviceMessages_CompanyId_Device_MessageId ON dbo.HospitalDeviceMessages (CompanyId, Device, MessageId);
    CREATE INDEX IX_HospitalDeviceMessages_CompanyId_ReceivedAt ON dbo.HospitalDeviceMessages (CompanyId, ReceivedAt);
END

IF OBJECT_ID('dbo.HospitalDeviceCodeMaps','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDeviceCodeMaps (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Device nvarchar(60) NOT NULL,
        DeviceCode nvarchar(40) NOT NULL,
        AnalyteId int NOT NULL,
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalDeviceCodeMaps PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDeviceCodeMaps_Analyte FOREIGN KEY (AnalyteId) REFERENCES dbo.HospitalLabAnalytes (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDeviceCodeMaps_CompanyId_Device_DeviceCode ON dbo.HospitalDeviceCodeMaps (CompanyId, Device, DeviceCode);
END

COMMIT TRANSACTION;
